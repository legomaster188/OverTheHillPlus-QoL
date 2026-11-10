using System;
using System.IO;
using System.IO.Compression;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Textures made ready off the main thread: an 8-bit PNG turned into the raw RGBA32 bytes with their mip chain that
/// Texture2D.LoadRawTextureData takes as they are. The game's own PNG decoder runs on the main thread, a tenth of a second
/// for each of a car's 2048 px textures, a hundred of them at every start. Pure code (unit-tested): a background thread may
/// run it.
/// </summary>
public static class TexturePixels
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>
    /// The pixels of an 8-bit RGBA or RGB PNG (not interlaced): RGBA32, bottom row first as Unity keeps them. Null for any other
    /// kind of PNG, which the caller hands to the game's decoder.
    /// </summary>
    public static byte[] DecodePng(byte[] png, out int width, out int height)
    {
        width = height = 0;
        if (png == null || png.Length < 33) return null;
        for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) return null;
        int at = 8, idat = 0, channels = 0;
        // the compressed data can be cut over many chunks: first how much of it there is
        while (at + 12 <= png.Length)
        {
            int len = BigEndian(png, at);
            if (len < 0 || at + 12 + len > png.Length) return null;
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IHDR")
            {
                width = BigEndian(png, at + 8);
                height = BigEndian(png, at + 12);
                int depth = png[at + 16], colour = png[at + 17], interlace = png[at + 20];
                if (depth != 8 || interlace != 0 || width <= 0 || height <= 0) return null;
                channels = colour == 6 ? 4 : colour == 2 ? 3 : 0;
                if (channels == 0) return null;
            }
            else if (type == "PLTE" || type == "tRNS") return null;
            else if (type == "IDAT") idat += len;
            else if (type == "IEND") break;
            at += 12 + len;
        }
        if (channels == 0 || idat == 0) return null;
        var packed = new byte[idat];
        at = 8;
        int got = 0;
        while (at + 12 <= png.Length)
        {
            int len = BigEndian(png, at);
            if (png[at + 4] == (byte)'I' && png[at + 5] == (byte)'D' && png[at + 6] == (byte)'A' && png[at + 7] == (byte)'T')
            {
                Buffer.BlockCopy(png, at + 8, packed, got, len);
                got += len;
            }
            at += 12 + len;
        }
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        using (var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0) return null;
                read += n;
            }
        }
        if (!Unfilter(raw, stride, height, channels)) return null;
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int src = y * (stride + 1) + 1, dst = (height - 1 - y) * width * 4;
            if (channels == 4)
            {
                Buffer.BlockCopy(raw, src, rgba, dst, stride);
                continue;
            }
            for (int x = 0; x < width; x++, src += 3, dst += 4)
            {
                rgba[dst] = raw[src];
                rgba[dst + 1] = raw[src + 1];
                rgba[dst + 2] = raw[src + 2];
                rgba[dst + 3] = 255;
            }
        }
        return rgba;
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    /// <summary>Undoes the row filters in place (each row starts with its filter's number). False on an unknown filter.</summary>
    private static bool Unfilter(byte[] d, int stride, int rows, int bpp)
    {
        for (int y = 0; y < rows; y++)
        {
            int row = y * (stride + 1), cur = row + 1, up = cur - stride - 1;
            switch (d[row])
            {
                case 0:
                    break;
                case 1:
                    for (int i = bpp; i < stride; i++) d[cur + i] += d[cur + i - bpp];
                    break;
                case 2:
                    if (y == 0) break;
                    for (int i = 0; i < stride; i++) d[cur + i] += d[up + i];
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= bpp ? d[cur + i - bpp] : 0, above = y > 0 ? d[up + i] : 0;
                        d[cur + i] += (byte)((left + above) >> 1);
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? d[cur + i - bpp] : 0, b = y > 0 ? d[up + i] : 0, c = i >= bpp && y > 0 ? d[up + i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        d[cur + i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Levels of a full mip chain of a w x h picture, down to 1 x 1.</summary>
    public static int MipLevels(int w, int h)
    {
        int n = 1;
        while (w > 1 || h > 1) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); n++; }
        return n;
    }

    /// <summary>
    /// A full mip chain of an RGBA32 picture, largest first, each level the average of 2 x 2 texels of the one before: what a
    /// mipmapped RGBA32 texture wants from LoadRawTextureData (it refuses less: "not enough data provided").
    /// </summary>
    public static byte[] MipChain(byte[] rgba, int w, int h)
    {
        int total = 0;
        for (int a = w, b = h; ; a = Math.Max(1, a / 2), b = Math.Max(1, b / 2))
        {
            total += a * b * 4;
            if (a == 1 && b == 1) break;
        }
        var all = new byte[total];
        Buffer.BlockCopy(rgba, 0, all, 0, w * h * 4);
        int src = 0, dst = w * h * 4, sw = w, sh = h;
        while (sw > 1 || sh > 1)
        {
            int dw = Math.Max(1, sw / 2), dh = Math.Max(1, sh / 2);
            for (int y = 0; y < dh; y++)
            {
                int r0 = src + Math.Min(2 * y, sh - 1) * sw * 4, r1 = src + Math.Min(2 * y + 1, sh - 1) * sw * 4, o = dst + y * dw * 4;
                for (int x = 0; x < dw; x++, o += 4)
                {
                    int x0 = Math.Min(2 * x, sw - 1) * 4, x1 = Math.Min(2 * x + 1, sw - 1) * 4;
                    all[o] = (byte)((all[r0 + x0] + all[r0 + x1] + all[r1 + x0] + all[r1 + x1] + 2) >> 2);
                    all[o + 1] = (byte)((all[r0 + x0 + 1] + all[r0 + x1 + 1] + all[r1 + x0 + 1] + all[r1 + x1 + 1] + 2) >> 2);
                    all[o + 2] = (byte)((all[r0 + x0 + 2] + all[r0 + x1 + 2] + all[r1 + x0 + 2] + all[r1 + x1 + 2] + 2) >> 2);
                    all[o + 3] = (byte)((all[r0 + x0 + 3] + all[r0 + x1 + 3] + all[r1 + x0 + 3] + all[r1 + x1 + 3] + 2) >> 2);
                }
            }
            src = dst;
            dst += dw * dh * 4;
            sw = dw;
            sh = dh;
        }
        return all;
    }
}
