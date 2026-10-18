using System;
using System.Collections.Generic;
using OverTheHillPlus.Shared;
using VehiclePhysics;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CarSpawner;

/// <summary>
/// Click-to-place: after "Place" the page closes and a see-through box the size of the car follows where the camera aims on
/// the ground, lying on the surface (tilted to the slope). Mouse wheel or Q/E turns it; red = blocked (something solid inside
/// it, or too steep), green = clear; left click spawns the car there, right click or Esc cancels. The game's input is switched
/// to its console map while placing so the keys don't also drive the car. The footprint is measured from the car's prefab
/// meshes (a modded car's prefab is served through Addressables under the same address, so one path covers both).
/// </summary>
internal static class SpawnerPlace
{
    private static readonly Vector3 FallbackSize = new(2f, 1.6f, 4.5f);
    private static readonly Dictionary<string, Bounds> Sizes = new();
    private static readonly RaycastHit[] Hits = new RaycastHit[32];

    private static readonly string[] Shaders =
    {
        "Universal Render Pipeline/Unlit",
        "Universal Render Pipeline/Particles/Unlit",
        "Sprites/Default",
        "UI/Default",
        "Unlit/Color",
    };

    private static CarChoice _car;
    private static Bounds _size;
    private static float _yaw;
    private static int _startFrame;
    private static float _startTime;
    private static Il2CppSystem.Guid? _map;
    private static int _mapPushes;
    private static float _repushWindowAt;
    private static GameObject _box;
    private static Material _mat;
    private static bool _matTried;
    private static bool _hasSpot, _clear, _steep, _fallback;
    private static string _aimLogged;
    private static Vector3 _pos, _euler;
    private static float _fbAhead = 8f, _fbSide; // fallback spot, metres ahead of / beside the car (W/S and A/D or arrows)

    public static void Start(CarChoice car)
    {
        if (car == null) return;
        _car = car;
        _size = Measure(car.Prefab);
        _yaw = GameState.LocalCar != null ? GameState.LocalCar.transform.eulerAngles.y : 0f;
        _startFrame = Time.frameCount;
        _startTime = Time.unscaledTime;
        _hasSpot = false;
        _aimLogged = null;
        _fbAhead = 8f;
        _fbSide = 0f;
        _mapPushes = 0;
        _repushWindowAt = Time.unscaledTime;
        PushMap();
        Menu.CloseRequested = true;
        Menu.SetStatus($"Placing {car.Display}: left click spawns, right click or Esc cancels.");
        EnsureMaterial();
        DebugLog.Info(CarSpawnerPlugin.ModId, $"place: started {car.Display} ({car.Prefab}), size {_size.size.x:0.0}x{_size.size.y:0.0}x{_size.size.z:0.0} m, " +
                                              $"camera {(Camera.main != null ? Camera.main.name : "NONE")}, cursor visible {Cursor.visible} ({Cursor.lockState}), " +
                                              $"map {(_map != null ? "pushed" : "FAILED")}, material {(_mat != null ? _mat.shader.name : "none (outline only)")}");
    }

