using System.Text;

namespace WinboardNews;

/// <summary>Adds/removes the single tagged "127.0.0.1 windows.msn.com" line, leaving everything else untouched.</summary>
public static class HostsFile
{
    public static string WantedLine => $"127.0.0.1 {AppInfo.HostName} {AppInfo.HostsMarker}";

    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>Pure text transform: drops every tagged line (also repairs malformed ones), then adds the wanted line if asked.</summary>
    public static string Apply(string text, bool add)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.RemoveAll(l => l.Contains(AppInfo.HostsMarker, StringComparison.Ordinal));
        if (add) lines.Add(WantedLine);
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static bool IsInstalled(string text) =>
        text.Split('\n').Any(l => l.TrimEnd('\r') == WantedLine);

    public static void Update(bool add, string? path = null)
    {
        path ??= DefaultPath;
        string backup = path + ".winboard.bak";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                string current = File.ReadAllText(path);
                string updated = Apply(current, add);
                if (updated == current) return;
                if (!File.Exists(backup)) File.Copy(path, backup);
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.WriteAllText(path, updated, new UTF8Encoding(false));
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(300 * attempt);   // antivirus or another tool may hold the file briefly
            }
        }
    }
}
