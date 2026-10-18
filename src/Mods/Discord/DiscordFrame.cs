using System;
using System.Text;

namespace OverTheHillPlus.Discord;

/// <summary>
/// Discord's local IPC framing (pure; the tests compile this file on its own). Every message on the named pipe
/// \\.\pipe\discord-ipc-N is a little-endian int32 opcode, a little-endian int32 payload length, then that many bytes
/// of UTF-8 JSON. Opcodes: 0 handshake, 1 frame (commands and their replies), 2 close, 3 ping, 4 pong.
/// </summary>
internal static class DiscordFrame
{
    public const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    public const int HeaderSize = 8;
    /// <summary>Largest payload either side sends or accepts (Discord's own replies are a few KB).</summary>
    public const int MaxPayload = 64 * 1024;

    /// <summary>One frame: header plus the UTF-8 JSON. Throws when the JSON is over <see cref="MaxPayload"/>.</summary>
    public static byte[] Encode(int op, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json ?? "");
        if (body.Length > MaxPayload) throw new ArgumentException("Discord frame too large: " + body.Length + " bytes");
        var frame = new byte[HeaderSize + body.Length];
        WriteInt(frame, 0, op);
        WriteInt(frame, 4, body.Length);
        Buffer.BlockCopy(body, 0, frame, HeaderSize, body.Length);
        return frame;
    }

    /// <summary>Reads a header; false for a short header, an unknown opcode or a length outside 0..MaxPayload.</summary>
    public static bool TryReadHeader(byte[] header, out int op, out int length) => TryReadHeader(header, 0, header?.Length ?? 0, out op, out length);

    public static bool TryReadHeader(byte[] buffer, int offset, int count, out int op, out int length)
    {
        op = length = 0;
        if (buffer == null || count < HeaderSize || offset < 0 || offset + HeaderSize > buffer.Length) return false;
        op = ReadInt(buffer, offset);
        length = ReadInt(buffer, offset + 4);
        return op >= OpHandshake && op <= OpPong && length >= 0 && length <= MaxPayload;
    }

    /// <summary>
    /// Decodes one whole frame from <paramref name="count"/> bytes at <paramref name="offset"/>. False when the bytes
    /// hold less than a frame (or a bad header); <paramref name="consumed"/> is the frame's full size.
    /// </summary>
    public static bool TryDecode(byte[] buffer, int offset, int count, out int op, out string json, out int consumed)
    {
        json = null;
        consumed = 0;
        if (!TryReadHeader(buffer, offset, count, out op, out int length)) return false;
        if (count < HeaderSize + length) return false;
        json = Encoding.UTF8.GetString(buffer, offset + HeaderSize, length);
        consumed = HeaderSize + length;
        return true;
    }

    private static void WriteInt(byte[] b, int at, int v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    internal static int ReadInt(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);
}
