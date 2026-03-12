using System.Buffers;
using System.Runtime.InteropServices;
using FluentAssertions;
using SimpleNetEngine.Gateway.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Gateway;

public class PacketCompressorTests
{
    /// <summary>
    /// 압축 → 해제 라운드트립: 원본 데이터가 정확히 복원되는지 검증
    /// </summary>
    [Fact]
    public void TryCompress_TryDecompress_RoundTrip_RestoresOriginalData()
    {
        // Arrange: [EndPointHeader][GameHeader][Payload]
        var payload = new byte[256];
        Random.Shared.NextBytes(payload);

        var gameHeader = new GameHeader { MsgId = 100, SequenceId = 1, RequestId = 0 };
        var endPointHeader = new EndPointHeader
        {
            TotalLength = EndPointHeader.Size + GameHeader.Size + (payload.Length - EndPointHeader.Size - GameHeader.Size),
            ErrorCode = 0,
            Flags = 0
        };

        // 원본 패킷 조립: [EndPointHeader][GameHeader][RandomPayload]
        var originalPacket = new byte[payload.Length];
        MemoryMarshal.Write(originalPacket.AsSpan(), in endPointHeader);
        gameHeader.Write(originalPacket.AsSpan(EndPointHeader.Size));
        // payload 뒤쪽은 이미 랜덤으로 채워짐

        // 반복 데이터로 압축 효율 확보 (랜덤 데이터는 압축이 안 됨)
        var compressiblePacket = CreateCompressiblePacket(512);

        // Act: 압축
        var compressed = PacketCompressor.TryCompress(
            compressiblePacket, compressionThreshold: 32,
            out var compressedBuffer, out var compressedLength);

        compressed.Should().BeTrue("512B 반복 데이터는 압축 가능해야 함");
        compressedBuffer.Should().NotBeNull();
        compressedLength.Should().BeLessThan(compressiblePacket.Length, "압축 후 크기가 줄어야 함");

        // 압축된 헤더 검증
        var compressedHeader = EndPointHeader.Read(compressedBuffer.AsSpan(0, compressedLength));
        compressedHeader.IsCompressed.Should().BeTrue();
        compressedHeader.OriginalLength.Should().Be(compressiblePacket.Length - EndPointHeader.Size);

        // Act: 해제
        var decompressed = PacketCompressor.TryDecompress(
            compressedBuffer.AsSpan(0, compressedLength),
            out var decompressedBuffer, out var decompressedLength);

        decompressed.Should().BeTrue("압축 해제가 성공해야 함");

        // Assert: 원본 GameHeader + Payload 복원 검증
        var originalGameData = compressiblePacket.AsSpan(EndPointHeader.Size);
        var restoredGameData = decompressedBuffer.AsSpan(EndPointHeader.Size, decompressedLength - EndPointHeader.Size);
        restoredGameData.ToArray().Should().BeEquivalentTo(originalGameData.ToArray());

        // 해제된 헤더 검증: FlagCompressed 제거, OriginalLength 클리어
        var decompressedHeader = EndPointHeader.Read(decompressedBuffer.AsSpan(0, decompressedLength));
        decompressedHeader.IsCompressed.Should().BeFalse();
        decompressedHeader.OriginalLength.Should().Be(0);

        // Cleanup
        PacketCompressor.ReturnBuffer(compressedBuffer);
        PacketCompressor.ReturnBuffer(decompressedBuffer);
    }

    /// <summary>
    /// 임계값 미만 패킷은 압축 스킵
    /// </summary>
    [Fact]
    public void TryCompress_BelowThreshold_ReturnsFalse()
    {
        var packet = CreateCompressiblePacket(64); // EndPointHeader(12) + GameHeader(8) + 44B payload

        var result = PacketCompressor.TryCompress(
            packet, compressionThreshold: 128,
            out var buffer, out var length);

        result.Should().BeFalse("임계값(128B) 미만의 GameData는 압축 스킵");
        buffer.Should().BeNull();
        length.Should().Be(0);
    }

    /// <summary>
    /// Handshake 패킷은 압축하지 않음
    /// </summary>
    [Fact]
    public void TryCompress_HandshakePacket_ReturnsFalse()
    {
        var packet = CreateCompressiblePacket(256);

        // Handshake 플래그 설정
        var header = EndPointHeader.Read(packet);
        header.Flags = EndPointHeader.FlagHandshake;
        header.Write(packet.AsSpan());

        var result = PacketCompressor.TryCompress(
            packet, compressionThreshold: 32,
            out var buffer, out var length);

        result.Should().BeFalse("Handshake 패킷은 압축 금지");
        buffer.Should().BeNull();
    }

