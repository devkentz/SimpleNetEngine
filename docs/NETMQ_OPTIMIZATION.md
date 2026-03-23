# NetMQ 성능 최적화 가이드

본 문서는 프로젝트 내 노드 간 통신(Node ServiceMesh)을 담당하는 `NodeCommunicator` 및 NetMQ 기반 소켓 통신의 성능을 극대화하기 위한 설계 원칙과 최적화 기법을 정리합니다.

## 1. 개요
고성능 네트워크 엔진에서 가장 큰 병목은 잦은 메모리 할당(`new byte[]`)으로 인한 **가비지 컬렉션(GC) 스파이크**와, 스레드 간 데이터 전달 시 발생하는 **메모리 복사 오버헤드**입니다. 이를 해결하기 위해 NetMQ의 `Msg` 구조체와 .NET의 `ArrayPool<byte>`를 결합한 제로 카피(Zero-copy) 및 메모리 풀링 기법을 적용합니다.

---

## 2. 메모리 풀링 (ArrayPool 기반 커스텀 BufferPool)

NetMQ는 기본적으로 내부 버퍼 풀(`GCBufferPool`)을 사용하지만, 대규모 동시 접속 환경에서는 .NET BCL의 `ArrayPool<byte>`를 사용하는 것이 성능상 압도적으로 유리합니다. 특히 범용 `Shared` 풀 대신 네트워크 전용으로 **독립된 풀(`ArrayPool<byte>.Create`)**을 구성하여 자원 격리와 최적의 튜닝을 달성합니다.

### 왜 `Shared` 대신 `Create()`를 사용하는가?
1. **자원 격리**: JSON 직렬화기나 ASP.NET Core 등 다른 시스템이 버퍼를 고갈시켜도 네트워크 엔진(NetMQ)은 안정적으로 버퍼를 공급받을 수 있습니다.
2. **커스텀 튜닝**: 게임 패킷의 특성과 동시 처리량에 맞춰 최대 배열 크기와 버킷당 개수를 세밀하게 조절할 수 있습니다.

### 커스텀 IBufferPool 구현

```csharp
using System.Buffers;
using NetMQ;

namespace SimpleNetEngine.Infrastructure.NetMQ
{
    public class DedicatedNetMQBufferPool : IBufferPool
    {
        private readonly ArrayPool<byte> _pool;

        public DedicatedNetMQBufferPool(int maxArrayLength = 1024 * 64, int maxArraysPerBucket = 50000)
        {
            // 네트워크 엔진 전용으로 거대한 크기의 독립 풀 생성
            _pool = ArrayPool<byte>.Create(maxArrayLength, maxArraysPerBucket);
        }

        public byte[] Take(int size)
        {
            return _pool.Rent(size);
        }

        public void Return(byte[] buffer)
        {
            _pool.Return(buffer);
        }
    }
}
```

### 적용 방법
애플리케이션 시작 시(예: `Program.cs` 또는 HostedService 초기화 단계) 글로벌 풀을 교체합니다.

```csharp
NetMQ.BufferPool.SetCustomBufferPool(new DedicatedNetMQBufferPool());
```

---

## 3. 제로 카피 송수신 (Zero-Copy Send / Recv)

### 송신 (Send) 최적화: `InitPool` 활용
`Msg.InitPool(size)`를 호출하면 앞서 등록한 커스텀 버퍼 풀(`DedicatedNetMQBufferPool`)에서 버퍼를 빌려옵니다. `Span`을 이용해 직렬화하면 중간에 `byte[]` 복사가 발생하지 않습니다.

```csharp
public void SendOptimized(byte[] identity, IMessage message)
{
    int payloadSize = message.CalculateSize();
    
    var payloadMsg = new Msg();
    payloadMsg.InitPool(payloadSize); // 커스텀 풀에서 버퍼 대여
    
    // 복사 없이 직접 Write
    message.WriteTo(payloadMsg.Slice());

    var identityMsg = new Msg();
    identityMsg.InitGC(identity, identity.Length);

    try
    {
        // RouterSocket의 경우 Identity 프레임 선전송 (More 플래그)
        _routerSocket.Send(ref identityMsg, true);
        _routerSocket.Send(ref payloadMsg, false);
    }
    finally
    {
        // [중요] Send 직후 Close 호출
        identityMsg.Close();
        payloadMsg.Close();
    }
}
```

### 수신 (Receive) 최적화: 소유권 이동 (Move)
수신 시에는 `msg.Move`를 사용하여 버퍼의 소유권을 포인터만으로 이전합니다.

```csharp
public void OnReceiveReady(object sender, NetMQSocketEventArgs e)
{
    Msg identityMsg = new Msg();
    Msg bodyMsg = new Msg();

    identityMsg.InitEmpty();
    bodyMsg.InitEmpty();

    try
    {
        // 소켓 버퍼에서 직접 수신 (복사 없음)
        e.Socket.Receive(ref identityMsg);
        if (identityMsg.HasMore)
        {
            e.Socket.Receive(ref bodyMsg);
        }

        // 수신된 bodyMsg의 소유권을 NodePacket으로 이전 (Move)
        var nodePacket = NodePacket.Create(ref bodyMsg);
        
        OnProcessPacket?.Invoke(nodePacket);
    }
    finally
    {
        identityMsg.Close();
        bodyMsg.Close(); // 이미 Move되었다면 무시됨
    }
}
```

