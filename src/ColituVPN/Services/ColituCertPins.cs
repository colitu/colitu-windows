using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace v2rayN.Services;

/// <summary>
/// Certificate pinning for the Colitu API / BFF connections (API bases incl. mirrors, colitu.com,
/// endpoints.json, update manifest). The server certificate must still validate normally; on top
/// of that the chain the OS BUILT (never certificates the server merely sent) has to contain one
/// of the public CA roots we use (ISRG X1/X2/YE/YR, GTS R1/R4), identified by the SHA-256 of their
/// SubjectPublicKeyInfo. This keeps an unrelated, rogue or injected CA from vouching for the API.
/// Pinning stops after <see cref="PinsExpireAt"/>, so a CA change can never lock old apps out for good.
/// Not used for sing-box/Xray or any third-party host.
/// </summary>
public static class ColituCertPins
{
    /// <summary>After this instant only the normal certificate validation applies.</summary>
    public static readonly DateTimeOffset PinsExpireAt = new(2027, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>Base64 SHA-256 of the SubjectPublicKeyInfo: ISRG X1, X2, YE, YR, GTS R1, R4.</summary>
    public static readonly IReadOnlyList<string> SpkiSha256Pins =
    [
        "C5+lpZ7tcVwmwQIMcRtPbsQtWLABXhQzejna0wHFr8M=",
        "diGVwiVYbubAI3RW4hB9xU8e/CH2GnkuvVFZE8zmgzI=",
        "sCkq5UWXjg+7mKu9lMhhYF5bGLsy7VI/UNW3tccdR7w=",
        "fk6IOKit1ild5647BH06ujSIq5XbCgqlbYl6ANhhi88=",
        "hxqRlPTu1bMS/0DITB1SSu0vd4u/8l8TjPgfaAp63Gc=",
        "mEflZT5enoR1FuXLgYYGqnVEoZvmf9c2bVBpiOjYQ0c="
    ];

    private static readonly HashSet<string> PinSet = new(SpkiSha256Pins, StringComparer.Ordinal);

    /// <summary>Base64 SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static string SpkiHash(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>
    /// True when pinning is over (<paramref name="now"/> after <see cref="PinsExpireAt"/>) or when
    /// one certificate of the validated chain carries a pinned key.
    /// </summary>
    public static bool Matches(IEnumerable<X509Certificate2> validatedChain, DateTimeOffset now)
    {
        if (now > PinsExpireAt)
        {
            return true;
        }
        foreach (var certificate in validatedChain)
        {
            if (PinSet.Contains(SpkiHash(certificate)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary><see cref="Matches(IEnumerable{X509Certificate2}, DateTimeOffset)"/> over the chain's built elements.</summary>
    public static bool Matches(X509Chain chain, DateTimeOffset now) =>
        Matches(chain.ChainElements.Select(element => element.Certificate), now);

    /// <summary>
    /// The full decision: normal validation first (<paramref name="sslPolicyErrors"/> must be None),
    /// then the pin. A mismatch is logged as <c>CERT_PIN_MISMATCH host</c>.
    /// </summary>
    public static bool Validate(string? host, X509Chain? chain, SslPolicyErrors sslPolicyErrors, DateTimeOffset now)
    {
        if (sslPolicyErrors != SslPolicyErrors.None)
        {
            return false;
        }
#if DEBUG
        // Local mock servers during development.
        if (host != null && (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]"))
        {
            return true;
        }
#endif
        if (now > PinsExpireAt)
        {
            return true;
        }
        if (chain != null && Matches(chain, now))
        {
            return true;
        }
        try
        {
            Logging.SaveLog($"CERT_PIN_MISMATCH {host ?? "?"}");
        }
        catch
        {
            // Logging must never turn a rejected connection into an exception of its own.
        }
        return false;
    }

    /// <summary><see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/> for Colitu hosts.</summary>
    public static bool RemoteCertificateValidation(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        var host = sender switch
        {
            SslStream stream => stream.TargetHostName,
            HttpRequestMessage request => request.RequestUri?.Host,
            string name => name,
            _ => null
        };
        return Validate(host, chain, sslPolicyErrors, DateTimeOffset.UtcNow);
    }

    /// <summary>Pins the server certificates of everything this handler connects to.</summary>
    public static SocketsHttpHandler Pin(SocketsHttpHandler handler)
    {
        handler.SslOptions.RemoteCertificateValidationCallback = RemoteCertificateValidation;
        return handler;
    }
}
