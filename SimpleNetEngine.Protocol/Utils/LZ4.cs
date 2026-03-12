using K4os.Compression.LZ4;

namespace SimpleNetEngine.Protocol.Utils
{
    public class Lz4
    {
        private const int MaxBufferSize = 1024 * 1024 * 2; // 2MB
        private byte[] _compressBuffer = new byte[1024 * 10];
        private byte[] _depressBuffer = new byte[1024 * 10];

        // 버퍼 크기를 확장하는 메서드
        private void EnsureBufferSize(ref byte[] buffer, int requiredSize)
        {
            if (requiredSize > MaxBufferSize)
            {
                throw new ArgumentException($"Required buffer size ({requiredSize} bytes) exceeds the maximum allowed size ({MaxBufferSize} bytes).");
            }

            if (buffer.Length < requiredSize)
            {
                int newSize = Math.Min(requiredSize * 2, MaxBufferSize);
                buffer = new byte[newSize]; // 새로운 배열 생성
            }
        }

        public ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> input)
        {
            int maxCompressedSize = LZ4Codec.MaximumOutputSize(input.Length);

            // 버퍼 크기 확인 및 확장
            EnsureBufferSize(ref _compressBuffer, maxCompressedSize);

            // LZ4 압축 수행
            int compressedSize = LZ4Codec.Encode(
                input,                         // 입력 데이터
                _compressBuffer.AsSpan(0, maxCompressedSize) // 출력 버퍼
            );

            // 압축된 데이터를 ReadOnlySpan으로 반환
            return _compressBuffer.AsSpan(0, compressedSize);
        }

        public int Decompress(ReadOnlySpan<byte> compressed, Span<byte> output)
        {
            // LZ4 압축 해제 수행
            return LZ4Codec.Decode(
                compressed,                     // 입력 데이터
                output              // 출력 버퍼
            );
        }
        
        public ReadOnlySpan<byte> Decompress(ReadOnlySpan<byte> compressed, int originalSize)
        {
            // 버퍼 크기 확인 및 확장
            EnsureBufferSize(ref _depressBuffer, originalSize);

            // LZ4 압축 해제 수행
            int decodedSize = LZ4Codec.Decode(
                compressed,                     // 입력 데이터
                _depressBuffer              // 출력 버퍼
            );

            // 압축 해제된 크기가 원본 크기와 일치하지 않으면 오류
            if (decodedSize != originalSize)
            {
                throw new InvalidOperationException("Decompressed size does not match original size.");
            }

            return _depressBuffer.AsSpan(0, originalSize);
        }
    }

}
