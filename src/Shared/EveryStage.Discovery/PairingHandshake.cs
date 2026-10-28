using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EveryStage.Discovery;

public sealed class CasterPairingHandshake : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    private readonly byte[] _casterNonce;
    private readonly byte[] _casterPublicKeyBytes;
    private readonly string _requestId;
    private readonly Guid _casterDeviceId;
    private readonly Guid _terminalDeviceId;
    private PairingHandshakeResult? _result;

    public CasterPairingHandshake(string requestId, Guid casterDeviceId, Guid terminalDeviceId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("RequestId is required.", nameof(requestId));
        if (casterDeviceId == Guid.Empty) throw new ArgumentException("Caster DeviceId is required.", nameof(casterDeviceId));
        if (terminalDeviceId == Guid.Empty) throw new ArgumentException("Terminal DeviceId is required.", nameof(terminalDeviceId));

        _requestId = requestId;
        _casterDeviceId = casterDeviceId;
        _terminalDeviceId = terminalDeviceId;
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _casterPublicKeyBytes = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        _casterNonce = RandomNumberGenerator.GetBytes(PairingHandshake.NonceSizeBytes);
    }

    public string CasterPublicKey => Convert.ToBase64String(_casterPublicKeyBytes);
    public string CasterNonceCommitment => Convert.ToBase64String(SHA256.HashData(_casterNonce));
    public string CasterNonce => Convert.ToBase64String(_casterNonce);

    public PairingHandshakeResult ProcessChallenge(string terminalPublicKey, string terminalNonce)
    {
        byte[] terminalPublicKeyBytes = PairingHandshake.DecodePublicKey(terminalPublicKey);
        byte[] terminalNonceBytes = PairingHandshake.DecodeNonce(terminalNonce, nameof(terminalNonce));
        byte[] secret = PairingHandshake.DeriveRawSecret(_ecdh, terminalPublicKeyBytes);
        _result = PairingHandshake.DeriveResult(
            secret, _requestId, _casterDeviceId, _terminalDeviceId,
            _casterPublicKeyBytes, terminalPublicKeyBytes, _casterNonce, terminalNonceBytes);
        return _result;
    }

    public bool VerifyKeyConfirmation(string? keyConfirmation) =>
        _result != null && PairingHandshake.FixedTimeEqualsBase64(_result.KeyConfirmation, keyConfirmation);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_casterNonce);
        _ecdh.Dispose();
    }
}

public sealed class TerminalPairingHandshake : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    private readonly byte[] _terminalNonce;
    private readonly byte[] _terminalPublicKeyBytes;
    private readonly byte[] _casterPublicKeyBytes;
    private readonly byte[] _casterNonceCommitment;
    private readonly string _requestId;
    private readonly Guid _casterDeviceId;
    private readonly Guid _terminalDeviceId;

    public TerminalPairingHandshake(
        string requestId, Guid casterDeviceId, Guid terminalDeviceId,
        string casterPublicKey, string casterNonceCommitment)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("RequestId is required.", nameof(requestId));
        if (casterDeviceId == Guid.Empty) throw new ArgumentException("Caster DeviceId is required.", nameof(casterDeviceId));
        if (terminalDeviceId == Guid.Empty) throw new ArgumentException("Terminal DeviceId is required.", nameof(terminalDeviceId));

        _requestId = requestId;
        _casterDeviceId = casterDeviceId;
        _terminalDeviceId = terminalDeviceId;
        _casterPublicKeyBytes = PairingHandshake.DecodePublicKey(casterPublicKey);
        _casterNonceCommitment = PairingHandshake.DecodeHash(casterNonceCommitment, nameof(casterNonceCommitment));
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _terminalPublicKeyBytes = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        _terminalNonce = RandomNumberGenerator.GetBytes(PairingHandshake.NonceSizeBytes);
    }

    public string TerminalPublicKey => Convert.ToBase64String(_terminalPublicKeyBytes);
    public string TerminalNonce => Convert.ToBase64String(_terminalNonce);

    public PairingHandshakeResult ProcessReveal(string casterNonce)
    {
        byte[] casterNonceBytes = PairingHandshake.DecodeNonce(casterNonce, nameof(casterNonce));
        byte[] actualCommitment = SHA256.HashData(casterNonceBytes);
        if (!CryptographicOperations.FixedTimeEquals(actualCommitment, _casterNonceCommitment))
            throw new CryptographicException("Caster nonce did not match its commitment.");

        byte[] secret = PairingHandshake.DeriveRawSecret(_ecdh, _casterPublicKeyBytes);
        return PairingHandshake.DeriveResult(
            secret, _requestId, _casterDeviceId, _terminalDeviceId,
            _casterPublicKeyBytes, _terminalPublicKeyBytes, casterNonceBytes, _terminalNonce);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_terminalNonce);
        _ecdh.Dispose();
    }
}