    /// <summary>
    /// 패킷이 너무 작으면 (EndPointHeader + GameHeader 미만) 압축 스킵
    /// </summary>
    [Fact]
    public void TryCompress_TooSmallPacket_ReturnsFalse()
    {
        var tinyPacket = new byte[EndPointHeader.Size]; // GameHeader도 없는 크기

        var result = PacketCompressor.TryCompress(
            tinyPacket, compressionThreshold: 0,
            out var buffer, out var length);

        result.Should().BeFalse("EndPointHeader + GameHeader 미만 패킷은 처리 불가");
    }

    /// <summary>
    /// 압축된 결과가 원본보다 크면 압축 스킵 (비압축성 데이터)
    /// </summary>
    [Fact]
    public void TryCompress_IncompressibleData_ReturnsFalse()
    {
        // 완전 랜덤 데이터 = LZ4로 압축 불가
        var packet = CreateRandomPacket(256);

        var result = PacketCompressor.TryCompress(
            packet, compressionThreshold: 32,
            out var buffer, out var length);

        // 랜덤 데이터는 압축률이 0이거나 오히려 커지므로 스킵
        result.Should().BeFalse("랜덤 데이터는 LZ4 압축 효과 없음");
        buffer.Should().BeNull();
    }

    /// <summary>
    /// TryDecompress: 패킷이 EndPointHeader보다 작으면 실패
    /// </summary>
    [Fact]
    public void TryDecompress_TooSmallPayload_ReturnsFalse()
    {
        var tiny = new byte[EndPointHeader.Size - 1];

        var result = PacketCompressor.TryDecompress(
            tiny, out var buffer, out var length);

        result.Should().BeFalse();
        buffer.Should().BeNull();
    }

    /// <summary>
    /// TryDecompress: OriginalLength가 0 이하면 실패
    /// </summary>
    [Fact]
    public void TryDecompress_InvalidOriginalLength_ReturnsFalse()
    {
        var packet = new byte[EndPointHeader.Size + 16];
        var header = new EndPointHeader
        {
            TotalLength = packet.Length,
            Flags = EndPointHeader.FlagCompressed,
            OriginalLength = 0 // 잘못된 값
        };
        header.Write(packet.AsSpan());

        var result = PacketCompressor.TryDecompress(
            packet, out var buffer, out var length);

        result.Should().BeFalse("OriginalLength <= 0이면 해제 실패");
    }

    /// <summary>
    /// TryDecompress: OriginalLength가 1MB 초과면 실패 (DoS 방어)
    /// </summary>
    [Fact]
    public void TryDecompress_OriginalLengthExceedsLimit_ReturnsFalse()
    {
        var packet = new byte[EndPointHeader.Size + 16];
        var header = new EndPointHeader
        {
            TotalLength = packet.Length,
            Flags = EndPointHeader.FlagCompressed,
            OriginalLength = 1024 * 1024 + 1 // 1MB + 1 = 상한 초과
        };
        header.Write(packet.AsSpan());

        var result = PacketCompressor.TryDecompress(
            packet, out var buffer, out var length);

        result.Should().BeFalse("1MB 초과 OriginalLength는 거부");
    }

    /// <summary>
    /// EndPointHeader.OriginalLength가 압축 시 설정되고 해제 시 클리어되는지 검증
    /// </summary>
    [Fact]
    public void Compress_SetsOriginalLength_Decompress_ClearsIt()
    {
        var packet = CreateCompressiblePacket(512);
        var originalGameDataLength = packet.Length - EndPointHeader.Size;

        // 압축
        PacketCompressor.TryCompress(packet, 32, out var compBuf, out var compLen);
        var compHeader = EndPointHeader.Read(compBuf.AsSpan(0, compLen));
        compHeader.OriginalLength.Should().Be(originalGameDataLength);

        // 해제
        PacketCompressor.TryDecompress(compBuf.AsSpan(0, compLen), out var decBuf, out var decLen);
        var decHeader = EndPointHeader.Read(decBuf.AsSpan(0, decLen));
        decHeader.OriginalLength.Should().Be(0, "해제 후 OriginalLength는 0이어야 함");

        PacketCompressor.ReturnBuffer(compBuf);
        PacketCompressor.ReturnBuffer(decBuf);
    }

