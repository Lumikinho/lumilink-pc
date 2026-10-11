using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LumiLink.Desktop.Transport;

// Gera e guarda certificado autoassinado (primeira execução) + fingerprint SHA-256.
public sealed class CertManager
{
    private readonly string _pfxPath;
    private readonly Action<string> _log;

    public X509Certificate2 Certificate { get; private set; } = null!;
    public string Fingerprint { get; private set; } = "";

    public CertManager(string appDir, Action<string> log)
    {
        _pfxPath = Path.Combine(appDir, "server.pfx");
        _log = log;
    }

    public void EnsureCertificate(string password = "lumilink-local")
    {
        if (File.Exists(_pfxPath))
        {
            Certificate = new X509Certificate2(_pfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=LumiLink-PC", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var pfx = cert.Export(X509ContentType.Pfx, password);
            Directory.CreateDirectory(Path.GetDirectoryName(_pfxPath)!);
            File.WriteAllBytes(_pfxPath, pfx);
            Certificate = new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            _log("[tls] certificado autoassinado gerado.");
        }
        Fingerprint = Sha256Hex(Certificate.RawData);
        _log($"[tls] fingerprint: {Fingerprint}");
    }

    public static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text)
        => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text));
}
