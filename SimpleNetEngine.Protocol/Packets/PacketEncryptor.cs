using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SimpleNetEngine.Protocol.Packets;

/// <summary>
/// 패킷 암호화/복호화 유틸리티 (AES-256-GCM)
/// Gateway(서버)와 Client 양쪽에서 공용으로 사용.
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

        if (!NetHeaderHelper.TryRead<EndPointHeader>(payload, out var endPointHeader))
            return false;

        // Handshake 패킷은 암호화하지 않음
        if (endPointHeader.IsHandshake)
            return false;

        var plaintext = NetHeaderHelper.GetPayload<EndPointHeader>(payload);

        // Nonce 생성: [Counter(8B)][Direction(1B)][Padding(3B)]
        Span<byte> nonce = stackalloc byte[NonceSize];
        var currentCounter = Interlocked.Increment(ref counter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, currentCounter);
        nonce[8] = direction;

        // 출력 버퍼: [EndPointHeader][Tag(16B)][Ciphertext]
        var totalSize = EndPointHeader.SizeOf + TagSize + plaintext.Length;
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

        var tagSpan = outSpan.Slice(EndPointHeader.SizeOf, TagSize);
        var ciphertextSpan = outSpan.Slice(EndPointHeader.SizeOf + TagSize, plaintext.Length);

        // AES-256-GCM 암호화 (EndPointHeader는 AAD로 사용하여 무결성 보호)
        aesGcm.Encrypt(nonce, plaintext, ciphertextSpan, tagSpan, outSpan[..EndPointHeader.SizeOf]);

        return true;
    }

    /// <summary>
    /// Inbound 복호화: [EndPointHeader(Encrypted)][Tag(16B)][Ciphertext] → [EndPointHeader][Data]
    /// </summary>
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

        if (!NetHeaderHelper.TryRead<EndPointHeader>(payload, out var endPointHeader))
            return false;

        if (payload.Length < EndPointHeader.SizeOf + TagSize)
            return false;

        var tag = payload.Slice(EndPointHeader.SizeOf, TagSize);
        var ciphertext = payload[(EndPointHeader.SizeOf + TagSize)..];

        // Nonce 생성
        Span<byte> nonce = stackalloc byte[NonceSize];
        var currentCounter = Interlocked.Increment(ref counter);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, currentCounter);
        nonce[8] = direction;

        // 출력 버퍼: [EndPointHeader][Plaintext]
        var totalSize = EndPointHeader.SizeOf + ciphertext.Length;
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

        var plaintextSpan = outSpan.Slice(EndPointHeader.SizeOf, ciphertext.Length);

        try
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextSpan, payload[..EndPointHeader.SizeOf]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
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
