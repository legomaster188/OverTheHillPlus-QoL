using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.LobbyGarage;

/// <summary>
/// Where the extra lobby cars (5th player and up) stand. The lobby scene only has spawn points for 4 cars; the
/// big-lobby code clones more straight behind them, which hides those cars (and stacks their name labels) behind
/// the front row. This places the clones in staggered rows behind the front row, away from the lobby camera, so
/// every car shows in the gaps, or extends the front line sideways. Purely local and visual.
/// </summary>
internal static class LobbyGarageLayout
{
    public const string CloneTag = "_OTHPlus"; // every cloned spawn point's name contains this (big-lobby clones too)
    public const string Staggered = "Staggered rows", Line = "Extend line", ModDefault = "Mod default";
    public static readonly string[] Layouts = { Staggered, Line, ModDefault };

    public static string LastLog = "";

    private struct Geometry
    {
        public int Count;              // original (non-cloned) spawn points
        public Vector3 Center;
        public Vector3 Axis;           // along the front row
        public Vector3 Back;           // away from the lobby camera
        public float Spacing;
        public float GroundOffset;     // how high the original points sit above the ground
        public float[] Lateral;        // each original's offset along Axis
        public Quaternion[] Rotation;
        public Transform[] Source;
    }

    public static bool IsClone(SpawnPoint p) => p != null && (p.gameObject.name ?? "").Contains(CloneTag);

