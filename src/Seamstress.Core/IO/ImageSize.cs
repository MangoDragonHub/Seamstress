using System.Buffers.Binary;

namespace Seamstress.Core.IO;

/// <summary>
/// Reads the pixel size from an image file's header, without decoding the picture. Supports the formats the
/// reference-image picker accepts: PNG, JPEG, GIF, BMP and WebP.
/// </summary>
public static class ImageSize
{
    /// <summary>The image's pixel size, or null when the file is unreadable or the format is not recognised.</summary>
    public static (int Width, int Height)? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return TryRead(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static (int Width, int Height)? TryRead(Stream stream)
    {
        // JPEG headers can sit behind a large EXIF block; a megabyte is plenty and bounds corrupt files.
        const int limit = 1 << 20;
        var buffer = new byte[limit];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0)
                break;
            length += read;
        }
        var data = new ReadOnlySpan<byte>(buffer, 0, length);

        (int, int)? size = data.Length >= 24 && data[..8].SequenceEqual(PngSignature) ? Png(data)
            : data.Length >= 10 && (data[..6].SequenceEqual(Gif87) || data[..6].SequenceEqual(Gif89)) ? Gif(data)
            : data.Length >= 26 && data[0] == 'B' && data[1] == 'M' ? Bmp(data)
            : data.Length >= 30 && data[..4].SequenceEqual(Riff) && data[8..12].SequenceEqual(WebpMagic) ? Webp(data)
            : Jpeg(data);
        return size is { } value && value.Item1 > 0 && value.Item2 > 0 && value.Item1 <= 200000 && value.Item2 <= 200000
            ? value : null;
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Gif87 = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89 = "GIF89a"u8.ToArray();
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] WebpMagic = "WEBP"u8.ToArray();

    private static (int, int)? Png(ReadOnlySpan<byte> data)
        => data[12] == 'I' && data[13] == 'H' && data[14] == 'D' && data[15] == 'R'
            ? (BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]))
            : null;

    private static (int, int)? Gif(ReadOnlySpan<byte> data)
        => (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));

    private static (int, int)? Bmp(ReadOnlySpan<byte> data)
    {
        int header = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        if (header < 12)
            return null;
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        return (width == int.MinValue ? 0 : Math.Abs(width), height == int.MinValue ? 0 : Math.Abs(height));
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        int i = 2;
        while (i + 9 < data.Length)
        {
            // Markers may be preceded by any number of 0xFF fill bytes.
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }
            while (i < data.Length && data[i] == 0xFF)
                i++;
            if (i >= data.Length)
                return null;
            byte marker = data[i++];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                continue;
            if (marker == 0xDA)
                return null; // start of scan; the frame header should already have been seen
            if (i + 2 > data.Length)
                return null;
            int segment = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            if (segment < 2)
                return null;
            // Every SOFn marker carries the size, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 7 > data.Length)
                    return null;
                int height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                return (width, height);
            }
            i += segment;
        }
        return null;
    }

    private static (int, int)? Webp(ReadOnlySpan<byte> data)
    {
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            string chunk = System.Text.Encoding.ASCII.GetString(data.Slice(offset, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            int start = offset + 8;
            if (size < 0 || start + size > data.Length)
                return null;
            var payload = data.Slice(start, size);
            switch (chunk)
            {
                case "VP8X" when payload.Length >= 10:
                    return (Read24(payload[4..]) + 1, Read24(payload[7..]) + 1);
                case "VP8 " when payload.Length >= 10:
                    // Three-byte frame tag, the 9D 01 2A start code, then 14-bit width and height.
                    if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
                        return null;
                    return (BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF,
                            BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF);
                case "VP8L" when payload.Length >= 5:
                    if (payload[0] != 0x2F)
                        return null;
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            offset = start + size + (size & 1);
        }
        return null;
    }

    private static int Read24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);
}
