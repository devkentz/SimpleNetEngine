using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// GatewaySession의 암호화 상태를 캡슐화합니다.
/// ECDH P-256 키 교환, ECDSA 서명, AES-256-GCM 암호화/복호화를 담당합니다.
///
/// 스레드 안전성:
/// - Encrypt/Decrypt는 별도 AesGcm 인스턴스 사용 (IO 스레드 간 contention 방지)
/// - AesGcm 교체 시 lock 사용 (SendEncrypted/ProcessSinglePacket과의 TOCTOU 방지)
///
/// 생성 시점: GatewaySession 생성자에서 encryptionEnabled == true일 때만 인스턴스화.
/// encryptionEnabled == false이면 GatewaySession._crypto = null (ECDH 키 생성 자체 생략).
/// </summary>
public sealed class SessionCrypto : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    private readonly ECDsa? _signingKey;
    private byte[]? _ephemeralPublicKeyDer;
    private byte[]? _ephemeralSignature;

    private AesGcm? _encryptAesGcm;
    private AesGcm? _decryptAesGcm;
    private ulong _encryptCounter;
    private ulong _decryptCounter;
    private volatile bool _encryptionActive;

    private volatile bool _disposed;
    private readonly Lock _lock = new();

    // HKDF 파라미터 (클라이언트와 동일해야 함)
    private static readonly byte[] HkdfSalt = "NE-v1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "NetworkEngine-AES256-GCM"u8.ToArray();

    public SessionCrypto(ECDsa? signingKey = null)
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _signingKey = signingKey;
    }

    /// <summary>
    /// 암호화 활성 여부
    /// </summary>
    public bool IsActive => _encryptionActive;

    /// <summary>
    /// Gateway ECDH P-256 공개키 (DER 형식) 반환.
    /// 최초 호출 시 생성 + 서명키가 있으면 ECDSA 서명도 함께 생성.
    /// </summary>
    public byte[] GetEphemeralPublicKey()
    {
        if (_ephemeralPublicKeyDer == null)
        {
            _ephemeralPublicKeyDer = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

            if (_signingKey != null)
            {
                _ephemeralSignature = _signingKey.SignData(
                    _ephemeralPublicKeyDer, HashAlgorithmName.SHA256);
            }
        }

        return _ephemeralPublicKeyDer;
    }

    /// <summary>
    /// ECDH 공개키에 대한 ECDSA 서명 반환 (null = 서명 없음)
    /// </summary>
    public byte[]? GetEphemeralSignature() => _ephemeralSignature;

    /// <summary>
    /// 클라이언트 ECDH 공개키로 SharedSecret 도출 → AES-256 키 생성 → 암호화 활성화.
    /// GameServer가 ServiceMeshActivateEncryptionReq로 클라이언트 공개키를 전달하면 호출됨.
    /// </summary>
    public void DeriveAndActivateEncryption(byte[] clientEphemeralPublicKeyDer)
    {
        if (_disposed)
            return;

        using var clientEcdh = ECDiffieHellman.Create();
        clientEcdh.ImportSubjectPublicKeyInfo(clientEphemeralPublicKeyDer, out _);

        // ECDH Raw SharedSecret 도출 (NIST SP 800-56C 준수: raw secret → HKDF)
        var sharedSecret = _ecdh.DeriveRawSecretAgreement(clientEcdh.PublicKey);

        // HKDF로 AES-256 키 도출 (32 bytes)
        var aesKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            outputLength: 32,
            salt: HkdfSalt,
            info: HkdfInfo);

        CryptographicOperations.ZeroMemory(sharedSecret);
        ActivateEncryption(aesKey);
    }

    /// <summary>
    /// ECDH 키 교환 완료 후 암호화 활성화.
    /// </summary>
    /// <param name="symmetricKey">HKDF로 도출된 AES-256 키 (32 bytes)</param>
    public void ActivateEncryption(byte[] symmetricKey)
    {
        if (symmetricKey.Length != 32)
            throw new ArgumentException("AES-256 key must be 32 bytes", nameof(symmetricKey));

        var encAes = new AesGcm(symmetricKey, PacketEncryptor.TagSize);
        var decAes = new AesGcm(symmetricKey, PacketEncryptor.TagSize);

        CryptographicOperations.ZeroMemory(symmetricKey);

        AesGcm? oldEncrypt, oldDecrypt;
        lock (_lock)
        {
            oldEncrypt = _encryptAesGcm;
            oldDecrypt = _decryptAesGcm;
            _encryptAesGcm = encAes;
            _decryptAesGcm = decAes;
            _encryptCounter = 0;
            _decryptCounter = 0;
            _encryptionActive = true;
        }
        oldEncrypt?.Dispose();
        oldDecrypt?.Dispose();
    }

    /// <summary>
    /// Outbound 암호화. 암호화 비활성 시 false 반환 (호출자가 평문 전송해야 함).
    /// </summary>
    public bool TryEncrypt(ReadOnlySpan<byte> data, out byte[]? encryptedBuffer, out int encryptedLength)
    {
        encryptedBuffer = null;
        encryptedLength = 0;

        if (_encryptAesGcm == null || !_encryptionActive)
            return false;

        return PacketEncryptor.TryEncrypt(data, _encryptAesGcm,
            ref _encryptCounter, PacketEncryptor.DirectionS2C,
            out encryptedBuffer, out encryptedLength);
    }

    /// <summary>
    /// Inbound 복호화. 암호화 비활성 시 false 반환.
    /// </summary>
    public bool TryDecrypt(ReadOnlySpan<byte> data, out byte[]? decryptedBuffer, out int decryptedLength)
    {
        decryptedBuffer = null;
        decryptedLength = 0;

        if (_decryptAesGcm == null || !_encryptionActive)
            return false;

        return PacketEncryptor.TryDecrypt(data, _decryptAesGcm,
            ref _decryptCounter, PacketEncryptor.DirectionC2S,
            out decryptedBuffer, out decryptedLength);
    }

    public void Dispose()
    {
        _disposed = true;
        AesGcm? oldEncrypt, oldDecrypt;
        lock (_lock)
        {
            _encryptionActive = false;
            oldEncrypt = _encryptAesGcm;
            oldDecrypt = _decryptAesGcm;
            _encryptAesGcm = null;
            _decryptAesGcm = null;
        }
        oldEncrypt?.Dispose();
        oldDecrypt?.Dispose();
        _ecdh.Dispose();
    }
}