    /// <summary>Moves the existing cloned points to the chosen layout. Returns how many were moved.</summary>
    public static int Apply(global::Lobby lobby, bool log)
    {
        var points = lobby?.spawnPoints;
        if (points == null || points.Count == 0) return 0;
        string layout = LobbyGarage.Layout;
        if (layout == ModDefault) return 0;
        if (!TryGeometry(lobby, out var g)) return 0;
        int moved = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p == null || !IsClone(p)) continue;
            Place(g, layout, i - g.Count, out var pos, out var rot);
            p.transform.SetPositionAndRotation(pos, rot);
            moved++;
        }
        if (log)
        {
            LastLog = $"{g.Count} original spots, {moved} extra placed as '{layout}' (spacing {g.Spacing:0.0} m)";
            Plugin.Logger.LogInfo($"[LobbyGarage] Layout: {LastLog}; centre {g.Center}, row axis {g.Axis}, back {g.Back}");
        }
        return moved;
    }

    /// <summary>Clones one more spawn point (for lobbies past the 16 the big-lobby code prepares).</summary>
    public static bool AddPoint(global::Lobby lobby)
    {
        var points = lobby?.spawnPoints;
        if (points == null || points.Count == 0 || !TryGeometry(lobby, out var g)) return false;
        int index = points.Count;
        Vector3 pos;
        Quaternion rot;
        Transform src;
        if (LobbyGarage.Layout == ModDefault)
        {
            // Same rule as the big-lobby clones: rows straight behind the originals.
            src = g.Source[index % g.Count];
            int row = index / g.Count;
            pos = src.position - src.forward * (9f * row) + src.right * ((index % 2 == 0 ? 1f : -1f) * 2f);
            pos = Snap(pos, src.position.y, g.GroundOffset);
            rot = src.rotation;
        }
        else
        {
            Place(g, LobbyGarage.Layout, index - g.Count, out pos, out rot);
            src = g.Source[Nearest(g, Vector3.Dot(pos - g.Center, g.Axis))];
        }
        var clone = Object.Instantiate(src.gameObject, pos, rot, src.parent);
        clone.name = $"{src.gameObject.name}{CloneTag}G{index}";
        var sp = clone.GetComponent<SpawnPoint>();
        if (sp == null) { Object.Destroy(clone); return false; }
        points.Add(sp);
        return true;
    }

    private static bool TryGeometry(global::Lobby lobby, out Geometry g)
    {
        g = default;
        var points = lobby.spawnPoints;
        var src = new List<Transform>();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p != null && !IsClone(p)) src.Add(p.transform);
        }
        if (src.Count == 0) return false;

        g.Count = src.Count;
        g.Source = src.ToArray();
        var c = Vector3.zero;
        foreach (var t in src) c += t.position;
        g.Center = c / src.Count;

        var axis = Flat(src[src.Count - 1].position - src[0].position);
        if (src.Count < 2 || axis.magnitude < 0.5f)
        {
            axis = Flat(src[0].right);
            g.Spacing = 5f;
        }
        else g.Spacing = Mathf.Clamp(axis.magnitude / (src.Count - 1), 3.5f, 9f);
        g.Axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : Vector3.right;

        // "Back" = away from the lobby camera, so added rows stand behind the front row in the shot.
        Vector3 back = Vector3.zero;
        try
        {
            var cam = lobby.lobbyCamera;
            if (cam != null) back = Flat(g.Center - cam.transform.position);
        }
        catch { }
        if (back.sqrMagnitude < 0.01f) back = -Flat(src[0].forward);
        back -= g.Axis * Vector3.Dot(back, g.Axis);
        if (back.sqrMagnitude < 1e-4f) back = Vector3.Cross(Vector3.up, g.Axis);
        g.Back = back.normalized;

        g.Lateral = new float[src.Count];
        g.Rotation = new Quaternion[src.Count];
        for (int i = 0; i < src.Count; i++)
        {
            g.Lateral[i] = Vector3.Dot(src[i].position - g.Center, g.Axis);
            g.Rotation[i] = src[i].rotation;
        }

        g.GroundOffset = 0f;
        if (Physics.Raycast(src[0].position + Vector3.up * 2f, Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore))
            g.GroundOffset = Mathf.Clamp(src[0].position.y - hit.point.y, 0f, 2f);
        return true;
    }

    /// <summary>Position for the extra spot number <paramref name="extra"/> (0 = first spot past the originals).</summary>
    private static void Place(Geometry g, string layout, int extra, out Vector3 pos, out Quaternion rot)
    {
        extra = Math.Max(0, extra);
        float lateral, depth;
        if (layout == Line)
        {
            float edge = 0f;
            foreach (var l in g.Lateral) edge = Mathf.Max(edge, Mathf.Abs(l));
            int k = extra / 2 + 1;
            lateral = (extra % 2 == 0 ? 1f : -1f) * (edge + k * g.Spacing);
            depth = 0f;
        }
        else
        {
            // Rows behind the front one: odd rows have one more spot and sit in the gaps, even rows line up.
            int row = 1, size = g.Count + 1, left = extra;
            while (left >= size)
            {
                left -= size;
                row++;
                size = row % 2 == 1 ? g.Count + 1 : g.Count;
            }
            lateral = CenterOut(left, size) * g.Spacing;
            depth = row * Mathf.Clamp(LobbyGarage.RowDepth, 3f, 20f);
        }
        float refY = g.Center.y;
        var p = g.Center + g.Axis * lateral + g.Back * depth;
        pos = Snap(p, refY, g.GroundOffset);
        rot = g.Rotation[Nearest(g, lateral)];
    }

    /// <summary>Slot offsets (in spacings) filled from the middle outwards, so a half-full row is centred.</summary>
    private static float CenterOut(int slot, int size)
    {
        var offsets = new List<float>(size);
        for (int j = 0; j < size; j++) offsets.Add(j - (size - 1) / 2f);
        offsets.Sort((a, b) =>
        {
            int c = Mathf.Abs(a).CompareTo(Mathf.Abs(b));
            return c != 0 ? c : b.CompareTo(a);
        });
        return offsets[Mathf.Clamp(slot, 0, size - 1)];
    }

    private static int Nearest(Geometry g, float lateral)
    {
        int best = 0;
        for (int i = 1; i < g.Count; i++)
            if (Mathf.Abs(g.Lateral[i] - lateral) < Mathf.Abs(g.Lateral[best] - lateral)) best = i;
        return best;
    }

    /// <summary>Puts the point on the ground below it, unless that ground is far from the lobby floor.</summary>
    private static Vector3 Snap(Vector3 p, float refY, float groundOffset)
    {
        // Cast from just above the lobby floor (not from high up) so roofs and awnings aren't taken for ground.
        if (Physics.Raycast(new Vector3(p.x, refY + 3.5f, p.z), Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore)
            && Mathf.Abs(hit.point.y + groundOffset - refY) < 3f)
            return new Vector3(p.x, hit.point.y + groundOffset, p.z);
        return new Vector3(p.x, refY, p.z);
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
}
