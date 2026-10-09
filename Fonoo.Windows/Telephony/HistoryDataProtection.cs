using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Fonoo.Windows.Telephony;

internal static class HistoryDataProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (bytes.Length is < 1 or > 1_000_000) throw new InvalidDataException("Invalid history cache size.");
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob { Length = bytes.Length, Data = pinned.AddrOfPinnedObject() };
            var success = encrypt
                ? CryptProtectData(ref input, "Fonoo Anrufliste", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows konnte die gespeicherte Anrufliste nicht schützen.");
            if (output.Length is < 1 or > 1_000_000) throw new InvalidDataException();
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); pinned.Free(); }
    }
}
