using System.Reflection;

namespace WinboardNews;

public static class AppInfo
{
    public const string HostName = "windows.msn.com";
    public const string HostsMarker = "# winboard-news-proxy";
    public const string ServerExeName = "WinboardNews.exe";
    public const string SetupExeName = "WinboardNews-Setup.exe";
    public const string RunValueName = "WinboardNews";

    /// <summary>Per-user install folder: %LOCALAPPDATA%\Programs\WinboardNews.</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinboardNews");

    public static string PfxPath => Path.Combine(InstallDir, "server.pfx");
    public static string ServerExePath => Path.Combine(InstallDir, ServerExeName);
    public static string SetupExePath => Path.Combine(InstallDir, SetupExeName);

    /// <summary>Build version (e.g. 1.0.42). The page reloads itself when this changes.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}

/// <summary>Tiny size-capped file log; never throws.</summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Write(string file, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.InstallDir);
                string path = Path.Combine(AppInfo.InstallDir, file);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