public sealed record PairingHandshakeResult(string PairingKey, string VerificationCode, string KeyConfirmation);

public static class PairingHandshake
{
    public const int ProtocolVersion = 2;
    public const int NonceSizeBytes = 32;

    private static readonly byte[] TranscriptLabel = Encoding.UTF8.GetBytes("EveryStage/pairing/v2");
    private static readonly byte[] KdfInfo = Encoding.UTF8.GetBytes("EveryStage/pairing/v2/key-and-code");
    private static readonly byte[] ConfirmLabel = Encoding.UTF8.GetBytes("terminal-confirm");

    internal static PairingHandshakeResult DeriveResult(
        byte[] rawSecret, string requestId, Guid casterDeviceId, Guid terminalDeviceId,
        byte[] casterPublicKey, byte[] terminalPublicKey, byte[] casterNonce, byte[] terminalNonce)
    {
        byte[] transcript = BuildTranscript(requestId, casterDeviceId, terminalDeviceId,
            casterPublicKey, terminalPublicKey, casterNonce, terminalNonce);
        byte[] transcriptHash = SHA256.HashData(transcript);
        byte[] material = HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSecret, PairingSecurity.KeySizeBytes + 4, transcriptHash, KdfInfo);
        byte[] key = material[..PairingSecurity.KeySizeBytes];
        uint codeNumber = BinaryPrimitives.ReadUInt32BigEndian(material.AsSpan(PairingSecurity.KeySizeBytes, 4)) % 1_000_000;
        byte[] confirmation = HMACSHA256.HashData(key, Combine(ConfirmLabel, transcriptHash));
        return new PairingHandshakeResult(
            Convert.ToBase64String(key),
            codeNumber.ToString("D6"),
            Convert.ToBase64String(confirmation));
    }

    internal static byte[] DecodePublicKey(string? base64)
    {
        byte[] bytes = DecodeBase64(base64, "publicKey");
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(bytes, out int read);
        if (read != bytes.Length) throw new CryptographicException("Public key has trailing data.");
        return bytes;
    }

    internal static byte[] DecodeNonce(string? base64, string name)
    {
        byte[] bytes = DecodeBase64(base64, name);
        if (bytes.Length != NonceSizeBytes) throw new CryptographicException($"{name} must be {NonceSizeBytes} bytes.");
        return bytes;
    }

    internal static byte[] DecodeHash(string? base64, string name)
    {
        byte[] bytes = DecodeBase64(base64, name);
        if (bytes.Length != SHA256.HashSizeInBytes) throw new CryptographicException($"{name} must be {SHA256.HashSizeInBytes} bytes.");
        return bytes;
    }

    internal static byte[] DeriveRawSecret(ECDiffieHellman ownKey, byte[] peerPublicKeyBytes)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKeyBytes, out _);
        return ownKey.DeriveRawSecretAgreement(peer.PublicKey);
    }

    internal static bool FixedTimeEqualsBase64(string expectedBase64, string? suppliedBase64)
    {
        if (string.IsNullOrWhiteSpace(suppliedBase64)) return false;
        try
        {
            byte[] expected = Convert.FromBase64String(expectedBase64);
            byte[] supplied = Convert.FromBase64String(suppliedBase64);
            return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] DecodeBase64(string? base64, string name)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new CryptographicException($"{name} is required.");
        try { return Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new CryptographicException($"{name} is not valid base64.", ex); }
    }

    private static byte[] BuildTranscript(
        string requestId, Guid casterDeviceId, Guid terminalDeviceId,
        byte[] casterPublicKey, byte[] terminalPublicKey, byte[] casterNonce, byte[] terminalNonce)
    {
        using var ms = new MemoryStream();
        WritePart(ms, TranscriptLabel);
        WritePart(ms, Encoding.UTF8.GetBytes(requestId));
        WritePart(ms, casterDeviceId.ToByteArray());
        WritePart(ms, terminalDeviceId.ToByteArray());
        WritePart(ms, casterPublicKey);
        WritePart(ms, terminalPublicKey);
        WritePart(ms, casterNonce);
        WritePart(ms, terminalNonce);
        return ms.ToArray();
    }

    private static void WritePart(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        byte[] combined = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);
        return combined;
    }
}