---

## 4. GameSessionChannel 라우팅 제로 카피 (Headroom 예약)

GameServer에서 클라이언트로 보내는 응답 패킷을 생성할 때, Gateway에서 발생하는 **버퍼 재할당 및 복사 오버헤드를 없애기 위해** 사전에 `EndPointHeader` 공간을 포함하여 메모리를 구성(Pre-allocation)하는 강력한 최적화 기법입니다.

### 패킷 메모리 레이아웃 전략
Gateway를 거쳐 Client로 전달되는 패킷은 다음과 같이 하나의 연속된 버퍼로 할당되어 전송됩니다.
`[GSCHeader] | [EndPointHeader] | [GameHeader] | [Message Payload]`

1. **GameServer (직렬화 및 Headroom 확보)**
   - GameServer는 응답 패킷을 만들 때, Gateway와 Client 간의 TCP 프레이밍 규칙인 `EndPointHeader`가 들어갈 자리를 미리 계산하여 포함시킵니다.
   - 이때 `EndPointHeader.TotalLength`는 **EndPointHeader의 크기(4) + GameHeader의 크기(8) + Message의 크기**를 정확히 합산하여 기록합니다.

2. **Gateway (복사 없는 슬라이싱 라우팅)**
   - Gateway는 수신된 패킷에서 `GSCHeader`만 읽어 타겟 클라이언트를 확인합니다.
   - 이후 데이터를 새 버퍼로 복사하지 않고, `EndPointHeader`부터 시작하는 나머지 데이터 영역을 단순히 **Slice(포인터 오프셋 이동)**만 수행하여 TCP `Socket.SendAsync`로 넘깁니다.
   - **결과: Gateway 노드의 할당(Allocation) 0, 복사(Copy) 0.**

### 구현 예시 (GameServer측 패킷 생성)
```csharp
public static Msg CreateOptimizedReplyPacket(GSCHeader routeHeader, int msgId, IMessage replyMsg)
{
    int payloadSize = replyMsg.CalculateSize();
    
    // TotalLength 계산: EndPointHeader(자신 포함) + GameHeader + Payload
    int tcpTotalLength = EndPointHeader.Size + GameHeader.Size + payloadSize;
    
    // 전체 Msg 크기 계산: GSCHeader + tcpTotalLength
    int totalSize = GSCHeader.Size + tcpTotalLength;
    
    Msg msg = new Msg();
    msg.InitPool(totalSize); // NetMQ 내부 풀(또는 커스텀 풀)에서 단일 할당
    
    var span = msg.Slice();
    int offset = 0;
    
    // 1. [GSC Header] 기록 (내부 라우팅용)
    MemoryMarshal.Write(span.Slice(offset, GSCHeader.Size), in routeHeader);
    offset += GSCHeader.Size;
    
    // 2. [EndPoint Header] 기록 (클라이언트가 해석할 프레이밍 헤더)
    var endPointHeader = new EndPointHeader { TotalLength = tcpTotalLength };
    MemoryMarshal.Write(span.Slice(offset, EndPointHeader.Size), in endPointHeader);
    offset += EndPointHeader.Size;
    
    // 3. [Game Header] 기록
    var gameHeader = new GameHeader { MsgId = msgId /*, SequenceId 등 */ };
    MemoryMarshal.Write(span.Slice(offset, GameHeader.Size), in gameHeader);
    offset += GameHeader.Size;
    
    // 4. [Message Payload] 기록
    replyMsg.WriteTo(span.Slice(offset, payloadSize));
    
    return msg;
}
```

### 주의 사항
- **메모리 수명 주기 (Lifecycle):** Gateway에서 Slice된 `Msg`의 버퍼를 비동기 TCP 전송(`await SendAsync`)에 사용할 경우, **전송이 완전히 끝날 때까지 해당 `Msg`를 `Close()`하여 풀로 반환해서는 안 됩니다.**
- **관심사 분리:** GameServer 비즈니스 로직(UserController)이 `EndPointHeader`를 직접 알게 하지 말고, Middleware나 패킷 전송 유틸리티 클래스 등에서 캡슐화하여 덧붙이는 구조로 설계해야 합니다.

---

## 5. Msg 수명 주기 (Lifecycle) 핵심 원칙

1. **Send 직후 항상 Close 호출**
   - `_socket.Send(ref msg)`가 성공하면 내부적으로 **`Move`**가 발생하여 호출 측의 `msg`는 비워집니다.
   - 전송 직후 `msg.Close()`를 호출하는 것은 매우 안전하며, 예외 상황(네트워크 오류 등) 발생 시 누수를 방지하는 권장 패턴입니다.
   - 실제 버퍼 반환은 NetMQ 내부 엔진이 전송을 완전히 끝내고 내부 참조 카운트가 0이 될 때 `IBufferPool.Return`을 통해 자동 수행됩니다.
2. **InitExternal은 사용하지 않음**
   - 최신 NetMQ 코어에서는 콜백을 지원하는 `InitExternal`이 존재하지 않습니다.
   - 따라서 외부 풀을 수동으로 연동하는 대신, 글로벌 `IBufferPool`을 `SetCustomBufferPool`로 등록하고 **`InitPool`**을 호출하여 라이브러리에 생명 주기 관리를 위임하는 것이 정석입니다.
