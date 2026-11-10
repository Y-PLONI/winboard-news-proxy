using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace WinboardNews.Setup;

internal static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinboardNews";

    public static void Log(string message) => WinboardNews.Log.Write("install.log", message);

    // ---------------------------------------------------------------- install

    public static void Install()
    {
        Directory.CreateDirectory(AppInfo.InstallDir);

        // 1. Files: the server exe (embedded) and a copy of this setup (used by "Apps > Uninstall").
        KillByName("WinboardNews");
        ExtractServer();
        CopySelfToInstallDir();

        // 2. Certificate for windows.msn.com (generated once, trusted machine-wide, key kept only in the install folder).
        EnsureCertificate();

        // 3. hosts: windows.msn.com -> 127.0.0.1
        HostsFile.Update(add: true);
        NativeMethods.FlushDns();
        Log("hosts updated");

        // 4. Start with Windows (current user) and appear in Apps & features.
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(AppInfo.RunValueName, $"\"{AppInfo.ServerExePath}\"");
        RegisterUninstallEntry();

        // 5. Start the server now, hidden, without elevation, and wait until it answers over HTTPS.
        if (!StartServerAndWait())
            throw new InvalidOperationException(
                "השרת המקומי לא הגיב. ייתכן שפורט 443 תפוס על ידי תוכנה אחרת (למשל גרסה קודמת של התוכנה שעדיין רצה).");

        // 6. Reset the Widgets board so it loads the new page right away.
        ResetWidgetBoard();
    }

    private static void ExtractServer()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinboardNews.exe")
            ?? throw new InvalidOperationException("Embedded server is missing from the installer.");
        string target = AppInfo.ServerExePath;
        string temp = target + ".new";
        using (var file = File.Create(temp))
            resource.CopyTo(file);
        Retry(() => File.Move(temp, target, overwrite: true));
        Log($"server written: {target}");
    }

    private static void CopySelfToInstallDir()
    {
        string? self = Environment.ProcessPath;
        if (self is null) return;
        string target = AppInfo.SetupExePath;
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        Retry(() => File.Copy(self, target, overwrite: true));
    }

    private static void EnsureCertificate()
    {
        var existing = CertTool.TryLoadPfx(AppInfo.PfxPath);
        if (existing is not null
            && existing.NotAfter > DateTime.Now.AddDays(30)
            && CertTool.IsTrusted(AppInfo.HostName, existing.Thumbprint))
        {
            Log("certificate already installed");
            return;
        }

        var cert = CertTool.Create(AppInfo.HostName, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        CertTool.SavePfx(cert, AppInfo.PfxPath);
        CertTool.Trust(cert, AppInfo.HostName);
        Log($"certificate created and trusted ({cert.Thumbprint})");
    }

    private static void RegisterUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Winboard News (Hagizra in the Widgets board)");
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", "Y-PLONI");
        key.SetValue("InstallLocation", AppInfo.InstallDir);
        key.SetValue("DisplayIcon", AppInfo.ServerExePath);
        key.SetValue("UninstallString", $"\"{AppInfo.SetupExePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static bool StartServerAndWait()
    {
        // explorer.exe starts it with the normal (non-elevated) user token, so the server never runs as admin.
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppInfo.ServerExePath}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Log($"explorer launch failed: {e.Message}");
        }
        if (WaitHealthy(TimeSpan.FromSeconds(8))) return true;

        Log("server did not answer after explorer launch; starting directly");
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ServerExePath) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Log($"direct launch failed: {e.Message}");
        }
        return WaitHealthy(TimeSpan.FromSeconds(8));
    }

    private static bool WaitHealthy(TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (http.GetStringAsync($"https://{AppInfo.HostName}/healthz").GetAwaiter().GetResult() == "ok")
                {
                    Log("server healthy");
                    return true;
                }
            }
            catch (Exception e)
            {
                Log($"health check: {e.GetBaseException().Message}");
            }
            Thread.Sleep(500);
        }
        return false;
    }

    // -------------------------------------------------------------- uninstall

    public static void Uninstall()
    {
        KillByName("WinboardNews");

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(AppInfo.RunValueName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        HostsFile.Update(add: false);
        int removed = CertTool.Untrust(AppInfo.HostName);
        Log($"hosts entry removed; {removed} certificate(s) untrusted");
        NativeMethods.FlushDns();
        ResetWidgetBoard();

        // Delete everything; this running exe lives in the folder, so a hidden helper removes the folder once we exit.
        string dir = AppInfo.InstallDir;
        foreach (string file in Directory.GetFiles(dir))
        {
            if (string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception e) { Log($"could not delete {file}: {e.Message}"); }
        }
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The board keeps its page (and old DNS/connections) alive; closing it makes it start fresh on the next open.</summary>
    private static void ResetWidgetBoard()
    {
        KillByName("WidgetBoard");   // its WebView2 child processes exit with it
        Log("widget board reset");
    }

    private static void KillByName(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch (Exception e)
            {
                Log($"could not stop {name} ({p.Id}): {e.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(400 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(400 * attempt); }
        }
    }
}