    private static void PushMap()
    {
        try { _map = GameInput.PushMap(GameInput.Map.Terminal, 900); _mapPushes++; }
        catch (Exception e) { _map = null; DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.PushMap"); }
    }

    public static void Tick()
    {
        if (_car == null) return;
        try
        {
            if (SpawnerCars.BlockReason() != null) { Stop("not in a world any more"); return; }
            // The MODS page should be gone by now (Start asked it to close); if it is not, Place can't work.
            if (Time.unscaledTime > _startTime + 3f && Time.frameCount > _startFrame + 10 && ModsPage.IsOpen)
            {
                Menu.SetStatus("Place: close the MODS page first.");
                Stop("the MODS page is still open");
                return;
            }
            // The game rebuilds its input stack when it returns to driving (as SpectateCamera found): put our map back,
            // but give up if it keeps being thrown out.
            if (_map != null && !MapActive())
            {
                if (Time.unscaledTime - _repushWindowAt > 10f) { _repushWindowAt = Time.unscaledTime; _mapPushes = 0; }
                DebugLog.Info(CarSpawnerPlugin.ModId, "place: the game dropped the input map, pushing it again");
                _map = null;
                if (_mapPushes < 5) PushMap();
                else { Stop("the game keeps resetting the controls"); return; }
            }
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            bool ready = Time.frameCount > _startFrame + 2; // the click on the page's button must not also place
            if (ready && ((kb != null && kb.escapeKey.wasPressedThisFrame) || (mouse != null && mouse.rightButton.wasPressedThisFrame)))
            {
                Menu.SetStatus("Placement cancelled.");
                Stop("cancelled by the player");
                return;
            }
            float dt = Time.unscaledDeltaTime;
            if (kb != null)
            {
                if (kb.qKey.isPressed) _yaw = SpawnerLogic.TurnYaw(_yaw, -120f * dt);
                if (kb.eKey.isPressed) _yaw = SpawnerLogic.TurnYaw(_yaw, 120f * dt);
                // Only used while nothing under the aim is ground (the fallback spot).
                float speed = (kb.leftShiftKey.isPressed ? 20f : 6f) * dt;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) _fbAhead += speed;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) _fbAhead -= speed;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) _fbSide += speed;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) _fbSide -= speed;
                _fbAhead = Mathf.Clamp(_fbAhead, 2f, 80f);
                _fbSide = Mathf.Clamp(_fbSide, -60f, 60f);
            }
            if (mouse != null)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel != 0f) _yaw = SpawnerLogic.TurnYaw(_yaw, Mathf.Sign(wheel) * 15f);
            }
            Aim(mouse);
            Draw();
            if (ready && mouse != null && mouse.leftButton.wasPressedThisFrame && _hasSpot)
            {
                if (!_clear)
                {
                    Menu.SetStatus(_steep ? "That spot is too steep." : "That spot is blocked.");
                    return;
                }
                var car = _car;
                Stop("spawning");
                Menu.SetStatus(SpawnerCars.SpawnPlaced(car, _pos, _euler));
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.Tick");
            Stop("error");
        }
    }

    private static bool MapActive()
    {
        try { return GameInput.IsMapActive(GameInput.Map.Terminal); }
        catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerPlace.MapActive"); return true; }
    }

    /// <summary>
    /// The aim: a ray through the mouse when the cursor is free, else through the screen centre. When that finds no ground
    /// (the chase camera looks at the sky or only at the player's car), the spot is ahead of the player's car instead,
    /// moved with W/S/A/D (or the arrows), so a box is always shown.
    /// </summary>
    private static void Aim(Mouse mouse)
    {
        _hasSpot = false;
        var cam = Camera.main;
        bool hit = false;
        Vector3 point = default, normal = Vector3.up;
        Collider ground = null;
        string what = null;
        if (cam != null)
        {
            bool free = Cursor.visible && Cursor.lockState != CursorLockMode.Locked && mouse != null;
            var ray = free
                ? cam.ScreenPointToRay(mouse.position.ReadValue())
                : cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            hit = Cast(ray, 300f, out point, out normal, out ground);
            what = free ? "mouse" : "screen centre";
        }
        _fallback = !hit;
        if (!hit)
        {
            var me = GameState.LocalCar;
            if (me == null) return;
            var t = me.cachedTransform != null ? me.cachedTransform : me.transform;
            var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            var at = t.position + fwd * _fbAhead + right * _fbSide;
            if (!Cast(new Ray(at + Vector3.up * 40f, Vector3.down), 120f, out point, out normal, out ground))
            {
                point = new Vector3(at.x, t.position.y - 0.5f, at.z);
                normal = Vector3.up;
                ground = null;
            }
            what = "fallback ahead of the car";
        }
        if (_aimLogged != what)
        {
            _aimLogged = what;
            DebugLog.Info(CarSpawnerPlugin.ModId, $"place: aim = {what}" + (ground != null ? $", ground {ground.name}" : ", no ground found") + $" at {point.x:0},{point.y:0},{point.z:0}");
        }
        var rot = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, _yaw, 0f);
        _pos = point + normal * (-_size.min.y + 0.05f);
        _euler = rot.eulerAngles;
        _hasSpot = true;
        _steep = SpawnerLogic.TooSteep(normal.y);
        _clear = !_steep && !Blocked(rot, ground);
    }

    /// <summary>Nearest solid hit that is not a car (aim through cars).</summary>
    private static bool Cast(Ray ray, float range, out Vector3 point, out Vector3 normal, out Collider ground)
    {
        point = default; normal = Vector3.up; ground = null;
        int n = Physics.RaycastNonAlloc(ray, Hits, range, ~0, QueryTriggerInteraction.Ignore);
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            var c = Hits[i].collider;
            if (c == null || c.GetComponentInParent<VehicleBase>() != null) continue;
            if (best < 0 || Hits[i].distance < Hits[best].distance) best = i;
        }
        if (best < 0) return false;
        point = Hits[best].point;
        normal = Hits[best].normal;
        ground = Hits[best].collider;
        return true;
    }

    private static bool Blocked(Quaternion rot, Collider ground)
    {
        // Lifted a little and shrunk, so the ground the box rests on isn't "something in the way".
        var half = Vector3.Max(_size.extents - new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0.1f, 0.1f, 0.1f));
        var centre = _pos + rot * _size.center + rot * Vector3.up * 0.2f;
        foreach (var c in Physics.OverlapBox(centre, half, rot, ~0, QueryTriggerInteraction.Ignore))
        {
            if (c == null || (ground != null && c.Pointer == ground.Pointer) || c.TryCast<TerrainCollider>() != null) continue;
            return true;
        }
        return false;
    }

    private static Color Tint(float a) => _clear ? new Color(0.1f, 0.9f, 0.2f, a) : new Color(0.95f, 0.15f, 0.1f, a);

    private static void Draw()
    {
        if (_box == null) Build();
        if (_box == null) return;
        _box.SetActive(_hasSpot);
        if (!_hasSpot) return;
        _box.transform.SetPositionAndRotation(_pos, Quaternion.Euler(_euler));
        if (_mat != null)
        {
            var c = Tint(0.35f);
            _mat.color = c;
            if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", c);
        }
    }

    /// <summary>On-screen hint and a wireframe of the box (drawn in OnGUI, so it shows even when no shader can fill the box).</summary>
    public static void DrawGui()
    {
        if (_car == null || Event.current == null || Event.current.type != EventType.Repaint) return;
        try
        {
            var cam = Camera.main;
            if (cam != null && _hasSpot)
            {
                var rot = Quaternion.Euler(_euler);
                var pts = new Vector2[8];
                bool ok = true;
                for (int i = 0; i < 8; i++)
                {
                    var corner = _size.center + Vector3.Scale(_size.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var sp = cam.WorldToScreenPoint(_pos + rot * corner);
                    if (sp.z <= 0f) { ok = false; break; }
                    pts[i] = new Vector2(sp.x, Screen.height - sp.y);
                }
                if (ok)
                {
                    var col = Tint(0.95f);
                    for (int i = 0; i < 8; i++)
                        for (int bit = 1; bit < 8; bit <<= 1)
                            if ((i & bit) == 0) OverlayDraw.Line(pts[i], pts[i | bit], col, 2f);
                }
            }
            string state = !_hasSpot ? "no spot" : _steep ? "too steep" : !_clear ? "blocked" : "clear";
            string line = $"Placing {_car.Display}: left click spawn  |  Q/E or wheel rotate  |  right click or Esc cancel";
            float y = Screen.height * 0.08f;
            y += OverlayDraw.Label(Screen.width * 0.5f, y, line, 18, new Color(1f, 0.95f, 0.75f, 1f)) + 4f;
            string sub = _fallback ? $"No ground under the aim: spot is {_fbAhead:0} m ahead of your car (W/S and A/D move it)  |  {state}" : state;
            OverlayDraw.Label(Screen.width * 0.5f, y, sub, 16, _clear ? new Color(0.6f, 1f, 0.65f, 1f) : new Color(1f, 0.5f, 0.45f, 1f));
        }
        catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.DrawGui"); }
    }

    private static void EnsureMaterial()
    {
        if (_mat != null || _matTried) return;
        _matTried = true;
        foreach (var name in Shaders)
        {
            try
            {
                var shader = Shader.Find(name);
                if (shader == null || !shader.isSupported) continue;
                var m = new Material(shader) { name = "OTH CarPlace", hideFlags = HideFlags.DontUnloadUnusedAsset };
                MakeTransparent(m);
                _mat = m;
                DebugLog.Info(CarSpawnerPlugin.ModId, $"place: box material uses \"{name}\"");
                return;
            }
            catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.EnsureMaterial"); }
        }
        DebugLog.Info(CarSpawnerPlugin.ModId, "place: no see-through shader found, only the outline is drawn");
    }

    // URP's own property names; shaders without them ignore the calls (same as the ghost car's material).
    private static void MakeTransparent(Material m)
    {
        SetFloat(m, "_Surface", 1f);
        SetFloat(m, "_Blend", 0f);
        SetFloat(m, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        SetFloat(m, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        SetFloat(m, "_ZWrite", 0f);
        SetFloat(m, "_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private static void SetFloat(Material m, string name, float v)
    {
        if (m.HasProperty(name)) m.SetFloat(name, v);
    }

    // Built once and reused: only the cube's size and offset change per car.
    private static void Build()
    {
        EnsureMaterial();
        var root = new GameObject("OTH_CarPlaceBox");
        Object.DontDestroyOnLoad(root);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var col = cube.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        cube.transform.SetParent(root.transform, false);
        var rend = cube.GetComponent<Renderer>();
        if (_mat != null && rend != null)
        {
            rend.sharedMaterial = _mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
        }
        else if (rend != null) rend.enabled = false; // the game's default material would be an opaque grey block
        _box = root;
        _box.SetActive(false);
    }

    private static void Stop(string why)
    {
        if (_car != null) DebugLog.Info(CarSpawnerPlugin.ModId, "place: stopped: " + why);
        _car = null;
        _hasSpot = false;
        if (_box != null) _box.SetActive(false);
        if (_map == null) return;
        var id = _map.Value;
        _map = null;
        try { GameInput.PopMap(id); } catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.Stop"); }
    }

    /// <summary>The car's footprint in its own space; also sizes the box.</summary>
    private static Bounds Measure(string prefab)
    {
        if (!Sizes.TryGetValue(prefab, out var b))
        {
            b = new Bounds(new Vector3(0f, FallbackSize.y * 0.5f, 0f), FallbackSize);
            try
            {
                string key = "Prefabs/Cars/Car_" + prefab;
                if (RuntimeAddressables.Resolves(key))
                {
                    var go = Addressables.LoadAssetAsync<GameObject>((Il2CppSystem.String)key).WaitForCompletion();
                    var m = MeshBounds(go);
                    if (m.HasValue) b = m.Value;
                }
            }
            catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerPlace.Measure"); }
            Sizes[prefab] = b;
        }
        if (_box == null) Build();
        if (_box != null && _box.transform.childCount > 0)
        {
            var t = _box.transform.GetChild(0);
            t.localPosition = b.center;
            t.localScale = b.size;
        }
        return b;
    }

    /// <summary>The prefab's mesh bounds in its own space (null when it has none).</summary>
    private static Bounds? MeshBounds(GameObject go)
    {
        if (go == null) return null;
        var inv = go.transform.worldToLocalMatrix;
        bool any = false;
        var total = new Bounds();
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var mb = mesh.bounds;
            var m = inv * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (!any) { total = new Bounds(p, Vector3.zero); any = true; }
                else total.Encapsulate(p);
            }
        }
        return any && total.size.sqrMagnitude > 0.01f ? total : null;
    }
}
