using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimpleNetEngine.Protocol.Packets;

// C# 11 이상 (static abstract / static virtual in interface)
public interface INetHeader<T> where T : unmanaged, INetHeader<T>
{
    static virtual int SizeOf => Unsafe.SizeOf<T>();   

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual T Read(ReadOnlySpan<byte> span)
        => MemoryMarshal.Read<T>(span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ref readonly T AsRef(ReadOnlySpan<byte> span)
        => ref MemoryMarshal.AsRef<T>(span); 
}

// Write는 ref 확장 메서드로 — 인스턴스 호출처럼 사용 가능
public static class NetHeaderExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(this ref T header, Span<byte> span)
        where T : unmanaged, INetHeader<T>
        => MemoryMarshal.Write(span, in header);
}

// ---------------------------------------------------------
// 1. EndPointHeader (C2S 외부 연결 프레이밍용) - TCP Stream
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EndPointHeader : INetHeader<EndPointHeader>
{
    public int TotalLength;      // 헤더 포함 패킷 전체 길이
    public short ErrorCode;      // 0 = 정상, non-zero = 에러 전용 응답 (Payload 없음)
    public byte Flags;           // 압축/암호화 플래그
    public byte Reserved;        // 예약
    public int OriginalLength;   // 압축 전 원본 크기 (FlagCompressed일 때만 유효, 해제 시 버퍼 크기 결정용)

    public static int SizeOf => Unsafe.SizeOf<EndPointHeader>();

    // Flag 상수
    public const byte FlagEncrypted  = 0x01;
    public const byte FlagCompressed = 0x02;
    public const byte FlagHandshake  = 0x04;

    public bool IsEncrypted  => (Flags & FlagEncrypted)  != 0;
    public bool IsCompressed => (Flags & FlagCompressed) != 0;
    public bool IsHandshake  => (Flags & FlagHandshake)  != 0;
}

// ---------------------------------------------------------
// 2. GameHeader (게임세션 순수 데이터 송수신용)
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameHeader : INetHeader<GameHeader>
{
    public int MsgId;              // 4 bytes
    public ushort SequenceId;      // 2 bytes
    public ushort RequestId;       // 2 bytes (RPC 통신용 결합)

    public static int SizeOf => Unsafe.SizeOf<GameHeader>();
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
public struct GSCHeader : INetHeader<GSCHeader>
{
    public GscMessageType Type;   // 1 byte
    public long GatewayNodeId;    // 8 bytes
    public long SourceNodeId;     // 8 bytes
    public long SessionId;        // 8 bytes

    // Distributed Tracing (OpenTelemetry)
    public long TraceIdHigh;      // 8 bytes
    public long TraceIdLow;       // 8 bytes
    public long SpanId;           // 8 bytes

    public static int SizeOf => Unsafe.SizeOf<GSCHeader>();
}

// ---------------------------------------------------------
// 4. NodeHeader (NodeServiceMesh 노드간 RPC/메시징망)
// ---------------------------------------------------------
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NodeHeader : INetHeader<NodeHeader>
{
    public long Dest;        // 8 bytes
    public long Source;      // 8 bytes
    public long ActorId;     // 8 bytes
    public int MsgId;        // 4 bytes
    public byte IsReply;     // 1 byte
    public int RequestKey;   // 4 bytes

    // Distributed Tracing (OpenTelemetry)
    public long TraceIdHigh;      // 8 bytes
    public long TraceIdLow;       // 8 bytes
    public long SpanId;           // 8 bytes

    public static int SizeOf => Unsafe.SizeOf<NodeHeader>();
}
