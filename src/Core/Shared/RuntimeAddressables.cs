using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Serves objects made at runtime (a mod's car prefab and garage model) under Addressables keys the game loads by
/// name, without patching any of the game's loaders.
///
/// <para><b>Why.</b> The game loads car prefabs and garage models with
/// <c>ResourcesManager.LoadAndCacheAddressablesSync&lt;GameObject&gt;(address, tags)</c> (OverlandNetworkRunner.SetupCar /
/// SpawnPlayer: "Prefabs/Cars/Car_&lt;name&gt;"; StateMerchantMenu.RefreshDummyCar, the lobby and main-menu dummies:
/// "Prefabs/Cars/MerchantDummies/Car_&lt;name&gt;_Dummy"). That method never looks at ResourcesManager's own cache: it
/// calls <c>Addressables.LoadAssetAsync(address).WaitForCompletion()</c> straight away and then caches the result (a
/// null result throws in AddAddressableToCache). Only ResourcesManager.Load reads the cache. The method is generic
/// (one native body shared by every reference type), so it can't be patched safely.</para>
///
/// <para><b>How.</b> Addressables' LoadAssetAsync asks each resource locator for the key's locations, takes the first
/// whose provider can give the wanted type, and ResourceManager.ProvideResource returns the operation it has cached for
/// that (location, type) before ever asking the provider. So each served address gets:</para>
/// <list type="bullet">
/// <item>one location (resource type GameObject), in one extra ResourceLocationMap added with
///   Addressables.AddResourceLocator; the game's catalogs come first and never have these keys. Only GameObject
///   requests are served: every game loader asks for GameObject, and an Object request would not get past the
///   provider's type check (CanProvide: the location's type must be assignable from the requested one);</item>
/// <item>a completed operation holding the object, put into ResourceManager's operation cache under the key
///   ProvideResource computes. This class keeps one reference to it, so the game's releases never take it to
///   zero.</item>
/// </list>
/// <para>The provider id is a real one (LegacyResourcesProvider when the game has it, else BundledAssetProvider), so
/// GetResourceProvider finds a provider; it is never asked to load anything while the operation is cached.
/// <see cref="Ensure"/> puts back a locator or operation the game dropped (catalog reload, ClearOperationCache).
/// Main thread only.</para>
/// </summary>
public static class RuntimeAddressables
{
    private sealed class Entry
    {
        public string Address;
        public GameObject Asset;
        public IResourceLocation Location;
        public IAsyncOperation Op;
        public AsyncOperationHandle<GameObject> Handle;
    }

    private const string LocatorId = "OTHP.RuntimeAddressables";
    private static readonly Dictionary<string, Entry> Served = new(StringComparer.Ordinal);
    private static ResourceLocationMap _map;
    private static string _providerId;
    private static IResourceProvider _provider;
    private static bool _loggedEnsure;

    public static bool IsServed(string address) => address != null && Served.ContainsKey(address);

    /// <summary>Serves <paramref name="asset"/> under <paramref name="address"/>. Null on success, otherwise why not.</summary>
    public static string Serve(string address, GameObject asset)
    {
        if (string.IsNullOrEmpty(address) || asset == null) return "no address or object";
        if (Served.TryGetValue(address, out var have))
        {
            if (have.Asset != null && have.Asset.Pointer == asset.Pointer) return Ensure();
            return $"{address} is already served for another object";
        }
        try
        {
            var rm = Addressables.ResourceManager;
            if (rm == null) return "Addressables isn't ready";
            if (_provider == null && !FindProvider(rm)) return "no Addressables provider to name";
            var e = new Entry { Address = address, Asset = asset };
            e.Location = new ResourceLocationBase(address, address, _providerId, Il2CppType.Of<GameObject>(),
                new Il2CppReferenceArray<IResourceLocation>(0)).Cast<IResourceLocation>();
            _map ??= new ResourceLocationMap(LocatorId, 8);
            _map.Add((Il2CppSystem.String)address, e.Location);
            Served[address] = e;
            string why = Ensure();
            if (why != null) return why;
            return Resolves(address) ? null : $"Addressables still doesn't find {address}";
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeAddressables.Serve"); return "could not serve " + address + ": " + ex.Message; }
    }

    /// <summary>Puts back the locator and the cached operations when the game dropped them. Cheap when nothing is
    /// missing; null when all is in place, otherwise why not.</summary>
    public static string Ensure()
    {
        if (Served.Count == 0 || _map == null) return null;
        try
        {
            var rm = Addressables.ResourceManager;
            if (rm == null) return "Addressables isn't ready";
            if (!LocatorPresent()) Addressables.AddResourceLocator(_map.Cast<IResourceLocator>(), null, null);
            foreach (var e in Served.Values)
            {
                if (e.Asset == null) continue; // the object is gone: nothing to serve
                var key = rm.CreateCacheKeyForLocation(_provider, e.Location, Il2CppType.Of<GameObject>());
                if (e.Op != null && rm.IsOperationCached(key)) continue;
                // Put back only an operation that is still ours: ClearOperationCache leaves it alive, but one whose count
                // reached zero went back to Addressables' pool (the handle's version no longer matches) and may hold
                // someone else's result by now. Then a fresh one is made (held here, so releases never take it to zero).
                bool alive = false;
                try { alive = e.Op != null && e.Handle != null && e.Handle.IsValid(); } catch (Exception ex2) { DebugLog.Error("core", ex2, "RuntimeAddressables.Ensure"); alive = false; }
                if (!alive)
                {
                    e.Handle = rm.CreateCompletedOperation<GameObject>(e.Asset, null);
                    e.Op = e.Handle.m_InternalOp.Cast<IAsyncOperation>();
                }
                rm.AddOperationToCache(key, e.Op);
            }
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "RuntimeAddressables.Ensure");
            if (!_loggedEnsure)
            {
                _loggedEnsure = true;
                Core.CorePlugin.Logger?.LogWarning("[RuntimeAddressables] " + ex);
            }
            return ex.Message;
        }
    }

    /// <summary>Addressables finds a location for the address that a provider can give a GameObject for (the check
    /// LoadAssetAsync makes).</summary>
    public static bool Resolves(string address) => HasLocation(address, Il2CppType.Of<GameObject>());

    /// <summary>Some resource locator has a location for <paramref name="key"/> that a provider can give
    /// <paramref name="type"/> for: LoadAssetAsync would find it (asking first avoids its InvalidKeyException errors).</summary>
    public static bool HasLocation(string key, Il2CppSystem.Type type)
    {
        if (string.IsNullOrEmpty(key) || type == null) return false;
        try
        {
            Il2CppSystem.Collections.Generic.IList<IResourceLocation> locs = null;
            string address = key;
            foreach (var locator in Locators())
            {
                if (!locator.Locate((Il2CppSystem.String)address, type, out locs) || locs == null) continue;
                var rm = Addressables.ResourceManager;
                for (int i = 0; i < locs.Cast<Il2CppSystem.Collections.Generic.ICollection<IResourceLocation>>().Count; i++)
                    if (rm.GetResourceProvider(type, locs[i]) != null) return true;
            }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeAddressables.HasLocation"); Core.CorePlugin.Logger?.LogDebug("[RuntimeAddressables] resolve check: " + ex.Message); }
        return false;
    }

    /// <summary>Some resource locator knows <paramref name="key"/> at all (any type). A load of a key no locator knows
    /// only logs Addressables' InvalidKeyException; asking first keeps the log clean. Errs on "yes" when the check
    /// itself fails.</summary>
    public static bool HasKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            Il2CppSystem.Collections.Generic.IList<IResourceLocation> locs = null;
            foreach (var locator in Locators())
                if (locator.Locate((Il2CppSystem.String)key, null, out locs) && locs != null) return true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "RuntimeAddressables.HasKey");
            Core.CorePlugin.Logger?.LogDebug("[RuntimeAddressables] key check: " + ex.Message);
            return true;
        }
    }

    private static bool FindProvider(ResourceManager rm)
    {
        IResourceProvider bundled = null, any = null;
        var list = rm.ResourceProviders;
        int n = list.Cast<Il2CppSystem.Collections.Generic.ICollection<IResourceProvider>>().Count;
        for (int i = 0; i < n; i++)
        {
            var p = list[i];
            string id = p?.ProviderId;
            if (string.IsNullOrEmpty(id)) continue;
            any ??= p;
            if (id.EndsWith("LegacyResourcesProvider", StringComparison.Ordinal)) { _provider = p; _providerId = id; return true; }
            if (id.EndsWith("BundledAssetProvider", StringComparison.Ordinal)) bundled ??= p;
        }
        _provider = bundled ?? any;
        _providerId = _provider?.ProviderId;
        return _provider != null;
    }

    private static bool LocatorPresent()
    {
        foreach (var l in Locators())
            if (l.Pointer == _map.Pointer || l.LocatorId == LocatorId) return true;
        return false;
    }

    private static List<IResourceLocator> Locators()
    {
        var result = new List<IResourceLocator>();
        var en = Addressables.ResourceLocators?.GetEnumerator();
        if (en == null) return result;
        var plain = en.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (plain.MoveNext())
        {
            var l = en.Current;
            if (l != null) result.Add(l);
        }
        return result;
    }
}
