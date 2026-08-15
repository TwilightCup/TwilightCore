using System.IO;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;

namespace TwilightCore.Net;

/// <summary>
/// Managed TLS1.2 client over a raw TCP stream, backed by BouncyCastle
/// (<c>Org.BouncyCastle.Crypto.Tls.TlsClientProtocol</c>).
/// </summary>
/// <remarks>
/// <para>
/// Unity 2017.4's bundled Mono ships a native TLS stack whose cipher suite is too
/// old to complete a TLS1.2 handshake against the public match server —
/// <c>SslStream.AuthenticateAsClient</c> fails with
/// <c>IOException: The authentication or decryption has failed</c> before any
/// certificate validation. BouncyCastle performs the entire handshake in pure
/// managed code with its own modern ciphers, so it is independent of Mono's TLS.
/// </para>
/// <para>
/// <c>BouncyCastle.Crypto.dll</c> is deployed as a loose file next to
/// <c>TwilightCore.dll</c> in <c>BepInEx/plugins/</c> (BepInEx loads every DLL
/// there). The accept-any-certificate policy mirrors the WebSocket client's
/// existing stance (organiser-controlled tournament hosts). The returned
/// <see cref="Stream"/> is the plaintext application stream; dispose it to close.
/// </para>
/// </remarks>
internal static class BouncyTls
{
    /// <summary>
    /// Perform a TLS1.2 client handshake over <paramref name="raw"/> (a connected
    /// TCP stream) and return the plaintext application <see cref="Stream"/>.
    /// Throws on handshake failure (caller handles via <see cref="WebSocketClient"/>'s
    /// existing error path).
    /// </summary>
    public static Stream Connect(string host, Stream raw)
    {
        var protocol = new TlsClientProtocol(raw, new SecureRandom());
        protocol.Connect(new AcceptAnyCertTlsClient());
        return protocol.Stream;
    }

    /// <summary>
    /// <see cref="DefaultTlsClient"/> with an accept-any-certificate policy.
    /// <see cref="DefaultTlsClient"/> supplies working defaults for cipher-suite
    /// selection and key exchange; only authentication is overridden.
    /// </summary>
    private sealed class AcceptAnyCertTlsClient : DefaultTlsClient
    {
        public override TlsAuthentication GetAuthentication() => new AcceptAllTlsAuthentication();
    }

    /// <summary>
    /// Trust the server certificate unconditionally (tournament host is
    /// organiser-controlled; self-signed / proxy certs are expected).
    /// No client certificate is offered.
    /// </summary>
    private sealed class AcceptAllTlsAuthentication : TlsAuthentication
    {
        public void NotifyServerCertificate(Certificate serverCertificate) { /* accept */ }
        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}
