using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The parts' looks, made when the game first shows them. A car instance gets an empty stand-in object for every part it
/// could carry (<see cref="PartObjects.Attach"/>: the game switches them by the car's bitset, so they must all be there,
/// in the same order, for every player), and nothing else. Building them all at the spawn, as before, read and parsed
/// every part's mesh file (each carries every car's shape), decoded four textures per part and made meshes and materials
/// for some fifty parts in one frame: 6 seconds on a free machine, 50 with two game copies, of which a car wears a
/// handful. Now a stand-in the game has switched on is built in steps:
/// <list type="bullet">
/// <item>off the game thread (<see cref="PartWorker"/>): the two shapes this car draws are read from the mesh file
/// (<see cref="PartMeshFile"/>), its four textures are decoded with their mips, its lenses and bulb sets are found
/// (<see cref="PartDecode"/>);</item>
/// <item>on the game thread, a few milliseconds a frame (<see cref="FrameBudgetMs"/>, at least one step): the Unity
/// meshes, the textures' upload, then the material, the renderer, the car's PropertyBlockSetter, the lower detail and
/// the lights, exactly as the one-frame build made them.</item>
/// </list>
/// Everything but the last step is shared: the meshes by mesh file and shape, the textures by part, the materials by
/// part and source material, the lenses by part and shape. A second car of the same model only dresses its objects.
/// </summary>
internal static class PartBuilds
{
    private const string Mod = CustomCosmeticsPlugin.ModId;

    /// <summary>Game-thread time a frame gives the parts' building (milliseconds); one step always runs.</summary>
    public const double FrameBudgetMs = 3.0;

    /// <summary>Stand-ins asked "are you shown?" per frame (one call into the game each); a car the game just refreshed is
    /// asked in full, the rest take turns, so a part previewed in the garage is seen within a few frames.</summary>
    private const int LooksPerFrame = 96;

    /// <summary>A batch of parts that took longer than this from shown to drawn is said in the log whatever its place.</summary>
    private const double SlowMs = 1000.0;

    // ---------------------------------------------------------------- what cars share

    /// <summary>One shape of a mesh file: read off the game thread, then a Unity mesh.</summary>
    internal sealed class ShapeRes
    {
        public string MeshName, ShapeName;
        /// <summary>The worker is done with it: <see cref="Arrays"/> or <see cref="Failed"/> is set.</summary>
        public volatile bool Done;
        public string Failed;
        /// <summary>As read (kept: the lenses of another colour of the same strip are found on it).</summary>
        public MeshPart Shape;
        /// <summary>As the mesh takes it (dropped once the mesh is made).</summary>
        public ShapeArrays Arrays;
        public Mesh Mesh;
        public double DecodeMs;
    }

    /// <summary>One texture of a part: decoded off the game thread, then uploaded.</summary>
    internal sealed class TexRes
    {
        /// <summary>The texture object's name ("bullbar_steel_albedo") and which of the four it is ("albedo").</summary>
        public string Name, File;
        public bool Linear;
        public Texture2D Fallback;
        public volatile bool Done;
        /// <summary>Its pixels with their mips (dropped once uploaded); null when there is no such file, or the plain
        /// decoder doesn't read it (<see cref="Png"/> then).</summary>
        public volatile TexturePixelData Pixels;
        /// <summary>The file's bytes when the plain decoder refused them: the game's own decoder takes them.</summary>
        public byte[] Png;
        public Texture2D Texture;
        public double DecodeMs;
    }

    /// <summary>The lenses of a part on one shape, and its bulb sets.</summary>
    internal sealed class LensRes
    {
        public volatile bool Done;
        public List<LampSpot> Spots;
        public ShapeArrays[] Sets;
        public string Failed;
        public double DecodeMs;
    }

    /// <summary>The four textures of a part: its file, linear or sRGB, and the stand-in when the part has none.</summary>
    private static readonly (string file, bool linear, bool white)[] Kinds =
    {
        ("albedo", false, true), ("ao", true, true), ("lights", true, false), ("mask", true, false),
    };

    private static readonly Dictionary<string, ShapeRes> Shapes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TexRes> Textures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LensRes> Lenses = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Material> Materials = new(StringComparer.Ordinal);
    private static readonly PartWorker Worker = new(2, "OTH part decoder");

    // ---------------------------------------------------------------- a car instance and its stand-ins

    /// <summary>The four wheels of a car instance as a wheels part needs them.</summary>
    internal sealed class WheelSet
    {
        public Transform WheelMeshes;
        public readonly Transform[] Frame = new Transform[4];
        public readonly MeshFilter[] FirstRim = new MeshFilter[4];
        public readonly int[] Layer = new int[4];
        public readonly Vector3[] FrameScale = new Vector3[4];
        public readonly List<MeshFilter> Rims = new();
        /// <summary>Why this car can't take custom wheels (null: it can).</summary>
        public string Why;
    }

    /// <summary>One part's stand-in on one car instance: an empty object (one per wheel for a wheels part) the game
    /// switches like any part, dressed with the part's look the first time it is seen switched on.</summary>
    internal sealed class Stand
    {
        public CarSet Car;
        public CustomPart Part;
        public GameObject[] Gos;
        /// <summary>The part's bit in the car's set (its place in the car's part list); -1 for a look.</summary>
        public int Bit;
        /// <summary>Drawn in place of a game part (<see cref="StockLooks"/> switches it), no bit of its own.</summary>
        public bool Look;
        public bool Wheels;
        /// <summary>0 waits to be shown, 1 being built, 2 drawn, 3 could not be built.</summary>
        public byte State;

