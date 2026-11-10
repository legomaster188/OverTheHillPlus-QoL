using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat's messages on mod-net channel 50: a player says which build its Meerkat has (<see cref="MsgBuild"/>, to the
/// host), the host sends everyone the table of every player's build (<see cref="MsgTable"/>). Only modded peers with the
/// channel get them; vanilla players get nothing. Malformed messages are dropped. Pure (unit-tested).
/// <list type="bullet">
/// <item>Build: u8 version, u8 lift, u8 tyre, u8 top, u16 parts (little-endian), u8 engine, u8 free (1: the player's own free-play choice, which the host puts on the car as this mod's tune layer; 0: a career's, which the career mod applies): 8 bytes.</item>
/// <item>Table: u8 version, u8 count (at most <see cref="MaxRows"/>), then per row an i32 player (little-endian) and the
/// build's seven bytes after its version.</item>
/// </list>
/// </summary>
public static class MeerkatWire
{
    public const ushort Channel = 50;
    public const byte Version = 1;
    public const byte MsgBuild = 1, MsgTable = 2;
    public const int MaxRows = 64;
    public const int BuildBytes = 8;
    public const int RowBytes = 11;
    public const int MaxPayload = 2 + MaxRows * RowBytes;

    public static byte[] Build(MeerkatBuild b, bool free)
    {
        var o = new byte[BuildBytes];
        o[0] = Version;
        Put(o, 1, b, free);
        return o;
    }

    private static void Put(byte[] o, int at, MeerkatBuild b, bool free)
    {
        o[at] = (byte)b.Lift;
        o[at + 1] = (byte)b.Tyre;
        o[at + 2] = (byte)b.Top;
        o[at + 3] = (byte)((ushort)b.Parts & 0xFF);
        o[at + 4] = (byte)((ushort)b.Parts >> 8);
        o[at + 5] = b.Engine;
        o[at + 6] = (byte)(free ? 1 : 0);
    }

    private static bool Get(byte[] p, int at, out MeerkatBuild b, out bool free)
    {
        b = default;
        free = p[at + 6] == 1;
        if (p[at + 6] > 1) return false;
        if (p[at] > (byte)LiftStage.FourInch || p[at + 1] > (byte)TyreKind.Mud || p[at + 2] > (byte)TopKind.None || p[at + 5] > MeerkatBuild.MaxEngine)
            return false;
        int parts = p[at + 3] | p[at + 4] << 8;
        if ((parts & ~(int)Addons.All) != 0) return false;
        b = new MeerkatBuild((LiftStage)p[at], (TyreKind)p[at + 1], (TopKind)p[at + 2], (Addons)parts, p[at + 5]);
        return true;
    }

    public static bool ReadBuild(byte[] p, out MeerkatBuild b, out bool free)
    {
        b = default;
        free = false;
        return p != null && p.Length == BuildBytes && p[0] == Version && Get(p, 1, out b, out free);
    }

    public static byte[] Table(IReadOnlyList<(int Player, MeerkatBuild Build, bool Free)> rows)
    {
        int n = Math.Min(rows?.Count ?? 0, MaxRows);
        var o = new byte[2 + n * RowBytes];
        o[0] = Version;
        o[1] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            int at = 2 + i * RowBytes;
            int pl = rows[i].Player;
            o[at] = (byte)pl; o[at + 1] = (byte)(pl >> 8); o[at + 2] = (byte)(pl >> 16); o[at + 3] = (byte)(pl >> 24);
            Put(o, at + 4, rows[i].Build, rows[i].Free);
        }
        return o;
    }

    public static bool ReadTable(byte[] p, out List<(int Player, MeerkatBuild Build, bool Free)> rows)
    {
        rows = null;
        if (p == null || p.Length < 2 || p[0] != Version) return false;
        int n = p[1];
        if (n > MaxRows || p.Length != 2 + n * RowBytes) return false;
        var l = new List<(int, MeerkatBuild, bool)>(n);
        for (int i = 0; i < n; i++)
        {
            int at = 2 + i * RowBytes;
            int pl = p[at] | p[at + 1] << 8 | p[at + 2] << 16 | p[at + 3] << 24;
            if (!Get(p, at + 4, out var b, out bool f)) return false;
            l.Add((pl, b, f));
        }
        rows = l;
        return true;
    }
}
