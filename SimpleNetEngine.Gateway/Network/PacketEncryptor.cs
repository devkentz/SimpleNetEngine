using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// Gateway 패킷 암호화/복호화 유틸리티 (AES-256-GCM)
/// Outbound: EndPointHeader 이후 데이터를 암호화 → FlagEncrypted 설정
/// Inbound: FlagEncrypted인 패킷을 복호화 후 평문으로 변환
///
/// 암호화 와이어 포맷: [EndPointHeader(FlagEncrypted)][Tag(16B)][Ciphertext]
/// Nonce: Counter-based (전송하지 않음, 양측 카운터 동기화)
///   - [Counter(8B LE)][Direction(1B)][Padding(3B)] = 12 bytes
///   - Direction: 0 = C2S, 1 = S2C
/// </summary>
public static class PacketEncryptor
{
    public const int TagSize = 16; // AES-GCM Authentication Tag
    public const int NonceSize = 12; // AES-GCM Nonce
    public const byte DirectionC2S = 0;
    public const byte DirectionS2C = 1;

    /// <summary>
    /// Outbound 암호화: [EndPointHeader][Data] → [EndPointHeader(Encrypted)][Tag(16B)][Ciphertext]
    /// </summary>
    /// <param name="payload">암호화할 패킷: [EndPointHeader][GameHeader][Payload] 또는 [EndPointHeader][LZ4Data]</param>
    /// <param name="aesGcm">AES-GCM 인스턴스 (S2C용)</param>
    /// <param name="counter">Nonce 카운터 (호출마다 1 증가)</param>
    /// <param name="direction">Nonce 방향 비트 (S2C=1)</param>
    /// <param name="encryptedBuffer">암호화된 패킷 (ArrayPool 할당, 호출자 반환 책임)</param>
    /// <param name="encryptedLength">유효 데이터 길이</param>
    /// <returns>true: 암호화 성공</returns>
    public static bool TryEncrypt(
        ReadOnlySpan<byte> payload,
        AesGcm aesGcm,
        ref ulong counter,
        byte direction,
        out byte[]? encryptedBuffer,
        out int encryptedLength)
    {
        encryptedBuffer = null;
        encryptedLength = 0;

        if (payload.Length < EndPointHeader.Size)
            return false;

        var endPointHeader = EndPointHeader.Read(payload);

        // Handshake 패킷은 암호화하지 않음
        if (endPointHeader.IsHandshake)
            return false;

        var plaintext = payload[EndPointHeader.Size..];

        // Nonce 생성: [Counter(8B)][Direction(1B)][Padding(3B)]
        Span<byte> nonce = stackalloc byte[NonceSize];
        var currentCounter = Interlocked.Increment(ref counter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, currentCounter);
        nonce[8] = direction;
        // nonce[9..11] = 0 (stackalloc zero-initialized)

        // 출력 버퍼: [EndPointHeader][Tag(16B)][Ciphertext]
        var totalSize = EndPointHeader.Size + TagSize + plaintext.Length;
        encryptedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        encryptedLength = totalSize;

        var outSpan = encryptedBuffer.AsSpan();

        // EndPointHeader 업데이트
        var newHeader = new EndPointHeader
        {
            TotalLength = totalSize,
            ErrorCode = endPointHeader.ErrorCode,
            Flags = (byte)(endPointHeader.Flags | EndPointHeader.FlagEncrypted),
            OriginalLength = endPointHeader.OriginalLength
        };
        newHeader.Write(outSpan);

        var tagSpan = outSpan.Slice(EndPointHeader.Size, TagSize);
        var ciphertextSpan = outSpan.Slice(EndPointHeader.Size + TagSize, plaintext.Length);

        // AES-256-GCM 암호화 (EndPointHeader는 AAD로 사용하여 무결성 보호)
        aesGcm.Encrypt(nonce, plaintext, ciphertextSpan, tagSpan, outSpan[..EndPointHeader.Size]);

        return true;
    }

    /// <summary>
    /// Inbound 복호화: [EndPointHeader(Encrypted)][Tag(16B)][Ciphertext] → [EndPointHeader][Data]
    /// </summary>
    /// <param name="payload">암호화된 패킷</param>
    /// <param name="aesGcm">AES-GCM 인스턴스 (C2S용)</param>
    /// <param name="counter">Nonce 카운터 (호출마다 1 증가)</param>
    /// <param name="direction">Nonce 방향 비트 (C2S=0)</param>
    /// <param name="decryptedBuffer">복호화된 패킷 (ArrayPool 할당, 호출자 반환 책임)</param>
    /// <param name="decryptedLength">유효 데이터 길이</param>
    /// <returns>true: 복호화 성공</returns>
    public static bool TryDecrypt(
        ReadOnlySpan<byte> payload,
        AesGcm aesGcm,
        ref ulong counter,
        byte direction,
        out byte[]? decryptedBuffer,
        out int decryptedLength)
    {
        decryptedBuffer = null;
        decryptedLength = 0;

        if (payload.Length < EndPointHeader.Size + TagSize)
            return false;

        var endPointHeader = EndPointHeader.Read(payload);

        var tag = payload.Slice(EndPointHeader.Size, TagSize);
        var ciphertext = payload[(EndPointHeader.Size + TagSize)..];

        // Nonce 생성
        Span<byte> nonce = stackalloc byte[NonceSize];
        var currentCounter = Interlocked.Increment(ref counter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, currentCounter);
        nonce[8] = direction;

        // 출력 버퍼: [EndPointHeader][Plaintext]
        var totalSize = EndPointHeader.Size + ciphertext.Length;
        decryptedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        decryptedLength = totalSize;

        var outSpan = decryptedBuffer.AsSpan();

        // EndPointHeader 복원 (FlagEncrypted 제거, TotalLength 갱신)
        var newHeader = new EndPointHeader
        {
            TotalLength = totalSize,
            ErrorCode = endPointHeader.ErrorCode,
            Flags = (byte)(endPointHeader.Flags & ~EndPointHeader.FlagEncrypted),
            OriginalLength = endPointHeader.OriginalLength
        };
        newHeader.Write(outSpan);

        var plaintextSpan = outSpan.Slice(EndPointHeader.Size, ciphertext.Length);

        try
        {
            // AES-256-GCM 복호화 (EndPointHeader를 AAD로 검증)
            // AAD는 원본 EndPointHeader (암호화 시점의 헤더)
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextSpan, payload[..EndPointHeader.Size]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            // 태그 불일치 = 변조 또는 카운터 desync
            ArrayPool<byte>.Shared.Return(decryptedBuffer);
            decryptedBuffer = null;
            decryptedLength = 0;
            return false;
        }
    }

    /// <summary>
    /// ArrayPool 버퍼 반환 헬퍼
    /// </summary>
    public static void ReturnBuffer(byte[]? buffer)
    {
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
