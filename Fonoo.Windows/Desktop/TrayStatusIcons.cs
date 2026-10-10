using System.Runtime.InteropServices;

namespace Fonoo.Windows.Desktop;

// Own only generated HICONs; the caller keeps ownership of the source icon.
internal sealed class TrayStatusIcons : IDisposable
{
    private readonly Dictionary<TrayStatusKind, (IntPtr Tray, IntPtr Badge)> cache = [];
    private readonly int size;
    private readonly byte[] source;
    public TrayStatusIcons(IntPtr sourceIcon, int size)
    {
        this.size = size;
        if (!GetIconInfo(sourceIcon, out var info)) throw new InvalidOperationException("Icon unavailable");
        var dc = GetDC(IntPtr.Zero);
        try
        {
            source = new byte[size * size * 4]; var bitmap = Info(size);
            if (info.Color == IntPtr.Zero || GetDIBits(dc, info.Color, 0, (uint)size, source, ref bitmap, 0) != size)
                throw new InvalidOperationException("Icon pixels unavailable");
        }
        finally { ReleaseDC(IntPtr.Zero, dc); if (info.Color != IntPtr.Zero) DeleteObject(info.Color); if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); }
    }
    public (IntPtr Tray, IntPtr Badge) Get(TrayStatusKind kind)
    {
        if (cache.TryGetValue(kind, out var icons)) return icons;
        var tray = Create(StatusBadgePixels.Draw(kind, size, source), size);
        try { var badge = Create(StatusBadgePixels.Draw(kind, size), size); cache.Add(kind, (tray, badge)); return (tray, badge); }
        catch { DestroyIcon(tray); throw; }
    }
    private static IntPtr Create(byte[] pixels, int size)
    {
        var bitmap = Info(size);
        var color = CreateDIBSection(IntPtr.Zero, ref bitmap, 0, out var bits, IntPtr.Zero, 0);
        var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        try
        {
            if (color == IntPtr.Zero || mask == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("Icon allocation failed");
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new IconInfo { IsIcon = true, Color = color, Mask = mask };
            var icon = CreateIconIndirect(ref info);
            return icon != IntPtr.Zero ? icon : throw new InvalidOperationException("Icon allocation failed");
        }
        finally { if (color != IntPtr.Zero) DeleteObject(color); if (mask != IntPtr.Zero) DeleteObject(mask); }
    }
    private static BitmapInfo Info(int size) => new() { Header = new() { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32, SizeImage = (uint)(size * size * 4) } };
    public void Dispose() { foreach (var pair in cache.Values) { DestroyIcon(pair.Tray); DestroyIcon(pair.Badge); } cache.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
