using System.IO.Compression;

namespace MoonWalk.Pipeline.Stages;

/// <summary>Draws a small PNG (a moon over a gradient) with no imaging library, so the demo project can produce a screenshot artifact.</summary>
internal static class DemoPng
{
    public static byte[] Moon(int width = 320, int height = 180)
    {
        var raw = new byte[(width * 3 + 1) * height];
        var cx = width * 0.68;
        var cy = height * 0.38;
        var radius = height * 0.22;
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            raw[row] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var t = y / (double)height;
                double r = 9 + 25 * t, g = 11 + 30 * t, b = 22 + 60 * t;
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                var bite = Math.Sqrt((x - cx - radius * 0.45) * (x - cx - radius * 0.45) + (y - cy + radius * 0.1) * (y - cy + radius * 0.1));
                if (d < radius && bite > radius * 0.85)
                {
                    r = 138; g = 180; b = 255;
                }
                var i = row + 1 + x * 3;
                raw[i] = (byte)r;
                raw[i + 1] = (byte)g;
                raw[i + 2] = (byte)b;
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolor
        Chunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }
            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);

        var body = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            body[i] = (byte)type[i];
        }
        data.CopyTo(body, 4);
        output.Write(body);

        var crc = new byte[4];
        WriteInt(crc, 0, (int)Crc32(body));
        output.Write(crc);
    }

    private static void WriteInt(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
