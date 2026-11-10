using System.Runtime.InteropServices;

namespace WinboardNews;

public static partial class NativeMethods
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hWnd, string text, string caption, uint type);

    public static void FlushDns()
    {
        try { DnsFlushResolverCache(); } catch { /* best effort */ }
    }

    /// <summary>Only used to report an installer failure; a successful run shows nothing at all.</summary>
    public static void ShowError(string caption, string text)
    {
        const uint MB_OK_ICONERROR_RTL = 0x00000010 | 0x00080000 | 0x00100000; // error icon, topmost, right-to-left
        try { MessageBox(0, text, caption, MB_OK_ICONERROR_RTL); } catch { /* nothing else to do */ }
    }
}
