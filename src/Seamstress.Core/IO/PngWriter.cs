using System.IO.Compression;

namespace Seamstress.Core.IO;

/// <summary>Minimal PNG encoder (8-bit RGB), used for generated textures such as the UV checker.</summary>
public static class PngWriter
{
    public static void WriteRgb(string path, int width, int height, byte[] rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("Pixel buffer does not match the image size.");
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bits per channel
        header[9] = 2;  // colour type: RGB
        WriteChunk(file, "IHDR", header);

        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter type: none
                zlib.Write(rgb, y * width * 3, width * 3);
            }
        }
        WriteChunk(file, "IDAT", data.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    /// <summary>
    /// Writes a checkerboard with numbered-looking contrast cells, used to judge texel density and distortion.
    /// </summary>
    public static void WriteChecker(string path, int size = 1024, int cells = 16)
    {
        var rgb = new byte[size * size * 3];
        int cell = size / cells;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int cx = x / cell, cy = y / cell;
                bool dark = ((cx + cy) & 1) == 0;
                // Tint each quadrant so flipped or rotated UVs are easy to spot.
                byte r = (byte)(dark ? 70 : 215), g = r, b = r;
                if (cx < cells / 2 && cy < cells / 2) r = (byte)Math.Min(255, r + 40);
                else if (cx >= cells / 2 && cy < cells / 2) g = (byte)Math.Min(255, g + 40);
                else if (cx < cells / 2) b = (byte)Math.Min(255, b + 40);
                bool line = x % cell == 0 || y % cell == 0;
                int i = (y * size + x) * 3;
                rgb[i] = line ? (byte)30 : r;
                rgb[i + 1] = line ? (byte)30 : g;
                rgb[i + 2] = line ? (byte)30 : b;
            }
        }
        WriteRgb(path, size, size, rgb);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
            typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        stream.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typed));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
