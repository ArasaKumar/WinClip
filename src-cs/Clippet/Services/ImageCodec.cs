using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clippet.Services;

/// <summary>DIB ↔ PNG conversion and thumbnail generation via Windows.Graphics.Imaging.
/// All methods are async and intended to run off the UI thread.</summary>
public static class ImageCodec
{
    public sealed record DecodedImage(byte[] Png, uint Width, uint Height);

    /// <summary>Clipboard CF_DIB bytes → PNG. Handles BI_RGB and BI_BITFIELDS by wrapping the
    /// DIB in a BITMAPFILEHEADER and letting the BMP decoder interpret it.</summary>
    public static async Task<DecodedImage?> DibToPngAsync(byte[] dib)
    {
        if (dib.Length < 40) return null;
        byte[] bmp = WrapDibAsBmp(dib);
        using var ras = await ToRandomAccessStreamAsync(bmp);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.BmpDecoderId, ras);
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(outStream, decoder);
        await encoder.FlushAsync();
        byte[] png = await ToByteArrayAsync(outStream);
        return new DecodedImage(png, w, h);
    }

    /// <summary>PNG bytes → a smaller PNG whose longest edge is ≤ maxEdge.</summary>
    public static async Task<byte[]> MakeThumbnailAsync(byte[] png, int maxEdge)
    {
        using var inStream = await ToRandomAccessStreamAsync(png);
        var decoder = await BitmapDecoder.CreateAsync(inStream);
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        if (w == 0 || h == 0) return png;

        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(w, h));
        uint tw = Math.Max(1, (uint)Math.Round(w * scale));
        uint th = Math.Max(1, (uint)Math.Round(h * scale));

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(outStream, decoder);
        encoder.BitmapTransform.ScaledWidth = tw;
        encoder.BitmapTransform.ScaledHeight = th;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        return await ToByteArrayAsync(outStream);
    }

    /// <summary>PNG bytes → a top-down 32bpp BI_RGB DIB suitable for CF_DIB.</summary>
    public static async Task<byte[]> PngToDibAsync(byte[] png)
    {
        using var inStream = await ToRandomAccessStreamAsync(png);
        var decoder = await BitmapDecoder.CreateAsync(inStream);
        var pdp = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] bgra = pdp.DetachPixelData();
        int w = (int)decoder.PixelWidth;
        int h = (int)decoder.PixelHeight;

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = w,
            biHeight = -h,            // negative = top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB,
            biSizeImage = (uint)(w * h * 4),
        };

        byte[] dib = new byte[40 + bgra.Length];
        WriteStruct(dib, 0, header);
        System.Buffer.BlockCopy(bgra, 0, dib, 40, bgra.Length);
        return dib;
    }

    private static byte[] WrapDibAsBmp(byte[] dib)
    {
        uint biSize = BitConverter.ToUInt32(dib, 0);
        ushort bitCount = BitConverter.ToUInt16(dib, 14);
        uint compression = BitConverter.ToUInt32(dib, 16);
        uint clrUsed = dib.Length >= 36 ? BitConverter.ToUInt32(dib, 32) : 0;

        int extra = 0;
        if (biSize == 40 && compression == Native.BI_BITFIELDS)
            extra = 12; // three DWORD color masks follow a BITMAPINFOHEADER
        else if (bitCount <= 8)
        {
            int colors = clrUsed != 0 ? (int)clrUsed : (1 << bitCount);
            extra = colors * 4;
        }

        uint offBits = (uint)(14 + biSize + extra);
        var fh = new Native.BITMAPFILEHEADER
        {
            bfType = 0x4D42, // 'BM'
            bfSize = (uint)(14 + dib.Length),
            bfOffBits = offBits,
        };

        byte[] bmp = new byte[14 + dib.Length];
        WriteStruct(bmp, 0, fh);
        System.Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    private static void WriteStruct<T>(byte[] dst, int offset, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, dst, offset, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static async Task<InMemoryRandomAccessStream> ToRandomAccessStreamAsync(byte[] bytes)
    {
        var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        ras.Seek(0);
        return ras;
    }

    private static async Task<byte[]> ToByteArrayAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        uint size = (uint)stream.Size;
        var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        reader.DetachStream();
        return bytes;
    }
}
