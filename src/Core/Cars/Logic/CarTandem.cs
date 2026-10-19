using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A car with more rear axles than the donor has wheel colliders for (a tractor with a tandem drive axle). The framework
/// has four wheel colliders (FL, FR, RL, RR), so the model carries one physical rear axle (<c>wheel_RL</c> / <c>wheel_RR</c>,
/// at the tandem's centre) and says where the visible axles are with the anchors <see cref="FrontAnchor"/> and
/// <see cref="RearAnchor"/> (their z; x and height come from the physical wheels). The rear wheels' own meshes, the parts
/// <see cref="MeshBlob.RearWheelName"/> and <see cref="MeshBlob.RearTyreName"/>, are drawn on the rear corners (a dual wheel
/// on the rear, a single one on the steer axle). Every visible axle is a copy of the physical wheel's mesh objects, moved
/// along the car and turned with it each frame (<see cref="CarTandemWheels"/>); the physical wheel's own meshes are emptied.
/// This is only what the model says: a blob without the anchors is a four-wheel car as before. Pure (unit-tested).
/// </summary>
public static class CarTandem
{
    public const string FrontAnchor = "tandem_F", RearAnchor = "tandem_R";
    /// <summary>A follower object's name starts with this: "OTHP_tandem|&lt;axle&gt;|&lt;path of the wheel mesh below the wheel meshes node&gt;".</summary>
    public const string Prefix = "OTHP_tandem|";
    public const int MaxAxles = 4;

    /// <summary>How far (metres along the car, forward positive, in the model's space) each extra visible axle is from the
    /// physical rear wheels, in anchor order (front axle first); empty when the model has no tandem anchors or no rear
    /// wheel anchors.</summary>
    public static float[] AxleOffsets(MeshBlob blob)
    {
        if (blob == null) return Array.Empty<float>();
        var rl = blob.Anchor(MeshBlob.WheelAnchors[2]);
        var rr = blob.Anchor(MeshBlob.WheelAnchors[3]);
        if (rl == null || rr == null) return Array.Empty<float>();
        float z = (rl.Position.Z + rr.Position.Z) * 0.5f;
        var list = new List<float>();
        foreach (string name in new[] { FrontAnchor, RearAnchor })
        {
            var a = blob.Anchor(name);
            if (a == null) continue;
            float d = a.Position.Z - z;
            if (float.IsNaN(d) || float.IsInfinity(d) || Math.Abs(d) > 5f) continue;
            list.Add(d);
        }
        return list.Count > MaxAxles ? list.GetRange(0, MaxAxles).ToArray() : list.ToArray();
    }

    /// <summary>The follower's object name for axle <paramref name="axle"/> of the wheel mesh at <paramref name="path"/>.</summary>
    public static string FollowerName(int axle, string path) =>
        Prefix + axle.ToString(CultureInfo.InvariantCulture) + "|" + path;

    /// <summary>Splits a follower name; false for any other name.</summary>
    public static bool TryParse(string name, out int axle, out string path)
    {
        axle = 0;
        path = null;
        if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int bar = name.IndexOf('|', Prefix.Length);
        if (bar < 0 || bar + 1 >= name.Length) return false;
        if (!int.TryParse(name.Substring(Prefix.Length, bar - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out axle)) return false;
        path = name.Substring(bar + 1);
        return axle >= 0 && axle < MaxAxles;
    }

    /// <summary>The rim and tyre parts of a rear wheel: the model's own, or the steer wheel's when it has none.</summary>
    public static MeshPart RearRim(MeshBlob blob) => Own(blob, MeshBlob.RearWheelName, MeshPartKind.Wheel) ?? blob.Pick(MeshPartKind.Wheel, MeshBlob.WheelName);

    public static MeshPart RearTyre(MeshBlob blob) => Own(blob, MeshBlob.RearTyreName, MeshPartKind.Tyre) ?? blob.Pick(MeshPartKind.Tyre, MeshBlob.TyreName);

    private static MeshPart Own(MeshBlob blob, string name, MeshPartKind kind)
    {
        var p = blob.Part(name);
        return p != null && p.Kind == kind ? p : null;
    }
}
