using System.Runtime.InteropServices;

namespace Seamstress.Core.IO;

/// <summary>
/// Decodes WebP images with the system's libwebp, for builds of Qt that ship without the WebP image plugin.
/// The library is loaded on first use; when it is not installed the decoder reports failure instead of throwing.
/// </summary>
public static class WebpDecoder
{
    private delegate int GetInfo(IntPtr data, UIntPtr dataSize, out int width, out int height);
    private delegate IntPtr DecodeRgba(IntPtr data, UIntPtr dataSize, out int width, out int height);
    private delegate void FreeBuffer(IntPtr buffer);

    /// <summary>Largest decoded picture kept in memory, in bytes (256 MiB).</summary>
    private const long MaxDecodedBytes = 256L * 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _attempted;
    private static GetInfo? _info;
    private static DecodeRgba? _decode;
    private static FreeBuffer? _free;

    public static bool TryDecode(string path, out int width, out int height, out byte[] rgba)
    {
        width = height = 0;
        rgba = Array.Empty<byte>();
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (!IsWebp(data) || !EnsureLoaded())
            return false;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr address = handle.AddrOfPinnedObject();
            // Check the declared size before asking libwebp for the pixel buffer.
            if (_info!(address, (UIntPtr)data.Length, out width, out height) == 0 || width <= 0 || height <= 0)
                return false;
            long length = (long)width * height * 4;
            if (length > MaxDecodedBytes)
                return false;

            IntPtr buffer = _decode!(address, (UIntPtr)data.Length, out int decodedWidth, out int decodedHeight);
            if (buffer == IntPtr.Zero || decodedWidth != width || decodedHeight != height)
            {
                if (buffer != IntPtr.Zero)
                    _free!(buffer);
                return false;
            }
            try
            {
                rgba = new byte[length];
                Marshal.Copy(buffer, rgba, 0, (int)length);
                return true;
            }
            finally
            {
                _free!(buffer);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Writes a WebP as a PNG cache file; false when libwebp is unavailable or the picture is invalid.</summary>
    public static bool TryWritePng(string path, string outputPath)
    {
        if (!TryDecode(path, out int width, out int height, out byte[] rgba))
            return false;
        try
        {
            PngWriter.WriteRgba(outputPath, width, height, rgba);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsWebp(ReadOnlySpan<byte> data)
        => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8);

    private static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_attempted)
                return _decode != null;
            _attempted = true;
            foreach (string name in LibraryNames())
            {
                if (!NativeLibrary.TryLoad(name, out IntPtr library))
                    continue;
                if (!NativeLibrary.TryGetExport(library, "WebPGetInfo", out IntPtr info) ||
                    !NativeLibrary.TryGetExport(library, "WebPDecodeRGBA", out IntPtr decode) ||
                    !NativeLibrary.TryGetExport(library, "WebPFree", out IntPtr free))
                {
                    NativeLibrary.Free(library);
                    continue;
                }
                _info = Marshal.GetDelegateForFunctionPointer<GetInfo>(info);
                _decode = Marshal.GetDelegateForFunctionPointer<DecodeRgba>(decode);
                _free = Marshal.GetDelegateForFunctionPointer<FreeBuffer>(free);
                return true;
            }
            return false;
        }
    }

    private static IEnumerable<string> LibraryNames()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "libwebp.dll", "webp.dll" };
        if (OperatingSystem.IsMacOS())
            return new[] { "libwebp.dylib", "libwebp.7.dylib" };
        return new[] { "libwebp.so.7", "libwebp.so" };
    }
}