    /// <summary>
    /// TotalLength가 압축/해제 시 정확히 갱신되는지 검증
    /// </summary>
    [Fact]
    public void Compress_UpdatesTotalLength_Correctly()
    {
        var packet = CreateCompressiblePacket(512);

        PacketCompressor.TryCompress(packet, 32, out var compBuf, out var compLen);
        var compHeader = EndPointHeader.Read(compBuf.AsSpan(0, compLen));

        compHeader.TotalLength.Should().Be(compLen, "압축 후 TotalLength == 실제 압축 패킷 길이");
        compLen.Should().BeLessThan(packet.Length);

        // 해제 후 TotalLength 복원
        PacketCompressor.TryDecompress(compBuf.AsSpan(0, compLen), out var decBuf, out var decLen);
        var decHeader = EndPointHeader.Read(decBuf.AsSpan(0, decLen));

        decHeader.TotalLength.Should().Be(decLen, "해제 후 TotalLength == 실제 해제 패킷 길이");
        decLen.Should().Be(packet.Length, "해제 후 패킷 크기 == 원본 크기");

        PacketCompressor.ReturnBuffer(compBuf);
        PacketCompressor.ReturnBuffer(decBuf);
    }

    /// <summary>
    /// ErrorCode가 압축/해제 과정에서 보존되는지 검증
    /// </summary>
    [Fact]
    public void Compress_PreservesErrorCode()
    {
        var packet = CreateCompressiblePacket(512);

        // ErrorCode 설정
        var header = EndPointHeader.Read(packet);
        header.ErrorCode = 42;
        header.Write(packet.AsSpan());

        PacketCompressor.TryCompress(packet, 32, out var compBuf, out var compLen);
        var compHeader = EndPointHeader.Read(compBuf.AsSpan(0, compLen));
        compHeader.ErrorCode.Should().Be(42, "ErrorCode는 압축 후 보존");

        PacketCompressor.TryDecompress(compBuf.AsSpan(0, compLen), out var decBuf, out var decLen);
        var decHeader = EndPointHeader.Read(decBuf.AsSpan(0, decLen));
        decHeader.ErrorCode.Should().Be(42, "ErrorCode는 해제 후 보존");

        PacketCompressor.ReturnBuffer(compBuf);
        PacketCompressor.ReturnBuffer(decBuf);
    }

    /// <summary>
    /// EndPointHeader 크기가 12바이트인지 검증 (OriginalLength 포함)
    /// </summary>
    [Fact]
    public void EndPointHeader_Size_Is12Bytes()
    {
        EndPointHeader.Size.Should().Be(12);
        // Unsafe.SizeOf와 일치하는지도 검증
        var structSize = System.Runtime.CompilerServices.Unsafe.SizeOf<EndPointHeader>();
        structSize.Should().Be(EndPointHeader.Size, "Pack=1 구조체 크기가 상수와 일치해야 함");
    }

    /// <summary>
    /// ReturnBuffer: null 전달 시 예외 없음
    /// </summary>
    [Fact]
    public void ReturnBuffer_Null_DoesNotThrow()
    {
        var act = () => PacketCompressor.ReturnBuffer(null);
        act.Should().NotThrow();
    }

    // --- Helper Methods ---

    /// <summary>
    /// 반복 패턴의 압축 가능한 패킷 생성
    /// [EndPointHeader][GameHeader][반복 데이터]
    /// </summary>
    private static byte[] CreateCompressiblePacket(int totalSize)
    {
        var packet = new byte[totalSize];

        var header = new EndPointHeader
        {
            TotalLength = totalSize,
            ErrorCode = 0,
            Flags = 0
        };
        MemoryMarshal.Write(packet.AsSpan(), in header);

        var gameHeader = new GameHeader { MsgId = 1, SequenceId = 1 };
        gameHeader.Write(packet.AsSpan(EndPointHeader.Size));

        // 반복 패턴으로 채움 (LZ4 압축 효율 확보)
        var payloadStart = EndPointHeader.Size + GameHeader.Size;
        for (var i = payloadStart; i < totalSize; i++)
            packet[i] = (byte)(i % 4);

        return packet;
    }

    /// <summary>
    /// 완전 랜덤 데이터 패킷 생성 (압축 불가)
    /// </summary>
    private static byte[] CreateRandomPacket(int totalSize)
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
        gameHeader.Write(packet.AsSpan(EndPointHeader.Size));

        return packet;
    }
}
