using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Google.Protobuf;
using K4os.Compression.LZ4;
using SimpleNetEngine.Protocol.Memory;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.ProtoGenerator;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleNetEngine.Client.Network
{
    /// <summary>
    /// 네트워크 패킷 (불변 record)
    /// IsError == true이면 Message == null (에러 전용 응답, Payload 없음)
    /// </summary>
    public sealed record NetworkPacket(EndPointHeader EndPointHeader, GameHeader GameHeader, IMessage Message)
    {
        public bool IsError => EndPointHeader.ErrorCode != 0;
        public short ErrorCode => EndPointHeader.ErrorCode;
    }

    public sealed class ProtoPacketParser : IPacketParser<NetworkPacket>, IDisposable
    {
        private const int TagSize = 16;
        private const int NonceSize = 12;
        private const byte DirectionS2C = 1;

        private AesGcm? _decryptAesGcm;
        private ulong _decryptCounter;

        /// <summary>
        /// S2C 복호화용 AES-GCM 설정 (Handshake 완료 후 호출)
        /// </summary>
        public void SetDecryptionKey(byte[] aesKey)
        {
            _decryptAesGcm?.Dispose();
            _decryptAesGcm = new AesGcm(aesKey, TagSize);
            _decryptCounter = 0;
        }

        public IReadOnlyList<NetworkPacket> Parse(ArrayPoolBufferWriter buffer)
        {
            var packets = new List<NetworkPacket>();

            while (true)
            {
                // 1. Peek TotalLength (LittleEndian)
                var span = buffer.WrittenSpan;
                if (span.Length < 4)
                    break;

                int totalSize = BinaryPrimitives.ReadInt32LittleEndian(span);
                if (buffer.WrittenCount < totalSize)
                    break;

                if (0 >= totalSize || PacketDefine.MaxPacketSize <= totalSize)
                    throw new InvalidDataException($"Invalid Packet Length : {totalSize}");

                var endPointHeader = MemoryMarshal.Read<EndPointHeader>(buffer.GetReadSpan(EndPointHeader.Size));

                // Step 1: 암호화된 패킷 복호화
                if (endPointHeader.IsEncrypted)
                {
                    var encryptedPacket = buffer.GetReadSpan(totalSize);
                    buffer.ReadAdvance(totalSize);

                    var decryptedPacket = DecryptPacket(encryptedPacket, endPointHeader);
                    if (decryptedPacket == null)
                        throw new CryptographicException("Decryption failed");

                    try
                    {
                        // 복호화된 패킷에서 헤더 재읽기 (ArrayPool 버퍼는 요청보다 클 수 있으므로 TotalLength로 슬라이스)
                        var decHeader = MemoryMarshal.Read<EndPointHeader>(decryptedPacket);
                        var decSpan = decryptedPacket.AsSpan(0, decHeader.TotalLength);
                        var innerData = decSpan[EndPointHeader.Size..];

                        ParseInnerPacket(packets, decHeader, innerData);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(decryptedPacket);
                    }

                    continue;
                }

                buffer.ReadAdvance(EndPointHeader.Size);

                // Step 2: 압축 또는 평문 패킷 처리
                var remainingData = totalSize - EndPointHeader.Size;
                ParseFromBuffer(packets, buffer, endPointHeader, remainingData, totalSize);
            }

            return packets;
        }

        private void ParseInnerPacket(List<NetworkPacket> packets, EndPointHeader endPointHeader, ReadOnlySpan<byte> data)
        {
            // 압축된 패킷: LZ4 해제 후 GameHeader + Payload 파싱
            if (endPointHeader.IsCompressed)
            {
                var originalLength = endPointHeader.OriginalLength;
                if (originalLength <= 0 || originalLength > 1024 * 1024)
                    throw new InvalidDataException($"Invalid OriginalLength: {originalLength}");

                var decompressedBuffer = ArrayPool<byte>.Shared.Rent(originalLength);
                try
                {
                    var decodedSize = LZ4Codec.Decode(data, decompressedBuffer.AsSpan());
                    if (decodedSize != originalLength)
                        throw new InvalidDataException($"Decompression size mismatch: expected={originalLength}, actual={decodedSize}");

                    ParseGamePacket(packets, endPointHeader, decompressedBuffer.AsSpan(0, originalLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decompressedBuffer);
                }
            }
            else
            {
                ParseGamePacket(packets, endPointHeader, data);
            }
        }

        private static void ParseGamePacket(List<NetworkPacket> packets, EndPointHeader endPointHeader, ReadOnlySpan<byte> data)
        {
            var gameHeader = MemoryMarshal.Read<GameHeader>(data);

            if (endPointHeader.ErrorCode != 0)
            {
                packets.Add(new NetworkPacket(endPointHeader, gameHeader, null!));
                return;
            }

            var parser = AutoGeneratedParsers.GetParserById(gameHeader.MsgId);
            if (parser == null)
                throw new InvalidDataException($"Unknown packet ID: {gameHeader.MsgId}");

            var payloadSize = data.Length - GameHeader.Size;
            var message = parser.ParseFrom(data.Slice(GameHeader.Size, payloadSize));
            packets.Add(new NetworkPacket(endPointHeader, gameHeader, message));
        }

        private void ParseFromBuffer(List<NetworkPacket> packets, ArrayPoolBufferWriter buffer,
            EndPointHeader endPointHeader, int remainingSize, int totalSize)
        {
            // 압축된 패킷
            if (endPointHeader.IsCompressed)
            {
                var compressedData = buffer.GetReadSpan(remainingSize);

                var originalLength = endPointHeader.OriginalLength;
                if (originalLength <= 0 || originalLength > 1024 * 1024)
                    throw new InvalidDataException($"Invalid OriginalLength: {originalLength}");

                var decompressedBuffer = ArrayPool<byte>.Shared.Rent(originalLength);
                try
                {
                    var decodedSize = LZ4Codec.Decode(compressedData, decompressedBuffer.AsSpan());
                    if (decodedSize != originalLength)
                        throw new InvalidDataException($"Decompression size mismatch: expected={originalLength}, actual={decodedSize}");

                    ParseGamePacket(packets, endPointHeader, decompressedBuffer.AsSpan(0, originalLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decompressedBuffer);
                }

                buffer.ReadAdvance(remainingSize);
            }
            else
            {
                var gameHeader = MemoryMarshal.Read<GameHeader>(buffer.GetReadSpan(GameHeader.Size));
                buffer.ReadAdvance(GameHeader.Size);

                if (endPointHeader.ErrorCode != 0)
                {
                    packets.Add(new NetworkPacket(endPointHeader, gameHeader, null!));
                    return;
                }

                var parser = AutoGeneratedParsers.GetParserById(gameHeader.MsgId);
                if (parser == null)
                    throw new InvalidDataException($"Unknown packet ID: {gameHeader.MsgId}");

                var payloadSize = totalSize - EndPointHeader.Size - GameHeader.Size;
                var message = parser.ParseFrom(buffer.GetReadSpan(payloadSize));
                buffer.ReadAdvance(payloadSize);

                if (message == null)
                    throw new InvalidCastException($"Cannot cast MsgId:{gameHeader.MsgId} to {nameof(NetworkPacket)}");

                packets.Add(new NetworkPacket(endPointHeader, gameHeader, message));
            }
        }

        private byte[]? DecryptPacket(ReadOnlySpan<byte> encryptedPacket, EndPointHeader endPointHeader)
        {
            if (_decryptAesGcm == null || encryptedPacket.Length < EndPointHeader.Size + TagSize)
                return null;

            var tag = encryptedPacket.Slice(EndPointHeader.Size, TagSize);
            var ciphertext = encryptedPacket[(EndPointHeader.Size + TagSize)..];

            Span<byte> nonce = stackalloc byte[NonceSize];
            var cnt = Interlocked.Increment(ref _decryptCounter);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce, cnt);
            nonce[8] = DirectionS2C;

            var outSize = EndPointHeader.Size + ciphertext.Length;
            var decryptedBuffer = ArrayPool<byte>.Shared.Rent(outSize);
            var outSpan = decryptedBuffer.AsSpan();

            // EndPointHeader 복원 (FlagEncrypted 제거)
            var newHeader = new EndPointHeader
            {
                TotalLength = outSize,
                ErrorCode = endPointHeader.ErrorCode,
                Flags = (byte)(endPointHeader.Flags & ~EndPointHeader.FlagEncrypted),
                OriginalLength = endPointHeader.OriginalLength
            };
            newHeader.Write(outSpan);

            var plaintextSpan = outSpan.Slice(EndPointHeader.Size, ciphertext.Length);

            try
            {
                _decryptAesGcm.Decrypt(nonce, ciphertext, tag, plaintextSpan, encryptedPacket[..EndPointHeader.Size]);
                return decryptedBuffer;
            }
            catch (AuthenticationTagMismatchException)
            {
                ArrayPool<byte>.Shared.Return(decryptedBuffer);
                return null;
            }
        }

        public void Dispose()
        {
            _decryptAesGcm?.Dispose();
        }
    }
}
