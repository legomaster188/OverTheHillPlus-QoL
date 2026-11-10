using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OverTheHillPlus.Cars;

/// <summary>A texture on its way: <see cref="Pixels"/> (mip chain included), or null with <see cref="Done"/> set when the game's
/// own decoder has to read it.</summary>
public sealed class TextureJob
{
    internal Assembly Assembly;
    internal string Resource;
    internal uint Order;
    internal volatile bool Wanted;
    private volatile bool _done;

    public byte[] Pixels;
    public int Width, Height;
    /// <summary>Set once it is decoded (or can't be).</summary>
    public bool Done { get => _done; internal set => _done = value; }
}

/// <summary>
/// The modded cars' assets read ahead on background threads, while the game is still starting: every car's model, parsed
/// next to the mods' own loading, and its textures, decoded so the car's build only uploads them. Runs plain managed code
/// over the DLLs' embedded resources and nothing else: a background thread touching the game's interop classes has
/// crashed the runtime, so nothing here may name a UnityEngine or interop type. Pure code (unit-tested).
/// </summary>
public static class AssetPrefetch
{
    /// <summary>Decoded textures waiting for their car, in bytes: about a dozen 2048 px ones with their mips.</summary>
    public const long MaxWaiting = 256L * 1024 * 1024;

    private static readonly Dictionary<string, Task<MeshBlob>> Models = new(StringComparer.Ordinal);
    private static readonly List<TextureJob> Textures = new();
    private static readonly List<TextureJob> Waiting = new();
    private static long _waiting;
    private static Thread _thread;
    private static bool _closed;

    /// <summary>Models read ahead so far, and textures decoded ahead.</summary>
    public static int ModelsRead { get; private set; }
    public static int TexturesDecoded { get; private set; }

    private static string Key(Assembly a, string resource) => a.GetName().Name + "/" + resource;

    /// <summary>Starts reading every .mesh resource of these assemblies on the thread pool.</summary>
    public static void ReadModels(IEnumerable<Assembly> assemblies)
    {
        foreach (var a in assemblies)
        {
            string[] names;
            try { names = a.GetManifestResourceNames(); }
            catch (Exception) { continue; }
            foreach (var n in names)
            {
                if (!n.EndsWith(".mesh", StringComparison.Ordinal)) continue;
                var asm = a;
                string res = n;
                lock (Models) Models[Key(asm, res)] = Task.Run(() => MeshBlob.Read(Read(asm, res)));
            }
        }
    }

    /// <summary>The model read ahead, waiting for it if need be; null when nobody read it or it failed (the caller
    /// reads it itself and reports what's wrong with it).</summary>
    public static MeshBlob TakeModel(Assembly a, string resource)
    {
        if (a == null) return null;
        Task<MeshBlob> t;
        lock (Models)
        {
            string key = Key(a, resource);
            if (!Models.TryGetValue(key, out t)) return null;
            Models.Remove(key);
        }
        try
        {
            var blob = t.GetAwaiter().GetResult();
            ModelsRead++;
            return blob;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Queues a texture; lower <paramref name="order"/> goes first.</summary>
    public static TextureJob Add(Assembly a, string resource, uint order)
    {
        var job = new TextureJob { Assembly = a, Resource = resource, Order = order };
        lock (Textures)
        {
            if (_closed) { job.Done = true; return job; }
            Textures.Add(job);
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "OTH car textures", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            Monitor.PulseAll(Textures);
        }
        return job;
    }

    /// <summary>A car's build wants this one now: it goes first, whatever waits.</summary>
    public static void Want(TextureJob job)
    {
        job.Wanted = true;
        lock (Textures) Monitor.PulseAll(Textures);
    }

    /// <summary>The job's pixels leave the queue (the caller owns them now, or dropped them).</summary>
    public static void Taken(TextureJob job, byte[] pixels)
    {
        lock (Textures)
        {
            if (pixels != null) _waiting -= pixels.Length;
            Waiting.Remove(job);
            Monitor.PulseAll(Textures);
        }
    }

    /// <summary>Every car stands: what's left in the queue will never be asked for.</summary>
    public static void Close()
    {
        lock (Textures)
        {
            _closed = true;
            foreach (var j in Textures) j.Done = true;
            Textures.Clear();
            // a car that failed before its textures never comes for them: back to the game's decoder if anything still asks
            foreach (var j in Waiting) j.Pixels = null;
            Waiting.Clear();
            _waiting = 0;
            Monitor.PulseAll(Textures);
        }
    }

    /// <summary>Back to the start (for the tests: a game start only goes through once).</summary>
    public static void Reopen()
    {
        lock (Textures)
        {
            _closed = false;
            _thread = null;
            _waiting = 0;
            Textures.Clear();
            Waiting.Clear();
            Monitor.PulseAll(Textures);
        }
    }

    private static TextureJob Next()
    {
        TextureJob best = null;
        foreach (var j in Textures)
        {
            if (j.Wanted) return j;
            if (_waiting < MaxWaiting && (best == null || j.Order < best.Order)) best = j;
        }
        return best;
    }

    private static void Run()
    {
        while (true)
        {
            TextureJob job;
            lock (Textures)
            {
                while (!_closed && (job = Next()) == null) Monitor.Wait(Textures);
                if (_closed) return;
                job = Next();
                Textures.Remove(job);
            }
            try
            {
                var rgba = TexturePixels.DecodePng(Read(job.Assembly, job.Resource), out int w, out int h);
                if (rgba != null)
                {
                    var chain = TexturePixels.MipChain(rgba, w, h);
                    job.Width = w;
                    job.Height = h;
                    lock (Textures)
                    {
                        if (!_closed)
                        {
                            job.Pixels = chain;
                            Waiting.Add(job);
                            _waiting += chain.Length;
                            TexturesDecoded++;
                        }
                    }
                }
            }
            catch (Exception) { /* the game's decoder reads it, and says what's wrong with it */ }
            job.Done = true;
        }
    }

    private static byte[] Read(Assembly a, string resource)
    {
        using var s = a.GetManifestResourceStream(resource);
        if (s == null) return null;
        var bytes = new byte[s.Length];
        int got = 0;
        while (got < bytes.Length)
        {
            int n = s.Read(bytes, got, bytes.Length - got);
            if (n <= 0) break;
            got += n;
        }
        return bytes;
    }
}
