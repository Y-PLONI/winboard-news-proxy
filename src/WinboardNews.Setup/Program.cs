[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace WinboardNews.Setup;

/// <summary>
/// WinboardNews-Setup.exe. Double-click = silent install (no window, no progress; only the Windows UAC prompt).
/// A message box appears ONLY if something fails. "--uninstall" reverts everything; "--no-ui" suppresses even the error box.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        bool uninstall = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        bool noUi = args.Contains("--no-ui", StringComparer.OrdinalIgnoreCase);

        try
        {
            Installer.Log($"=== {(uninstall ? "uninstall" : "install")} v{AppInfo.Version}");
            if (uninstall) Installer.Uninstall(); else Installer.Install();
            Installer.Log("done");
            return 0;
        }
        catch (Exception e)
        {
            Installer.Log($"FAILED: {e}");
            if (!noUi)
            {
                NativeMethods.ShowError(
                    "Winboard News",
                    $"{(uninstall ? "ההסרה" : "ההתקנה")} נכשלה:\n{e.Message}\n\nפרטים בקובץ:\n{Path.Combine(AppInfo.InstallDir, "install.log")}");
            }
            return 1;
        }
    }
}
