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
        private AesGcm? _decryptAesGcm;
        private ulong _decryptCounter;

        /// <summary>
        /// S2C 복호화용 AES-GCM 설정 (Handshake 완료 후 호출)
        /// </summary>
        public void SetDecryptionKey(byte[] aesKey)
        {
            _decryptAesGcm?.Dispose();
            _decryptAesGcm = new AesGcm(aesKey, PacketEncryptor.TagSize);
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

                var endPointHeader = MemoryMarshal.Read<EndPointHeader>(buffer.GetReadSpan(EndPointHeader.SizeOf));

                // Step 1: 암호화된 패킷 복호화
                if (endPointHeader.IsEncrypted)
                {
                    var encryptedPacket = buffer.GetReadSpan(totalSize);
                    buffer.ReadAdvance(totalSize);

                    if (_decryptAesGcm == null ||
                        !PacketEncryptor.TryDecrypt(encryptedPacket, _decryptAesGcm,
                            ref _decryptCounter, PacketEncryptor.DirectionS2C,
                            out var decryptedBuffer, out var decryptedLength))
                        throw new CryptographicException("Decryption failed");

                    try
                    {
                        var decSpan = decryptedBuffer.AsSpan(0, decryptedLength);
                        var decHeader = MemoryMarshal.Read<EndPointHeader>(decSpan);
                        var innerData = decSpan[EndPointHeader.SizeOf..];

                        ParseInnerPacket(packets, decHeader, innerData);
                    }
                    finally
                    {
                        PacketEncryptor.ReturnBuffer(decryptedBuffer);
                    }

                    continue;
                }

                // Step 2: 압축 또는 평문 패킷 처리
                var fullPacketSpan = buffer.GetReadSpan(totalSize);
                var bodySpan = fullPacketSpan[EndPointHeader.SizeOf..];
                buffer.ReadAdvance(totalSize);

                ParseInnerPacket(packets, endPointHeader, bodySpan);
            }

            return packets;
        }

        private static void ParseInnerPacket(List<NetworkPacket> packets, EndPointHeader endPointHeader, ReadOnlySpan<byte> data)
        {
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
            ref readonly var gameHeader = ref NetHeaderHelper.Peek<GameHeader>(data);

            if (endPointHeader.ErrorCode != 0)
            {
                packets.Add(new NetworkPacket(endPointHeader, gameHeader, null!));
                return;
            }

            var parser = AutoGeneratedParsers.GetParserById(gameHeader.MsgId);
            if (parser == null)
                throw new InvalidDataException($"Unknown packet ID: {gameHeader.MsgId}");

            var payloadSize = data.Length - GameHeader.SizeOf;
            var message = parser.ParseFrom(data.Slice(GameHeader.SizeOf, payloadSize));
            packets.Add(new NetworkPacket(endPointHeader, gameHeader, message));
        }

        public void Dispose()
        {
            _decryptAesGcm?.Dispose();
        }
    }
}
