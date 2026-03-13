using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentAssertions;
using SimpleNetEngine.Gateway.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Gateway;

public class PacketEncryptorTests : IDisposable
{
    private readonly byte[] _key;
    private readonly AesGcm _encryptAesGcm;
    private readonly AesGcm _decryptAesGcm;

    public PacketEncryptorTests()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        _encryptAesGcm = new AesGcm(_key, PacketEncryptor.TagSize);
        _decryptAesGcm = new AesGcm(_key, PacketEncryptor.TagSize);
    }

    public void Dispose()
    {
        _encryptAesGcm.Dispose();
        _decryptAesGcm.Dispose();
    }

    /// <summary>
    /// 암호화 → 복호화 라운드트립: 원본 데이터가 정확히 복원되는지 검증
    /// </summary>
    [Fact]
    public void TryEncrypt_TryDecrypt_RoundTrip_RestoresOriginalData()
    {
        var packet = CreatePacket(256);
        ulong encCounter = 0;
        ulong decCounter = 0;

        // 암호화
        var encrypted = PacketEncryptor.TryEncrypt(
            packet, _encryptAesGcm, ref encCounter, PacketEncryptor.DirectionS2C,
            out var encBuf, out var encLen);

        encrypted.Should().BeTrue();
        encBuf.Should().NotBeNull();
        encLen.Should().Be(packet.Length + PacketEncryptor.TagSize, "암호화 후 Tag(16B) 추가");

        // 암호화된 헤더 검증
        var encHeader = MemoryMarshal.Read<EndPointHeader>(encBuf.AsSpan(0, encLen));
        encHeader.IsEncrypted.Should().BeTrue();

        // 복호화
        var decrypted = PacketEncryptor.TryDecrypt(
            encBuf.AsSpan(0, encLen), _decryptAesGcm, ref decCounter, PacketEncryptor.DirectionS2C,
            out var decBuf, out var decLen);

        decrypted.Should().BeTrue();

        // 원본 GameHeader + Payload 복원 검증
        var originalData = packet.AsSpan(EndPointHeader.SizeOf);
        var restoredData = decBuf.AsSpan(EndPointHeader.SizeOf, decLen - EndPointHeader.SizeOf);
        restoredData.ToArray().Should().BeEquivalentTo(originalData.ToArray());

        // 복호화된 헤더: FlagEncrypted 제거
        var decHeader = MemoryMarshal.Read<EndPointHeader>(decBuf.AsSpan(0, decLen));
        decHeader.IsEncrypted.Should().BeFalse();

        PacketEncryptor.ReturnBuffer(encBuf);
        PacketEncryptor.ReturnBuffer(decBuf);
    }

    /// <summary>
    /// Handshake 패킷은 암호화하지 않음
    /// </summary>
    [Fact]
    public void TryEncrypt_HandshakePacket_ReturnsFalse()
    {
        var packet = CreatePacket(128);
        var header = MemoryMarshal.Read<EndPointHeader>(packet);
        header.Flags = EndPointHeader.FlagHandshake;
        header.Write(packet.AsSpan());

        ulong counter = 0;
        var result = PacketEncryptor.TryEncrypt(
            packet, _encryptAesGcm, ref counter, PacketEncryptor.DirectionS2C,
            out var buf, out _);

        result.Should().BeFalse("Handshake 패킷은 항상 평문");
        buf.Should().BeNull();
    }

    /// <summary>
    /// 너무 작은 패킷은 암호화 실패
    /// </summary>
    [Fact]
    public void TryEncrypt_TooSmallPacket_ReturnsFalse()
    {
        var tiny = new byte[EndPointHeader.SizeOf - 1];
        ulong counter = 0;

        var result = PacketEncryptor.TryEncrypt(
            tiny, _encryptAesGcm, ref counter, PacketEncryptor.DirectionS2C,
            out var buf, out _);

        result.Should().BeFalse();
        buf.Should().BeNull();
    }

    /// <summary>
    /// 복호화 시 Tag가 없을 정도로 작은 패킷은 실패
    /// </summary>
    [Fact]
    public void TryDecrypt_TooSmallPayload_ReturnsFalse()
    {
        var tiny = new byte[EndPointHeader.SizeOf + PacketEncryptor.TagSize - 1];
        ulong counter = 0;

        var result = PacketEncryptor.TryDecrypt(
            tiny, _decryptAesGcm, ref counter, PacketEncryptor.DirectionC2S,
            out var buf, out _);

        result.Should().BeFalse();
        buf.Should().BeNull();
    }

    /// <summary>
    /// 변조된 Ciphertext → AuthenticationTagMismatch → 복호화 실패
    /// </summary>
    [Fact]
    public void TryDecrypt_TamperedCiphertext_ReturnsFalse()
    {
        var packet = CreatePacket(128);
        ulong encCounter = 0;
        ulong decCounter = 0;

        PacketEncryptor.TryEncrypt(
            packet, _encryptAesGcm, ref encCounter, PacketEncryptor.DirectionS2C,
            out var encBuf, out var encLen);

        // Ciphertext 변조 (Tag 이후 첫 바이트 플립)
        encBuf![EndPointHeader.SizeOf + PacketEncryptor.TagSize] ^= 0xFF;

        var result = PacketEncryptor.TryDecrypt(
            encBuf.AsSpan(0, encLen), _decryptAesGcm, ref decCounter, PacketEncryptor.DirectionS2C,
            out var decBuf, out _);

        result.Should().BeFalse("변조된 데이터는 인증 실패");
        decBuf.Should().BeNull();

        PacketEncryptor.ReturnBuffer(encBuf);
    }

    /// <summary>
    /// 다른 키로 복호화 시도 → 실패
    /// </summary>
    [Fact]
    public void TryDecrypt_WrongKey_ReturnsFalse()
    {
        var packet = CreatePacket(128);
        ulong encCounter = 0;
        ulong decCounter = 0;

        PacketEncryptor.TryEncrypt(
            packet, _encryptAesGcm, ref encCounter, PacketEncryptor.DirectionS2C,
            out var encBuf, out var encLen);

        // 다른 키로 AesGcm 생성
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        using var wrongAesGcm = new AesGcm(wrongKey, PacketEncryptor.TagSize);

        var result = PacketEncryptor.TryDecrypt(
            encBuf.AsSpan(0, encLen), wrongAesGcm, ref decCounter, PacketEncryptor.DirectionS2C,
            out var decBuf, out _);

        result.Should().BeFalse("잘못된 키로 복호화 불가");
        decBuf.Should().BeNull();

        PacketEncryptor.ReturnBuffer(encBuf);
    }

    /// <summary>
    /// Nonce 카운터가 매 호출마다 증가하는지 검증
    /// </summary>
    [Fact]
    public void TryEncrypt_IncrementsCounter()
    {
        var packet = CreatePacket(64);
        ulong counter = 0;

        PacketEncryptor.TryEncrypt(packet, _encryptAesGcm, ref counter, PacketEncryptor.DirectionS2C, out var buf1, out _);
        counter.Should().Be(1);
        PacketEncryptor.ReturnBuffer(buf1);

        PacketEncryptor.TryEncrypt(packet, _encryptAesGcm, ref counter, PacketEncryptor.DirectionS2C, out var buf2, out _);
        counter.Should().Be(2);
        PacketEncryptor.ReturnBuffer(buf2);
    }

    /// <summary>
    /// ErrorCode가 암호화/복호화 과정에서 보존되는지 검증
    /// </summary>
    [Fact]
    public void Encrypt_PreservesErrorCode()
    {
        var packet = CreatePacket(128);
        var header = MemoryMarshal.Read<EndPointHeader>(packet);
        header.ErrorCode = 42;
        header.Write(packet.AsSpan());

        ulong encCounter = 0;
        ulong decCounter = 0;

        PacketEncryptor.TryEncrypt(packet, _encryptAesGcm, ref encCounter, PacketEncryptor.DirectionS2C, out var encBuf, out var encLen);
        var encHeader = MemoryMarshal.Read<EndPointHeader>(encBuf.AsSpan(0, encLen));
        encHeader.ErrorCode.Should().Be(42);

        PacketEncryptor.TryDecrypt(encBuf.AsSpan(0, encLen), _decryptAesGcm, ref decCounter, PacketEncryptor.DirectionS2C, out var decBuf, out var decLen);
        var decHeader = MemoryMarshal.Read<EndPointHeader>(decBuf.AsSpan(0, decLen));
        decHeader.ErrorCode.Should().Be(42);

        PacketEncryptor.ReturnBuffer(encBuf);
        PacketEncryptor.ReturnBuffer(decBuf);
    }

    /// <summary>
    /// 압축 + 암호화 조합 라운드트립: compress → encrypt → decrypt → decompress
    /// </summary>
    [Fact]
    public void CompressThenEncrypt_DecryptThenDecompress_RoundTrip()
    {
        // 압축 가능한 패킷 생성
        var packet = CreateCompressiblePacket(512);
        ulong encCounter = 0;
        ulong decCounter = 0;

        // Step 1: 압축
        var compressed = PacketCompressor.TryCompress(packet, 32, out var compBuf, out var compLen);
        compressed.Should().BeTrue();

        // Step 2: 암호화
        var encrypted = PacketEncryptor.TryEncrypt(
            compBuf.AsSpan(0, compLen), _encryptAesGcm, ref encCounter, PacketEncryptor.DirectionS2C,
            out var encBuf, out var encLen);
        encrypted.Should().BeTrue();

        // 플래그 검증: Compressed | Encrypted
        var encHeader = MemoryMarshal.Read<EndPointHeader>(encBuf.AsSpan(0, encLen));
        encHeader.IsCompressed.Should().BeTrue();
        encHeader.IsEncrypted.Should().BeTrue();

        // Step 3: 복호화
        var decrypted = PacketEncryptor.TryDecrypt(
            encBuf.AsSpan(0, encLen), _decryptAesGcm, ref decCounter, PacketEncryptor.DirectionS2C,
            out var decBuf, out var decLen);
        decrypted.Should().BeTrue();

        // 복호화 후: Compressed만 남음
        var decHeader = MemoryMarshal.Read<EndPointHeader>(decBuf.AsSpan(0, decLen));
        decHeader.IsCompressed.Should().BeTrue();
        decHeader.IsEncrypted.Should().BeFalse();

        // Step 4: 압축 해제
        var decompressed = PacketCompressor.TryDecompress(
            decBuf.AsSpan(0, decLen), out var decompBuf, out var decompLen);
        decompressed.Should().BeTrue();

        // 원본 GameHeader + Payload 복원 검증
        var originalData = packet.AsSpan(EndPointHeader.SizeOf);
        var restoredData = decompBuf.AsSpan(EndPointHeader.SizeOf, decompLen - EndPointHeader.SizeOf);
        restoredData.ToArray().Should().BeEquivalentTo(originalData.ToArray());

        PacketCompressor.ReturnBuffer(compBuf);
        PacketEncryptor.ReturnBuffer(encBuf);
        PacketEncryptor.ReturnBuffer(decBuf);
        PacketCompressor.ReturnBuffer(decompBuf);
    }

    /// <summary>
    /// ReturnBuffer: null 전달 시 예외 없음
    /// </summary>
    [Fact]
    public void ReturnBuffer_Null_DoesNotThrow()
    {
        var act = () => PacketEncryptor.ReturnBuffer(null);
        act.Should().NotThrow();
    }

    // --- Helpers ---

    private static byte[] CreatePacket(int totalSize)
    {
        var packet = new byte[totalSize];
        Random.Shared.NextBytes(packet);

        var header = new EndPointHeader
        {
            TotalLength = totalSize,
            ErrorCode = 0,
            Flags = 0
        };
        MemoryMarshal.Write(packet.AsSpan(), in header);

        var gameHeader = new GameHeader { MsgId = 1, SequenceId = 1 };
        gameHeader.Write(packet.AsSpan(EndPointHeader.SizeOf));

        return packet;
    }

    private static byte[] CreateCompressiblePacket(int totalSize)
    {
        var packet = new byte[totalSize];

        var header = new EndPointHeader { TotalLength = totalSize, Flags = EndPointHeader.FlagCompressed };
        MemoryMarshal.Write(packet.AsSpan(), in header);

        var gameHeader = new GameHeader { MsgId = 1, SequenceId = 1 };
        gameHeader.Write(packet.AsSpan(EndPointHeader.SizeOf));

        var payloadStart = EndPointHeader.SizeOf + GameHeader.SizeOf;
        for (var i = payloadStart; i < totalSize; i++)
            packet[i] = (byte)(i % 4);

        return packet;
    }
}