        internal StagedBuild Build;
        /// <summary>The build's steps by name, in order (for the line that says which one took longest).</summary>
        internal readonly List<string> Steps = new();
        internal ShapeRes Full, Low;
        internal TexRes[] Tex;
        internal LensRes Lens;
        internal string Variant, From, Why;
        internal double ShownAt;
    }

    /// <summary>One controller instance (a networked car, the garage's model) with custom parts.</summary>
    internal sealed class CarSet
    {
        public Transform Cosmetics, Root, Group;
        public string Car, Where;
        public bool Garage;
        public PropertyBlockSetter Setter;
        public RuntimeCar Modded;
        public readonly List<Stand> Stands = new();
        public WheelSet Wheels;

        /// <summary>The game's parts dictionary and the car's list as the last pass over them left them: while they are
        /// the same, every stand-in is where the game looks for it.</summary>
        public IntPtr DictPtr;
        public int DictCount = -1, ListCount = -1;

        // where a body part's stand-in goes (read once per pass that makes any)
        internal int Layer;
        internal Quaternion RootRotation;
        internal Vector3 GroupScale, RootScale, Offset;
        internal bool Placing;

        internal int Cursor, Waiting, Building;
        internal bool LookNow;
        internal Dictionary<string, Material> Sources;
        internal Material AnySource, BodySource;
        internal bool BodyLooked;

        // the parts being built together, for the one line about them
        internal readonly List<string> BatchNames = new();
        internal readonly List<string> BatchFailed = new();
        internal double BatchStart, BatchDecodeMs, BatchMainMs, BatchWorstMs;
        internal string BatchWorst;
        internal int BatchFrame, Batches;

        /// <summary>This part's stand-in on this car, while its objects live.</summary>
        public Stand Find(CustomPart part, bool look)
        {
            foreach (var s in Stands)
                if (s.Part == part && s.Look == look && s.Gos.Length > 0 && s.Gos[0] != null) return s;
            return null;
        }

        public bool Settled(IntPtr dict, int dictCount, int listCount) => DictPtr == dict && DictCount == dictCount && ListCount == listCount;

        public void Settle(IntPtr dict, int dictCount, int listCount)
        {
            DictPtr = dict;
            DictCount = dictCount;
            ListCount = listCount;
            Placing = false;
        }
    }

    private static readonly Dictionary<IntPtr, CarSet> Sets = new();
    private static readonly List<CarSet> Cars = new();
    private static readonly List<Stand> Builds = new();
    private static readonly Stopwatch FrameClock = new();
    private static int _carCursor, _frame;

    public static int CarCount => Cars.Count;
    public static int Building => Builds.Count;

    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>This controller instance's set, null when it has none (yet).</summary>
    public static CarSet SetOf(Transform cosmetics)
    {
        if (cosmetics == null || !Sets.TryGetValue(cosmetics.Pointer, out var set)) return null;
        if (set.Cosmetics != null) return set;
        Drop(set);
        return null;
    }

    public static CarSet NewSet(Transform cosmetics, string car, PropertyBlockSetter setter, bool garage)
    {
        var set = new CarSet
        {
            Cosmetics = cosmetics, Root = RootOf(cosmetics), Group = GroupOf(cosmetics), Car = car, Garage = garage, Setter = setter,
            Modded = ModdedCars.ByCarName(car), Where = $"{car} ({(garage ? "garage model" : "car")})",
        };
        Sets[cosmetics.Pointer] = set;
        Cars.Add(set);
        return set;
    }

