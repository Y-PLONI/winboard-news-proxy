using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinboardNews;

/// <summary>
/// A self-signed certificate for windows.msn.com ONLY: not a CA, serverAuth only, so even with the key it
/// cannot sign certificates for any other site. It is trusted by adding it to LocalMachine\Root.
/// </summary>
public static class CertTool
{
    public static X509Certificate2 Create(string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        using var selfSigned = request.CreateSelfSigned(notBefore, notAfter);
        // Round-trip through PFX so the private key is a normal (non-ephemeral) key that SslStream accepts.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public static void SavePfx(X509Certificate2 cert, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
    }

    public static X509Certificate2? TryLoadPfx(string path)
    {
        try
        {
            return File.Exists(path) ? X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOurs(X509Certificate2 c, string host) =>
        c.Subject == $"CN={host}" && c.Issuer == c.Subject;

    /// <summary>True if a self-signed cert with this exact thumbprint is already trusted in LocalMachine\Root.</summary>
    public static bool IsTrusted(string host, string thumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(c => IsOurs(c, host) && string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Replaces any previous self-signed cert for the host in LocalMachine\Root with this one (needs admin).</summary>
    public static void Trust(X509Certificate2 cert, string host)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var old in store.Certificates.Where(c => IsOurs(c, host)).ToList())
            store.Remove(old);
        store.Add(X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
    }

    /// <summary>Removes every self-signed cert for the host from LocalMachine\Root (needs admin).</summary>
    public static int Untrust(string host)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var ours = store.Certificates.Where(c => IsOurs(c, host)).ToList();
        foreach (var c in ours) store.Remove(c);
        return ours.Count;
    }
}
