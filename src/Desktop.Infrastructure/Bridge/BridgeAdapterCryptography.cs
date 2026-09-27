using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Desktop.Contracts.Bridge;

namespace Desktop.Infrastructure.Bridge;

/// <summary>Length-prefixed UTF-8 transcripts and RFC 2104 HMAC; no JSON reserialization for body MACs.</summary>
public static class BridgeAdapterCryptography
{
    public static string RandomToken() => Encode(RandomNumberGenerator.GetBytes(32));

    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode32(string value)
    {
        if (value.Length != 43 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new FormatException("BRIDGE_PAIRING_REJECTED");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        if (bytes.Length != 32 || Encode(bytes) != value)
            throw new FormatException("BRIDGE_PAIRING_REJECTED");
        return bytes;
    }

    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static string Sign(ReadOnlySpan<byte> key, params string[] fields)
    {
        using var transcript = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            transcript.Write(length);
            transcript.Write(bytes);
        }
        try { return Encode(HMACSHA256.HashData(key, transcript.GetBuffer().AsSpan(0, (int)transcript.Length))); }
        finally { CryptographicOperations.ZeroMemory(transcript.GetBuffer().AsSpan(0, (int)transcript.Length)); }
    }

    public static bool FixedEquals(string expected, string supplied)
    {
        try
        {
            var left = Decode32(expected);
            var right = Decode32(supplied);
            try { return CryptographicOperations.FixedTimeEquals(left, right); }
            finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); }
        }
        catch (FormatException) { return false; }
    }

    public static string ChallengeProof(ReadOnlySpan<byte> secret, AdapterChallenge challenge) => Sign(secret,
        BridgeAdapterProtocol.Version, "server-challenge", challenge.RequestId, challenge.ClientNonce,
        challenge.Nonce, challenge.SessionId, challenge.PluginInstanceId, challenge.TaskBinding,
        challenge.AppVersion, challenge.PluginId, challenge.PluginVersion, challenge.PhotoshopBuild,
        challenge.UxpRuntimeVersion, Number(challenge.IssuedAtUnixSeconds), Number(challenge.ExpiresAtUnixSeconds),
        string.Join('\n', challenge.Capabilities.Order(StringComparer.Ordinal)));

    public static byte[] SessionKey(ReadOnlySpan<byte> secret, AdapterChallenge challenge) => Decode32(Sign(secret,
        BridgeAdapterProtocol.Version, "session-key", challenge.ServerProof, challenge.ClientNonce,
        challenge.Nonce, challenge.SessionId, challenge.PluginInstanceId, challenge.TaskBinding));

    public static string PairingProof(ReadOnlySpan<byte> secret, BridgeHandshakeBodyV1 request)
    {
        // v1 nonce is the decoded 32 bytes, followed by the UTF-8 strings in the frozen order.
        var nonce = Decode32(request.Nonce);
        var suffix = Encoding.UTF8.GetBytes(request.SessionId + request.Host.PluginManifestVersion + request.Host.PhotoshopBuild);
        var message = new byte[nonce.Length + suffix.Length];
        nonce.CopyTo(message, 0); suffix.CopyTo(message, nonce.Length);
        try { return Encode(HMACSHA256.HashData(secret, message)); }
        finally { CryptographicOperations.ZeroMemory(message); CryptographicOperations.ZeroMemory(nonce); }
    }

    public static string HandshakeContextProof(ReadOnlySpan<byte> secret, AdapterChallenge challenge, string requestJson) =>
        Sign(secret, BridgeAdapterProtocol.Version, "client-handshake", challenge.ServerProof, Hash(requestJson));

    public static string HandshakeResponseProof(ReadOnlySpan<byte> key, string requestId, string responseJson) =>
        Sign(key, BridgeAdapterProtocol.Version, "handshake-response", requestId, Hash(responseJson));

    public static string RequestMac(ReadOnlySpan<byte> key, AdapterRequestAuthentication auth, string method, string path, string body)
    {
        string[] fields = [BridgeAdapterProtocol.Version, "request", method, path, auth.RequestId, auth.SessionId,
            auth.PluginInstanceId, Number(auth.Sequence), auth.Token, Hash(body)];
        if (method == "GET")
        {
            var query = auth.Query ?? throw new InvalidDataException("BRIDGE_PROTOCOL_MISMATCH");
            fields = [.. fields, "get-metadata", query.ProtocolVersion, query.CorrelationId, query.SentAt, query.IdempotencyKey];
        }
        return Sign(key, fields);
    }

    public static string ResponseMac(ReadOnlySpan<byte> key, AdapterRequestAuthentication auth, int status, string body) =>
        Sign(key, BridgeAdapterProtocol.Version, "response", auth.RequestId, auth.SessionId,
            auth.PluginInstanceId, Number(auth.Sequence), Number(status), Hash(body));

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
