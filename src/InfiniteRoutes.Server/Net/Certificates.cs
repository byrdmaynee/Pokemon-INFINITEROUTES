using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using InfiniteRoutes.Server.Config;

namespace InfiniteRoutes.Server.Net;

/// <summary>
/// The server's TLS certificate: a .pfx from <c>[tls] cert_file</c>, or (mode "self-signed") one made on first start
/// and kept in the server's data folder so its fingerprint stays the same for clients that trusted it.
/// </summary>
public static class Certificates
{
    public static X509Certificate2 Load(ServerConfig config)
    {
        if (config.TlsMode == "files")
            return X509CertificateLoader.LoadPkcs12FromFile(config.Resolve(config.CertFile), config.CertPassword);
        var path = Path.Combine(config.Resolve(config.DataDir), "server-cert.pfx");
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cert = CreateSelfSigned(config);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static X509Certificate2 CreateSelfSigned(ServerConfig config)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={config.Name.Replace(",", " ")}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        if (IPAddress.TryParse(config.PublicAddress, out var ip))
            names.AddIpAddress(ip);
        else if (!string.IsNullOrWhiteSpace(config.PublicAddress))
            names.AddDnsName(config.PublicAddress);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    /// <summary>The certificate's SHA-256 fingerprint as colon-separated hex (what players are asked to trust).</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        string.Join(":", cert.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2")));
}
