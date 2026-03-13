using System.Buffers;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// Gateway 패킷 압축/해제 유틸리티
/// Outbound: GameHeader + Payload를 LZ4 압축 → EndPointHeader.FlagCompressed 설정
/// Inbound: FlagCompressed인 패킷을 LZ4 해제 후 평문으로 변환
///
/// 압축 와이어 포맷: [EndPointHeader(OriginalLength 포함)][LZ4 CompressedData]
/// EndPointHeader.OriginalLength는 해제 시 출력 버퍼 크기 결정에 사용
/// </summary>
public static class PacketCompressor
{
    /// <summary>
    /// Outbound 압축: [EndPointHeader][GameHeader][Payload] → [EndPointHeader(Compressed, OriginalLength)][LZ4Data]
    /// </summary>
    /// <param name="payload">GSCHeader 이후의 데이터: [EndPointHeader][GameHeader][Payload]</param>
    /// <param name="compressionThreshold">이 크기 미만이면 압축 스킵</param>
    /// <param name="compressedBuffer">압축된 전체 패킷 (ArrayPool에서 할당, 호출자가 반환 책임)</param>
    /// <param name="compressedLength">compressedBuffer 내 유효 데이터 길이</param>
    /// <returns>true: 압축 적용됨, false: 압축 스킵 (원본 사용)</returns>
    public static bool TryCompress(
        ReadOnlySpan<byte> payload,
        int compressionThreshold,
        out byte[]? compressedBuffer,
        out int compressedLength)
    {
        compressedBuffer = null;
        compressedLength = 0;

        // EndPointHeader 읽기
        if (!NetHeaderHelper.TryRead<EndPointHeader>(payload, out var endPointHeader))
            return false;

        // Handshake 패킷은 압축하지 않음
        if (endPointHeader.IsHandshake)
            return false;

        // GameHeader + Payload 추출 (EndPointHeader 이후)
        var gameData = NetHeaderHelper.GetPayload<EndPointHeader>(payload);

        if (!NetHeaderHelper.HasHeader<GameHeader>(gameData))
            return false;

        // 임계값 미만이면 스킵
        if (gameData.Length < compressionThreshold)
            return false;

        // LZ4 압축
        var maxCompressedSize = EndPointHeader.SizeOf + LZ4Codec.MaximumOutputSize(gameData.Length);
        compressedBuffer = ArrayPool<byte>.Shared.Rent(maxCompressedSize);

        try
        {
            var encodedSize = LZ4Codec.Encode(gameData, compressedBuffer.AsSpan(EndPointHeader.SizeOf));
            if (encodedSize <= 0 || encodedSize >= gameData.Length)
            {
                return false;
            }

            // 최종 패킷 구성: [EndPointHeader(OriginalLength 포함)][LZ4Data]
            var totalSize = EndPointHeader.SizeOf + encodedSize;
            compressedLength = totalSize;

            var outSpan = compressedBuffer.AsSpan();

            // EndPointHeader 업데이트 (Flags, TotalLength, OriginalLength)
            var newHeader = new EndPointHeader
            {
                TotalLength = totalSize,
                ErrorCode = endPointHeader.ErrorCode,
                Flags = (byte)(endPointHeader.Flags | EndPointHeader.FlagCompressed),
                OriginalLength = gameData.Length
            };
            
            newHeader.Write(outSpan);
            return true;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(compressedBuffer);
            compressedBuffer = null;
            return false;
        }
    }

    /// <summary>
    /// Inbound 해제: [EndPointHeader(Compressed, OriginalLength)][LZ4Data] → [EndPointHeader][GameHeader][Payload]
    /// </summary>
    /// <param name="payload">클라이언트에서 받은 전체 패킷</param>
    /// <param name="decompressedBuffer">해제된 패킷 (ArrayPool에서 할당, 호출자가 반환 책임)</param>
    /// <param name="decompressedLength">decompressedBuffer 내 유효 데이터 길이</param>
    /// <returns>true: 해제 성공, false: 실패</returns>
    public static bool TryDecompress(
        ReadOnlySpan<byte> payload,
        out byte[]? decompressedBuffer,
        out int decompressedLength)
    {
        decompressedBuffer = null;
        decompressedLength = 0;

        if (!NetHeaderHelper.TryRead<EndPointHeader>(payload, out var endPointHeader))
            return false;

        // OriginalLength 검증
        if (endPointHeader.OriginalLength <= 0 || endPointHeader.OriginalLength > 1024 * 1024) // 1MB 상한
            return false;

        var compressedData = NetHeaderHelper.GetPayload<EndPointHeader>(payload);

        // 해제 버퍼 할당: [EndPointHeader] + [원본 GameHeader + Payload]
        var totalSize = EndPointHeader.SizeOf + endPointHeader.OriginalLength;
        decompressedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        decompressedLength = totalSize;

        var outSpan = decompressedBuffer.AsSpan();

        // EndPointHeader 복원 (FlagCompressed 제거, TotalLength 갱신, OriginalLength 클리어)
        var newHeader = new EndPointHeader
        {
            TotalLength = totalSize,
            ErrorCode = endPointHeader.ErrorCode,
            Flags = (byte)(endPointHeader.Flags & ~EndPointHeader.FlagCompressed),
            OriginalLength = 0
        };
        newHeader.Write(outSpan);

        // LZ4 해제
        var decodedSize = LZ4Codec.Decode(compressedData, outSpan[EndPointHeader.SizeOf..]);
        if (decodedSize != endPointHeader.OriginalLength)
        {
            ArrayPool<byte>.Shared.Return(decompressedBuffer);
            decompressedBuffer = null;
            decompressedLength = 0;
            return false;
        }

        return true;
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
