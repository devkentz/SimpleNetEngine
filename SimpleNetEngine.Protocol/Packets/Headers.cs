using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimpleNetEngine.Protocol.Packets;

// ---------------------------------------------------------
// 1. EndPointHeader (C2S 외부 연결 프레이밍용) - TCP Stream
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EndPointHeader
{
    public int TotalLength;      // 헤더 포함 패킷 전체 길이
    public short ErrorCode;      // 0 = 정상, non-zero = 에러 전용 응답 (Payload 없음)
    public byte Flags;           // 압축/암호화 플래그
    public byte Reserved;        // 향후 확장용
    public int OriginalLength;   // 압축 전 원본 크기 (FlagCompressed일 때만 유효, 해제 시 버퍼 크기 결정용)

    public const int Size = sizeof(int) + sizeof(short) + sizeof(byte) + sizeof(byte) + sizeof(int); // 12 bytes

    // Flag 상수
    public const byte FlagEncrypted  = 0x01;
    public const byte FlagCompressed = 0x02;
    public const byte FlagHandshake  = 0x04;

    public bool IsEncrypted  => (Flags & FlagEncrypted) != 0;
    public bool IsCompressed => (Flags & FlagCompressed) != 0;
    public bool IsHandshake  => (Flags & FlagHandshake) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EndPointHeader Read(ReadOnlySpan<byte> span)
    {
        return MemoryMarshal.Read<EndPointHeader>(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(Span<byte> span)
    {
        MemoryMarshal.Write(span, in this);
    }
}

// ---------------------------------------------------------
// 2. GameHeader (게임세션 순수 데이터 송수신용)
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameHeader
{
    public int MsgId;              // 4 bytes
    public ushort SequenceId;      // 2 bytes
    public ushort RequestId;       // 2 bytes (RPC 통신용 결합)

    public const int Size = sizeof(int) + sizeof(ushort) + sizeof(ushort); // = 8 bytes

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GameHeader Read(ReadOnlySpan<byte> span)
    {
        return MemoryMarshal.Read<GameHeader>(span);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(Span<byte> span)
    {
        MemoryMarshal.Write(span, in this);
    }
}

// ---------------------------------------------------------
// 3. GSCHeader (GameSessionChannel 내부망 브릿지용 헤더)
// ---------------------------------------------------------
public enum GscMessageType : byte
{
    ClientPacket = 1,
    ServerPacket = 2,
    Control = 3,
    ClientConnected = 4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GSCHeader
{
    public GscMessageType Type;   // 1 byte
    public long GatewayNodeId;    // 8 bytes
    public long SourceNodeId;     // 8 bytes
    public long SessionId;        // 8 bytes

    // Distributed Tracing (OpenTelemetry)
    public Guid TraceId;          // 16 bytes
    public long SpanId;           // 8 bytes

    public const int Size = sizeof(byte) + sizeof(long) * 3 + 16 + 8; // 1 + 24 + 8 + 16 + 8 = 49 bytes (SocketId 제거)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GSCHeader Read(ReadOnlySpan<byte> span)
    {
        return MemoryMarshal.Read<GSCHeader>(span);
    }
}

// ---------------------------------------------------------
// 4. NodeHeader (NodeServiceMesh 노드간 RPC/메시징망)
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NodeHeader
{
    public long Dest;        // 8 bytes
    public long Source;      // 8 bytes
    public long ActorId;     // 8 bytes
    public int MsgId;        // 4 bytes
    public byte IsReply;     // 1 byte
    public int RequestKey;   // 4 bytes
    
    // Distributed Tracing (OpenTelemetry)
    public Guid TraceId;          // 16 bytes
    public long SpanId;           // 8 bytes

    public const int Size = sizeof(long) * 3 + sizeof(int) * 2 + sizeof(byte) + 16 + 8; // 24 + 8 + 1 + 16 + 8 = 57 bytes

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeHeader Read(ReadOnlySpan<byte> span)
    {
        return MemoryMarshal.Read<NodeHeader>(span);
    }
}
