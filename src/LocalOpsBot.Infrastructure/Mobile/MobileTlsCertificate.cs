using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Infrastructure.Mobile;

/// <summary>
/// The LAN path's TLS identity (DECISIONS M-15): one self-signed ECDSA P-256 certificate kept in
/// the Agent account's personal store — for the LocalSystem service that is SYSTEM's own store,
/// readable only by SYSTEM. The QR carries its SHA-256 and the app accepts exactly that
/// certificate, so no CA, hostname or expiry check is involved; replacing it means re-pairing.
/// </summary>
public static class MobileTlsCertificate
{
    public const string SubjectName = "CN=Homebase Mobile LAN";
    private const string FriendlyName = "Homebase Mobile LAN";

    /// <summary>Loads the certificate (with its private key), creating and storing it on first use.</summary>
    public static X509Certificate2 LoadOrCreate(ILogger logger, StoreLocation location = StoreLocation.CurrentUser)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, SubjectName, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        var created = Create(persistKey: true);
        store.Add(created);
        logger.LogInformation("Created the Homebase Mobile LAN certificate (SHA-256 {Pin}).", Sha256Hex(created));
        return created;
    }

    /// <summary>
    /// A new self-signed server certificate. SChannel can't use ephemeral keys, so the key always
    /// lives in a key container; <paramref name="persistKey"/>=false (tests) lets the container go
    /// away when the certificate is disposed.
    /// </summary>
    public static X509Certificate2 Create(bool persistKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null,
            persistKey ? X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.UserKeySet);
        if (OperatingSystem.IsWindows()) cert.FriendlyName = FriendlyName;
        return cert;
    }

    /// <summary>The pin: lowercase hex SHA-256 of the DER-encoded certificate.</summary>
    public static string Sha256Hex(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