    private static void Drop(CarSet set)
    {
        Cars.Remove(set);
        foreach (var kv in Sets)
            if (ReferenceEquals(kv.Value, set)) { Sets.Remove(kv.Key); break; }
        for (int i = Builds.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Builds[i].Car, set)) Builds.RemoveAt(i);
        if (Builds.Count == 0) Sweep();
    }

    /// <summary>The game just switched this instance's parts (a mount, a spawn): its stand-ins are looked at in full on
    /// the next tick instead of waiting their turn.</summary>
    public static void Refreshed(Transform cosmetics)
    {
        if (cosmetics != null && Sets.TryGetValue(cosmetics.Pointer, out var set)) set.LookNow = true;
    }

    /// <summary>The car's root: the node above the cosmetic parts that has the game's "Parts" child.</summary>
    private static Transform RootOf(Transform from)
    {
        for (var t = from; t != null; t = t.parent)
            if (t.Find("Parts") != null) return t;
        return from.root;
    }

    /// <summary>The node the parts hang under: "Visual" of the car's "..._CosmeticParts" (every car has the same slot
    /// tree). Looked for where it always is first; a walk over every node under the cosmetics only if it isn't there.</summary>
    private static Transform GroupOf(Transform cosmetics)
    {
        const string end = "_CosmeticParts";
        if (cosmetics.name.EndsWith(end, StringComparison.Ordinal))
        {
            var visual = cosmetics.Find("Visual");
            if (visual != null) return visual;
        }
        for (int i = 0, n = cosmetics.childCount; i < n; i++)
        {
            var child = cosmetics.GetChild(i);
            if (child == null || !child.name.EndsWith(end, StringComparison.Ordinal)) continue;
            var visual = child.Find("Visual");
            if (visual != null) return visual;
        }
        foreach (var t in cosmetics.GetComponentsInChildren<Transform>(true))
            if (t.name == "Visual" && t.parent != null && t.parent.name.EndsWith(end, StringComparison.Ordinal)) return t;
        return cosmetics;
    }

    // ---------------------------------------------------------------- stand-ins (when the car spawns: cheap)

    /// <summary>Reads where body parts go on this instance, once per pass that makes stand-ins.</summary>
    private static void BeginPlacing(CarSet set)
    {
        if (set.Placing) return;
        set.Placing = true;
        set.Layer = set.Group.gameObject.layer;
        set.RootRotation = set.Root.rotation;
        set.GroupScale = set.Group.lossyScale;
        set.RootScale = set.Root.lossyScale;
        // A modded car's model sits on its donor skeleton at the layout's offset (CarBody does the same for the game's
        // slots); the garage model has its own.
        set.Offset = set.Modded != null ? set.Modded.ModelOffset(set.Garage) : Vector3.zero;
    }

    /// <summary>
    /// A body part's stand-in: an empty object at the part's fit under the car's cosmetics, hidden, with the game's
    /// upgrade marker when it is a part of its own (<paramref name="so"/>; null for a look, which the game knows nothing of).
    /// </summary>
    public static Stand AddBody(CarSet set, CustomPart part, VehiclePartSO so, PartFit fit, int bit)
    {
        BeginPlacing(set);
        var go = new GameObject("CC_" + part.Id);
        go.layer = set.Layer;
        var t = go.transform;
        t.SetParent(set.Group, false);
        t.position = set.Root.TransformPoint(new Vector3(fit.Pos[0], fit.Pos[1], fit.Pos[2]) + set.Offset);
        t.rotation = set.RootRotation * Quaternion.Euler(fit.Rot[0], fit.Rot[1], fit.Rot[2]);
        var gl = set.GroupScale;
        var rl = set.RootScale;
        t.localScale = new Vector3(Div(rl.x * fit.AxisScale(0), gl.x), Div(rl.y * fit.AxisScale(1), gl.y), Div(rl.z * fit.AxisScale(2), gl.z));
        if (so != null)
        {
            var upgrade = go.AddComponent<VehicleUpgradeObject>();
            upgrade.vehiclePartReference = so;
        }
        go.SetActive(false);
        var stand = new Stand { Car = set, Part = part, Gos = new[] { go }, Bit = bit, Look = so == null };
        set.Stands.Add(stand);
        set.Waiting++;
        return stand;
    }

    private static readonly string[] Corners = { "FL", "FR", "RL", "RR" };

    /// <summary>The wheel's own frame (as the car framework uses it): the nearest LODGroup above the mesh, which carries the
    /// right wheels' 180° yaw, else the corner node. Its origin is the wheel centre, +x the axle inboard on the left wheel.</summary>
    private static Transform WheelFrame(Transform mesh, Transform corner)
    {
        for (var t = mesh.parent; t != null && t != corner.parent; t = t.parent)
            if (t.GetComponent<LODGroup>() != null) return t;
        return corner;
    }

    /// <summary>The car's four wheels, looked at once per instance (every wheels part asks the same): each one's frame,
    /// its stock rim meshes, and whether the rim can be told from the tyre.</summary>
    public static WheelSet WheelsOf(CarSet set)
    {
        if (set.Wheels != null) return set.Wheels;
        var w = new WheelSet { WheelMeshes = set.Root.Find("Axles/WheelMeshes") };
        if (w.WheelMeshes == null) { w.Why = "the car has no Axles/WheelMeshes"; return w; }
        for (int c = 0; c < Corners.Length; c++)
        {
            var corner = w.WheelMeshes.Find(Corners[c]);
            if (corner == null) { w.Why = "the car has no wheel " + Corners[c]; return w; }
            MeshFilter firstRim = null;
            bool tyre = false;
            foreach (var mf in corner.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null) continue;
                string name = mf.name;
                if (WheelNames.IsTyre(name)) tyre = true;
                else if (WheelNames.IsRim(name)) { w.Rims.Add(mf); firstRim ??= mf; }
            }
            if (firstRim == null) { w.Why = $"wheel {Corners[c]} has no rim mesh"; return w; }
            // A wheel drawn as one mesh (rim and tyre together) can't lose its rim alone.
            if (!tyre) { w.Why = $"wheel {Corners[c]} draws its rim and tyre as one mesh"; return w; }
            w.Frame[c] = WheelFrame(firstRim.transform, corner);
            w.FirstRim[c] = firstRim;
            w.Layer[c] = firstRim.gameObject.layer;
            w.FrameScale[c] = w.Frame[c].lossyScale;
        }
        // kept only when the car has its wheels: one that isn't ready yet is looked at again on the next pass
        set.Wheels = w;
        return w;
    }

    /// <summary>
    /// A wheels part's stand-ins: one empty object per wheel, in the wheel's own frame under the node that steers and
    /// spins it, at the fit (pos = offset from the wheel centre in metres, -x outboard on either side; scale = the rim's
    /// size for this car's tyre). Null with <paramref name="why"/> when the car's wheels can't take one.
    /// </summary>
    public static Stand AddWheels(CarSet set, CustomPart part, VehiclePartSO so, PartFit fit, int bit, ref WheelSet wheels, out string why)
    {
        why = null;
        wheels ??= WheelsOf(set);
        if (wheels.Why != null) { why = wheels.Why; return null; }
        var gos = new GameObject[Corners.Length];
        for (int c = 0; c < Corners.Length; c++)
        {
            var go = new GameObject("CC_" + part.Id + "_" + Corners[c]);
            go.layer = wheels.Layer[c];
            var t = go.transform;
            t.SetParent(wheels.Frame[c], false);
            var fl = wheels.FrameScale[c];
            t.localPosition = new Vector3(Div(fit.Pos[0], fl.x), Div(fit.Pos[1], fl.y), Div(fit.Pos[2], fl.z));
            t.localRotation = Quaternion.Euler(fit.Rot[0], fit.Rot[1], fit.Rot[2]);
            t.localScale = new Vector3(Div(fit.AxisScale(0), fl.x), Div(fit.AxisScale(1), fl.y), Div(fit.AxisScale(2), fl.z));
            var upgrade = go.AddComponent<VehicleUpgradeObject>();
            upgrade.vehiclePartReference = so;
            go.SetActive(false);
            gos[c] = go;
        }
        var stand = new Stand { Car = set, Part = part, Gos = gos, Bit = bit, Wheels = true };
        set.Stands.Add(stand);
        set.Waiting++;
        return stand;
    }

    // ---------------------------------------------------------------- every frame

    public static void Tick()
    {
        if (Cars.Count == 0 || PartObjects.Off) return;
        _frame++;
        FrameClock.Restart();
        for (int i = Cars.Count - 1; i >= 0; i--)
            if (Cars[i].Cosmetics == null) Drop(Cars[i]);   // the car is gone, and its stand-ins with it
        Look();
        Work();
    }

    /// <summary>Finds the stand-ins the game has switched on since they were last looked at, and starts their build.</summary>
    private static void Look()
    {
        int budget = LooksPerFrame, n = Cars.Count;
        if (n == 0) return;
        if (_carCursor >= n) _carCursor = 0;
        double now = NowMs;
        for (int k = 0; k < n; k++)
        {
            int at = (_carCursor + k) % n;
            var car = Cars[at];
            if (car.Waiting <= 0) { car.LookNow = false; continue; }
            bool all = car.LookNow;
            car.LookNow = false;
            int count = car.Stands.Count;
            for (int i = 0; i < count && (all || budget > 0); i++)
            {
                if (car.Cursor >= count) car.Cursor = 0;
                var s = car.Stands[car.Cursor++];
                if (s.State != 0) continue;
                if (!all) budget--;
                bool shown;
                try { shown = s.Gos[0].activeSelf; }
                catch (Exception)
                {
                    // its object is gone though the car is not: nothing to build on
                    s.State = 3;
                    car.Waiting--;
                    continue;
                }
                if (shown) Begin(s, now);
            }
            if (budget <= 0) { _carCursor = at; return; }
        }
    }

    private static bool BudgetLeft() => FrameClock.Elapsed.TotalMilliseconds < FrameBudgetMs;

    /// <summary>The builds' steps for this frame: in turn while the frame's budget lasts, at least one step that does
    /// something. A build that waits for the background threads costs nothing and lets the next one go.</summary>
    private static void Work()
    {
        if (Builds.Count == 0) return;
        bool ran = false;
        for (int i = 0; i < Builds.Count; i++)
        {
            if (ran && !BudgetLeft()) break;
            var s = Builds[i];
            int before = s.Build.Position;
            BuildProgress p;
            try { p = s.Build.Advance(false, BudgetLeft); }
            catch (Exception e)
            {
                PartObjects.Fail(e, "PartBuilds " + s.Part.Id + " " + s.Build.StepName);
                s.Why = e.GetType().Name + ": " + e.Message;
                p = BuildProgress.Failed;
            }
            if (s.Build.Position != before) ran = true;
            if (p is BuildProgress.Working or BuildProgress.Later) continue;
            Builds.RemoveAt(i--);
            Finish(s, p == BuildProgress.Done);
        }
        if (Builds.Count == 0) Sweep();
    }

    // ---------------------------------------------------------------- a build

    /// <summary>The game has switched this stand-in on: asks the background threads for what it needs that no car has
    /// had made yet, and queues its steps.</summary>
    private static void Begin(Stand s, double now)
    {
        var part = s.Part;
        var car = s.Car;
        s.State = 1;
        s.ShownAt = now;
        car.Waiting--;
        if (car.Building == 0)
        {
            car.BatchStart = now;
            car.BatchFrame = _frame;
            car.BatchNames.Clear();
            car.BatchFailed.Clear();
            car.BatchDecodeMs = car.BatchMainMs = car.BatchWorstMs = 0;
            car.BatchWorst = null;
        }
        car.Building++;
        try
        {
            var shapes = part.Mesh.Shapes;
            int fullAt = PartShape.Pick(shapes, out string variant, car.Car, car.Modded?.Id);
            if (fullAt < 0) { s.Why = "its mesh has no shape with triangles"; Finish(s, false); return; }
            int lowAt = PartShape.PickLod1(shapes, variant);
            s.Variant = variant;
            AskShapes(part, shapes[fullAt].Name, lowAt < 0 ? null : shapes[lowAt].Name, out s.Full, out s.Low);
            s.Tex = new TexRes[Kinds.Length];
            for (int k = 0; k < Kinds.Length; k++) s.Tex[k] = AskTexture(part, k);
            var light = part.Manifest.Light;
            // a wheel has no extras (its lenses, if it had any, would spin)
            if (light != null && light.Any && !s.Wheels && !PartLights.Has(part, s.Full.ShapeName))
                s.Lens = AskLenses(part, s.Full, s.Tex[2], s.Tex[0], light.Twinkle);

            var b = new StagedBuild(() => NowMs);
            void Step(string name, Func<bool, StepResult> run)
            {
                b.Step(name, run);
                s.Steps.Add(name);
            }
            Step("mesh", _ => MeshStep(s.Full));
            if (s.Low != null) Step("lower detail", _ => LowStep(s));
            foreach (var tex in s.Tex)
            {
                var t = tex;
                Step(t.File + " texture", _ => TextureStep(t));
            }
            if (s.Lens != null) Step("lenses", _ => LensStep(s));
            Step("dress", _ => DressStep(s));
            s.Build = b;
            // the garage's model first: the player is looking at it
            int at = Builds.Count;
            if (car.Garage)
            {
                at = 0;
                while (at < Builds.Count && Builds[at].Car.Garage) at++;
            }
            Builds.Insert(at, s);
        }
        catch (Exception e)
        {
            PartObjects.Fail(e, "PartBuilds.Begin " + part.Id);
            s.Why = e.GetType().Name + ": " + e.Message;
            Builds.Remove(s);
            Finish(s, false);
        }
    }

    private static void AskShapes(CustomPart part, string full, string low, out ShapeRes fullRes, out ShapeRes lowRes)
    {
        var todo = new List<ShapeRes>(2);
        fullRes = ShapeOf(part.MeshName, full, todo);
        lowRes = low == null ? null : ShapeOf(part.MeshName, low, todo);
        if (todo.Count == 0) return;
        string meshName = part.MeshName;
        bool packed = part.MeshPacked;
        var want = todo.ToArray();
        Worker.Run(() => ReadShapes(meshName, packed, want));
    }

    private static ShapeRes ShapeOf(string meshName, string shapeName, List<ShapeRes> todo)
    {
        string key = meshName + "|" + shapeName;
        if (Shapes.TryGetValue(key, out var have)) return have;
        var r = new ShapeRes { MeshName = meshName, ShapeName = shapeName };
        Shapes[key] = r;
        todo.Add(r);
        return r;
    }

    private static TexRes AskTexture(CustomPart part, int kind)
    {
        var (file, linear, white) = Kinds[kind];
        string key = part.Id + "|" + file;
        if (Textures.TryGetValue(key, out var have)) return have;
        var r = new TexRes { Name = part.Id + "_" + file, File = file, Linear = linear, Fallback = white ? Texture2D.whiteTexture : Texture2D.blackTexture };
        Textures[key] = r;
        string resource = part.Id + "/" + file + ".png";
        if (!PartLibrary.Has(resource)) r.Done = true;   // the part has no such texture: the stand-in colour
        else Worker.Run(() => DecodeTexture(r, resource));
        return r;
    }

    private static LensRes AskLenses(CustomPart part, ShapeRes shape, TexRes lights, TexRes albedo, bool twinkle)
    {
        string key = part.ItemId.ToString("X8") + "|" + shape.ShapeName;
        if (Lenses.TryGetValue(key, out var have)) return have;
        var r = new LensRes();
        Lenses[key] = r;
        string id = part.Id;
        // After the jobs for its shape and its textures: they are queued already, so they have at least begun when a
        // thread takes this one.
        Worker.Run(() => FindLenses(r, shape, lights, albedo, id, twinkle));
        return r;
    }

    // ---- off the game thread: plain code only (no game, Unity or log calls); each job says how it went in its record

    private static void ReadShapes(string meshName, bool packed, ShapeRes[] want)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var names = new string[want.Length];
            for (int i = 0; i < want.Length; i++) names[i] = want[i].ShapeName;
            var file = PartLibrary.OpenFile(meshName) ?? throw new InvalidOperationException(meshName + " is not in the DLL");
            var got = PartMeshFile.Read(file, packed, names);   // closes the file
            for (int i = 0; i < want.Length; i++)
            {
                if (got[i] == null) { want[i].Failed = $"{meshName} has no shape {names[i]}"; continue; }
                want[i].Shape = got[i];
                want[i].Arrays = PartDecode.Arrays(got[i]);
            }
        }
        catch (Exception e)
        {
            foreach (var w in want)
                if (w.Arrays == null) w.Failed = e.GetType().Name + ": " + e.Message;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        foreach (var w in want)
        {
            w.DecodeMs = ms / want.Length;
            w.Done = true;
        }
    }

    private static void DecodeTexture(TexRes r, string resource)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var png = PartLibrary.ReadFile(resource);
            if (png != null)
            {
                var px = PartDecode.Texture(png);
                if (px != null) r.Pixels = px;
                else r.Png = png;
            }
        }
        catch (Exception) { /* the stand-in colour; the build says which texture is missing */ }
        r.DecodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        r.Done = true;
    }

    private static bool WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 60000 && !done(); i++) Thread.Sleep(1);
        return done();
    }

    private static void FindLenses(LensRes r, ShapeRes shape, TexRes lights, TexRes albedo, string partId, bool twinkle)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (!WaitFor(() => shape.Done && lights.Done && albedo.Done) || shape.Shape == null) throw new InvalidOperationException("its shape was not read");
            byte[] l = Level0(lights, partId + "/lights.png", out int lw, out int lh);
            int aw = 0, ah = 0;
            byte[] a = l == null ? null : Level0(albedo, partId + "/albedo.png", out aw, out ah);
            r.Spots = PartDecode.Spots(shape.Shape, l, lw, lh, a, aw, ah);
            r.Sets = twinkle ? PartDecode.TwinkleSets(shape.Shape, r.Spots) : null;
        }
        catch (Exception e)
        {
            r.Failed = e.GetType().Name + ": " + e.Message;
            r.Spots = new List<LampSpot>();
            r.Sets = null;
        }
        r.DecodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        r.Done = true;
    }

    /// <summary>A texture's full-size pixels (RGBA32, bottom row first): the start of its decoded chain while that waits
    /// for its upload, else decoded again from the file. Null when it can't be had.</summary>
    private static byte[] Level0(TexRes tex, string resource, out int w, out int h)
    {
        var px = tex.Pixels;
        if (px != null) { w = px.Width; h = px.Height; return px.Chain; }
        w = h = 0;
        var png = PartLibrary.ReadFile(resource);
        return png == null ? null : TexturePixels.DecodePng(png, out w, out h);
    }

    // ---- on the game thread, a step at a time

    private static StepResult MeshStep(ShapeRes r)
    {
        if (r.Mesh != null) return StepResult.Next;
        if (!r.Done) return StepResult.Wait;
        if (r.Arrays == null) return StepResult.Fail(r.Failed ?? "its mesh could not be read");
        r.Mesh = MakeMesh(MeshName(r), r.Arrays);
        r.Arrays = null;
        return r.Mesh != null ? StepResult.Next : StepResult.Fail("its mesh has no triangles");
    }

    /// <summary>The lower detail is an extra: a part whose LOD1 can't be made draws its full shape at any distance.</summary>
    private static StepResult LowStep(Stand s)
    {
        var r = s.Low;
        if (r.Mesh != null) return StepResult.Next;
        if (!r.Done) return StepResult.Wait;
        if (r.Arrays != null) r.Mesh = MakeMesh(MeshName(r), r.Arrays);
        r.Arrays = null;
        if (r.Mesh == null) s.Low = null;
        return StepResult.Next;
    }

    /// <summary>"cc_bullbar_steel", "cc_bullbar_steel_@Hilo", "cc_bullbar_steel_lod1@Hilo": the mesh file's part and the shape.</summary>
    private static string MeshName(ShapeRes r)
    {
        int slash = r.MeshName.IndexOf('/');
        string owner = slash > 0 ? r.MeshName.Substring(0, slash) : r.MeshName;
        return "cc_" + owner + (r.ShapeName == PartShape.Full ? "" : "_" + r.ShapeName.Replace(PartShape.Full, "").Trim('_'));
    }

    /// <summary>A Unity mesh of a shape's arrays, as core's CarAssets.BuildMesh makes one (normals as given, bounds and
    /// tangents worked out by Unity).</summary>
    private static Mesh MakeMesh(string name, ShapeArrays a)
    {
        int nv = a.VertexCount;
        if (nv == 0 || a.Indices.Length == 0) return null;
        var verts = new Vector3[nv];
        var norms = new Vector3[nv];
        var uvs = new Vector2[nv];
        // plain floats straight into the arrays: a Unity vector's constructor is a trip into the game
        a.Positions.AsSpan().CopyTo(MemoryMarshal.Cast<Vector3, float>(verts.AsSpan()));
        a.Normals.AsSpan().CopyTo(MemoryMarshal.Cast<Vector3, float>(norms.AsSpan()));
        a.Uvs.AsSpan().CopyTo(MemoryMarshal.Cast<Vector2, float>(uvs.AsSpan()));
        var mesh = new Mesh { name = name };
        if (nv > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.uv = uvs;
        mesh.triangles = a.Indices;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(mesh);
        return mesh;
    }

    private static StepResult TextureStep(TexRes r)
    {
        if (r.Texture != null) return StepResult.Next;
        if (!r.Done) return StepResult.Wait;
        var px = r.Pixels;
        Texture2D t = null;
        if (px != null) t = Upload(r, px);
        else if (r.Png != null) t = PartObjects.LoadPng(r.Name, r.Png, r.Linear);
        r.Pixels = null;
        r.Png = null;
        r.Texture = t != null ? t : r.Fallback;
        return StepResult.Next;
    }

    /// <summary>A texture from pixels decoded off the game thread: the object the game's own PNG decoder made of the same
    /// file (RGBA32 with mips, pixels on the GPU only), sampled the same way.</summary>
    private static unsafe Texture2D Upload(TexRes r, TexturePixelData px)
    {
        var t = new Texture2D(px.Width, px.Height, TextureFormat.RGBA32, true, r.Linear) { name = r.Name };
        // straight from managed memory: the byte[] overload would copy the 5.6 MB into the game's heap first
        fixed (byte* p = px.Chain) t.LoadRawTextureData((IntPtr)p, px.Chain.Length);
        t.wrapMode = TextureWrapMode.Clamp; // like the game's car textures: no bleeding across the atlas edge
        // sampled like the modded cars' textures (core CarAssets.Sampling): trilinear, so lamp lenses and bars don't step
        // between mip levels, and anisotropic for parts seen at a grazing angle (bars, racks, steps)
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = 8;
        t.Apply(false, true);
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>Hands the lenses to the part lights, with the meshes of a twinkling part's bulb sets.</summary>
    private static StepResult LensStep(Stand s)
    {
        var r = s.Lens;
        if (!r.Done) return StepResult.Wait;
        string shape = s.Full.ShapeName;
        if (PartLights.Has(s.Part, shape)) return StepResult.Next;
        Mesh[] meshes = null;
        var sets = r.Sets;
        if (sets != null && sets[3] != null)
        {
            meshes = new Mesh[4];
            for (int k = 0; k < 4; k++)
                if (sets[k] != null) meshes[k] = MakeMesh($"cc_{s.Part.Id}_{shape}_{(k < 3 ? "twinkle" + k : "body")}", sets[k]);
            if (meshes[3] == null) meshes = null;
        }
        r.Sets = null;
        PartLights.Provide(s.Part, shape, r.Spots, meshes);
        if (r.Failed != null) DebugLog.Error(Mod, $"{s.Part.Id} ({shape}): its lenses could not be found: {r.Failed}");
        else DebugLog.Verbose(Mod, () => $"{s.Part.Id} ({shape}): {r.Spots.Count} lens(es) found in {r.DecodeMs:0.0} ms off the game thread");
        return StepResult.Next;
    }

    /// <summary>
    /// The last step, the only one a car does not share: mesh, material, the car's lighting, the lower detail and the
    /// part's own night lights on the stand-in objects (<see cref="PartLights"/>), the stock rims under a set of wheels
    /// (<see cref="WheelRims"/>) and the game's own mesh under a look (<see cref="StockLooks"/>).
    /// </summary>
    private static StepResult DressStep(Stand s)
    {
        var car = s.Car;
        var part = s.Part;
        foreach (var go in s.Gos)
            if (go == null) return StepResult.Fail("the car is gone");
        var slotSource = SourceFor(car, part.Manifest.Slot, out string from);
        if (slotSource == null)
            return StepResult.Fail($"no car material found to copy (nothing under {car.Cosmetics.name} uses a material with {PartObjects.LiveryProperty})");
        var mesh = s.Full.Mesh;
        var low = s.Low?.Mesh;
        for (int i = 0; i < s.Gos.Length; i++)
        {
            var go = s.Gos[i];
            var source = slotSource;
            if (s.Wheels)
            {
                var rim = car.Wheels?.FirstRim[i];
                var rimRenderer = rim != null ? rim.GetComponent<MeshRenderer>() : null;
                if (rimRenderer != null && PartObjects.UsesCarShader(rimRenderer.sharedMaterial, car.Setter)) { source = rimRenderer.sharedMaterial; from = "the stock rim's material"; }
            }
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = MaterialOf(s, source);
            renderer.sharedMaterial = material;
            PartObjects.RegisterWithSetter(renderer, material, source, car.Setter, part.Id, car.Car);
            if (low != null) PartLods.Add(go, mf, mesh, low);
            PartLights.Add(go, mf, part, s.Wheels ? null : s.Full.ShapeName, mesh, material, car.Setter, car.Root);
        }
        s.From = from;
        if (s.Wheels && car.Wheels != null) WheelRims.Add(car.Wheels.WheelMeshes, car.Wheels.Rims, new List<GameObject>(s.Gos));
        int emptied = s.Look ? StockLooks.Empty(s.Gos[0]) : 0;
        DebugLog.Verbose(Mod, () =>
        {
            var t = s.Gos[0].transform;
            return $"{car.Where}: {part.Id} " + (s.Look ? $"drawn in place of the game's {part.Manifest.Replaces} ({emptied} mesh(es) emptied)" : $"drawn (bit {s.Bit})") +
                   $", slot {part.Manifest.Slot}, {part.Manifest.Mount.ToString().ToLowerInvariant()}{(part.Manifest.Group.Length > 0 ? ", group " + part.Manifest.Group : "")}, " +
                   $"shape {s.Full.ShapeName} ({mesh.vertexCount} vertices{(low != null ? ", with its lower detail" : "")}), under {t.parent.name} of {car.Root.name}: " +
                   $"car-root pos {F(car.Root.InverseTransformPoint(t.position))}, local pos {F(t.localPosition)}, scale {F(t.localScale)}, layer {s.Gos[0].layer}, material from {from}";
        });
        return StepResult.Next;
    }

    private static Material MaterialOf(Stand s, Material carMaterial)
    {
        var p = s.Part;
        string key = p.Id + "|" + carMaterial.GetInstanceID();
        if (Materials.TryGetValue(key, out var have) && have != null) return have;
        var m = new Material(carMaterial) { name = "cc_" + p.Id };
        // The game's livery goes onto materials that are switchable; the part keeps its own albedo.
        CarShader.KeepTexture(m, s.Tex[0].Texture);
        m.SetTexture("_AO_Texture", s.Tex[1].Texture);
        m.SetTexture("_Lights_Texture", s.Tex[2].Texture);
        m.SetTexture("_Mask_Texture", s.Tex[3].Texture);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(m);
        Materials[key] = m;
        return m;
    }

    // ---------------------------------------------------------------- the material a part copies

    /// <summary>
    /// The material to copy: one a stock cosmetic of this very car uses (the one in the part's own slot first), so the shader
    /// settings, render state and lighting match the car's other parts and the car's PropertyBlockSetter (which drives the
    /// lights, dirt and wet by that shader) takes it. Falls back to the car's body material. <paramref name="from"/> says
    /// which it was. The car's cosmetics are walked once per instance for all the slots (a walk per part was most of what
    /// a part cost once its mesh and textures were there).
    /// </summary>
    private static Material SourceFor(CarSet car, string slot, out string from)
    {
        from = null;
        if (car.Sources == null) FindSources(car);
        string[] path = (slot ?? "").Split('/');
        string leaf = path.Length > 0 ? path[path.Length - 1] : "";
        string parent = path.Length > 1 ? path[path.Length - 2] : null;
        if (car.Sources.TryGetValue(parent == null ? leaf : parent + "\n" + leaf, out var m) && m != null)
        {
            from = "the stock " + slot + " cosmetic's material";
            return m;
        }
        if (car.AnySource != null) { from = "a stock cosmetic's material"; return car.AnySource; }
        if (!car.BodyLooked)
        {
            car.BodyLooked = true;
            car.BodySource = CarMaterial(car.Root);
        }
        if (car.BodySource != null) from = "the car's body material";
        return car.BodySource;
    }

    /// <summary>
    /// For every node under the car's cosmetics, by its name and by "parent\nname": the first material of the car's
    /// shader on a renderer at or under it, in the order of the hierarchy. And the first of all.
    /// </summary>
    private static void FindSources(CarSet car)
    {
        var map = new Dictionary<string, Material>(StringComparer.Ordinal);
        car.Sources = map;
        var names = new Dictionary<IntPtr, string>();
        var usable = new Dictionary<IntPtr, bool>();
        var seen = new HashSet<IntPtr>();
        IntPtr top = car.Cosmetics.Pointer;
        foreach (var r in car.Cosmetics.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            Material m = null;
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                if (!usable.TryGetValue(mat.Pointer, out bool ok)) usable[mat.Pointer] = ok = PartObjects.UsesCarShader(mat, car.Setter);
                if (ok) { m = mat; break; }
            }
            if (m == null) continue;
            if (car.AnySource == null) car.AnySource = m;
            // every node from the renderer up to the cosmetics may be a part's slot; one a renderer before this one sat
            // under has its material already, and so have all the nodes above it
            for (var t = r.transform; t != null; t = t.parent)
            {
                IntPtr at = t.Pointer;
                if (!seen.Add(at)) break;
                if (!names.TryGetValue(at, out string name)) names[at] = name = t.name;
                if (!map.ContainsKey(name)) map[name] = m;
                var up = t.parent;
                if (up != null)
                {
                    if (!names.TryGetValue(up.Pointer, out string upName)) names[up.Pointer] = upName = up.name;
                    string key = upName + "\n" + name;
                    if (!map.ContainsKey(key)) map[key] = m;
                }
                if (at == top) break;
            }
        }
    }

    private static Material CarMaterial(Transform root)
    {
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty(PartObjects.LiveryProperty)) return m;
        }
        return null;
    }

    // ---------------------------------------------------------------- done: one line per batch

    private static void Finish(Stand s, bool ok)
    {
        var car = s.Car;
        s.State = ok ? (byte)2 : (byte)3;
        if (s.Build != null)
        {
            var ms = s.Build.StepMs;
            for (int i = 0; i < ms.Length; i++)
            {
                car.BatchMainMs += ms[i];
                if (ms[i] > car.BatchWorstMs) { car.BatchWorstMs = ms[i]; car.BatchWorst = s.Part.Id + " " + (i < s.Steps.Count ? s.Steps[i] : "step " + i); }
            }
        }
        car.BatchDecodeMs += DecodeMsOf(s);
        if (ok) car.BatchNames.Add(s.Part.Id);
        else
        {
            string why = s.Why ?? s.Build?.Text ?? "it could not be built";
            car.BatchFailed.Add($"{s.Part.Id} ({why})");
            // a car that left while its part was being made is no news
            if (car.Cosmetics != null) PartObjects.LogOnce("skip" + car.Where + s.Part.Id, $"{car.Where}: {s.Part.Id} skipped: {why}.");
        }
        s.Build = null;
        if (--car.Building > 0) return;
        double took = NowMs - car.BatchStart;
        int frames = _frame - car.BatchFrame + 1;
        bool first = car.Batches++ == 0;
        if (car.BatchNames.Count == 0) return;
        string line = $"{car.Where}: {car.BatchNames.Count} part(s) drawn {took:0} ms after the game showed them ({Some(car.BatchNames)}): " +
                      $"{car.BatchDecodeMs:0} ms of reading and decoding off the game thread, {car.BatchMainMs:0.0} ms on the game thread over {frames} frame(s), " +
                      $"longest step {car.BatchWorstMs:0.0} ms ({car.BatchWorst ?? "-"})" +
                      (car.BatchFailed.Count > 0 ? $"; not built: {Some(car.BatchFailed)}" : "") + ".";
        // the parts a car spawns with, and any batch that took long, are said; a part tried on in the garage later only
        // in the debug log
        if (first || took > SlowMs) CustomCosmeticsPlugin.Logger?.LogInfo("Custom Cosmetics: " + line);
        else DebugLog.Verbose(Mod, line);
    }

    /// <summary>Background time this build waited for: what it needs that had not been made for a car before.</summary>
    private static double DecodeMsOf(Stand s)
    {
        double ms = 0;
        if (s.Full != null) { ms += s.Full.DecodeMs; s.Full.DecodeMs = 0; }
        if (s.Low != null) { ms += s.Low.DecodeMs; s.Low.DecodeMs = 0; }
        if (s.Tex != null)
            foreach (var t in s.Tex) { ms += t.DecodeMs; t.DecodeMs = 0; }
        if (s.Lens != null) { ms += s.Lens.DecodeMs; s.Lens.DecodeMs = 0; }
        return ms;
    }

    private static string Some(List<string> names)
    {
        const int max = 8;
        if (names.Count <= max) return string.Join(", ", names);
        return string.Join(", ", names.GetRange(0, max)) + $" and {names.Count - max} more";
    }

    /// <summary>Nothing is being built: what was decoded for a car that left before it was used is let go (a texture's
    /// pixels are 5.6 MB), and asked for again if a car ever shows that part.</summary>
    private static void Sweep()
    {
        List<string> gone = null;
        foreach (var kv in Textures)
            if (kv.Value.Done && kv.Value.Texture == null) (gone ??= new List<string>()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) Textures.Remove(k);
        gone = null;
        foreach (var kv in Shapes)
            if (kv.Value.Done && kv.Value.Mesh == null) (gone ??= new List<string>()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) Shapes.Remove(k);
        gone = null;
        foreach (var kv in Lenses)
            if (kv.Value.Done) (gone ??= new List<string>()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) Lenses.Remove(k);
    }

    private static float Div(float a, float b) => Mathf.Abs(b) < 1e-6f ? a : a / b;

    private static string F(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
}
