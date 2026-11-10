using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace WinboardNews.Server;

/// <summary>
/// The background server (WinboardNews.exe). Started hidden at logon by the installer.
///   WinboardNews.exe                 HTTPS on 127.0.0.1:443 using the installed certificate
///   WinboardNews.exe --dev [--port N] plain HTTP (default :8443), for local development
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        bool dev = args.Contains("--dev");
        int port = dev ? 8443 : 443;
        int portIndex = Array.IndexOf(args, "--port");
        if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out int custom))
            port = custom;

        // One instance per user; a second launch (e.g. logon + manual start) just exits.
        using var mutex = new Mutex(true, dev ? $@"Local\WinboardNewsDev{port}" : @"Local\WinboardNewsServer", out bool first);
        if (!first) return 0;

        static void Log(string message) => WinboardNews.Log.Write("server.log", message);

        X509Certificate2? certificate = null;
        if (!dev)
        {
            certificate = CertTool.TryLoadPfx(AppInfo.PfxPath);
            if (certificate is null)
            {
                Log($"certificate not found or unreadable: {AppInfo.PfxPath}. Re-run the installer.");
                return 2;
            }
        }

        var server = new LocalServer(IPAddress.Loopback, port, certificate, new NewsService(), AppInfo.Version, Log);
        try
        {
            server.Start();
        }
        catch (Exception e)
        {
            Log($"cannot listen on 127.0.0.1:{port}: {e.Message}");
            return 3;
        }

        Log($"started v{AppInfo.Version} on {(dev ? "http" : "https")}://127.0.0.1:{port}");
        await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}
