# 네트워크 패킷 구조 및 시퀀스 명세

**레이어:** Data Plane / Control Plane 통합
**목적:** 네트워킹 이원화(Network Dualism)를 뒷받침하는 패킷 계층 구조 및 Gateway 라우팅 시퀀스 정의

---

## 목차
1. [패킷 3대 구조 및 4대 헤더](#패킷-3대-구조-및-4대-헤더)
2. [이중 헤더(Dual Header) 캡슐화 설계](#이중-헤더dual-header-캡슐화-설계)
3. [Gateway ↔ GameServer 실행 및 처리 시퀀스](#gateway--gameserver-실행-및-처리-시퀀스)

---

## 패킷 3대 구조 및 4대 헤더

시스템은 I/O 오프로딩 아키텍처를 지원하기 위해 통신 구간에 따라 3개의 패킷 채널을 정의하고, 이를 4가지 헤더 종류로 조립합니다.

### 📦 3대 패킷 구조 (통신 구간 기준)

1. **EndPointPacket (C2S)**
   - **구간**: `Client` ↔ `Gateway` (TCP 인터넷 망)
   - **목적**: 무거운 네트워크 I/O 방어, TCP 단편화 조립(Framing), SSL/TLS 기반 Handshake 및 보안을 전담하는 외곽 패킷.
2. **GameSessionChannelPacket (G2G)**
   - **구간**: `Gateway` ↔ `GameServer` (내부 P2P 망)
   - **목적**: 외부에서 받은 암호화된 데이터를 복호화해 껍데기를 벗긴 후, "누구(Session)의 것인가" 식별 표표지를 붙여 비즈니스 서버로 던지는 배달부(Envelope) 패킷.
3. **NodePacket (N2N)**
   - **구간**: `Node` ↔ `Node` (서버 간 Service Mesh)
   - **목적**: 게임 플레이와 무관하게 시스템이 스스로 라우팅(`Pin Session`, `Kickout`)을 제어하기 위한 관리망 패킷.

### 🔖 4대 헤더 맵핑
1. **[Header 1] EndPoint Header**: 패킷 Framing 및 암호화 여부 조율용. 항상 **평문(Plain)** 전송. (`TotalLength`, `Flags`)
2. **[Header 2] Game Header**: 내부망 순수 비즈니스 로직용 헤더. (암호화되어 전송) (`MsgId(OpCode)`, `SequenceId`, `AckSequenceId`)
3. **[Header 3] GSC Header**: Gateway와 GameServer간 통신할 때 사용하는 라우팅 식별자. (`NodeId`, `SocketId`)
4. **[Header 4] Node Header**: 서버 노드 간 메시지 송수신 및 RPC용. (`Sender`, `Target`, `RequestId`)

---

## 이중 헤더(Dual Header) 캡슐화 설계

클라이언트가 전송하는 최종적인 비즈니스 패킷(`EndPointPacket`)은 가장 명확한 캡슐화 형태를 취합니다. 외부 인터넷망을 통과하는 순간은 오직 아래와 같이 처리됩니다.

💡 **클라이언트 송수신 최종 패킷 구조:**
> `EndPointHeader [ GameHeader [ Msg / Payload ] ]`

| 패턴 명칭 | 용도 및 설명 | 구조 예시 |
|---|---|---|
| **Handshake Request/Response** | • TCP 연결 직후 최초 1회만 주고받는 제어 패킷<br/>• 대칭키 교환 및 Reconnect Key 발급 목적<br/>• **전체 평문** 전송 | `EndPointHeader(TotalLength, Flags(Handshake))` |
| **Encrypted Stream Packet** | • 인증 완료 후 주고받는 비즈니스/인게임 패킷<br/>• Gateway가 바깥 헤더를 보고 Payload를 복호화함 | `EndPointHeader(Length, Flags(Encrypted))` + **[ 암호화 시작 ]** `GameHeader` + `MsgPayload` **[ 암호화 끝 ]** |
| **Reconnect Request** | • 네트워크 순단 복구용 우회 패킷 | `EndPointHeader(Length, Flags(Reconnect))` + `Payload(Reconnect Key)` |

---

## Gateway ↔ GameServer 실행 및 처리 시퀀스

최전방 Gateway에서 **Rate Limit, 암호화, 압축 등 무거운 I/O**를 모두 오프로딩(전담)하고, GameServer는 순수 비즈니스 틱에만 집중하는 흐름입니다.

```mermaid
sequenceDiagram
    autonumber
    
    actor Client
    participant Gateway as Gateway (I/O & Security Hub)
    participant GameLibrary as Server Library (GameServer)
    participant GameLogic as Game Logic (GameServer)
    participant Redis as Redis (SSOT)

    %% ---- 1. TCP Connection & I/O Setup ----
    note right of Client: 1. TCP Connection & 커넥션 확립
    Client->>Gateway: [TCP Connect]
    Gateway-->>Gateway: 소켓별 독립 Receive Buffer 할당<br/>Rate Limit(DoS 방어) 트래킹 시작

    %% ---- 2. Handshake & 라우팅 고정 (Pinning) ----
    note right of Client: 2. 신규 접속 Handshake (보안키 협상 및 타겟 고정)
    Gateway-->>Gateway: 1. 보안 협상 (AES 세션키 생성)<br/>2. 임의의 GameServer(Node) 선택 및 PinnedNodeId 확정<br/>   (이 시점에는 Actor 생성 없음)
    
    Client->>Gateway: HandshakeReq (최초 패킷, TCP 접속 후 첫 번째 클라이언트 메시지)
    Gateway->>GameLibrary: [Internal] HandshakeReq 포워딩 (Dumb Proxy)

    %% ---- 3. 익명 Actor 생성 및 통합 응답 (HandshakeRes) ----
    note over Gateway, GameLibrary: 3. HandshakeReq 받시에 Actor 할당 (HandleHandshakeAsync)
    GameLibrary-->>GameLibrary: MsgId = HandshakeReq 확인 → 익명 Actor(Created) 생성<br/>SessionId 발급<br/>Reconnect Key 생성
    GameLibrary->>Gateway: [NodePacket] HandshakeRes (SessionId, ReconnectKey) 발송
    note over Gateway: 💡 Gateway 측 대기 후, 자신이 생성한 암호화 키를 통합하여 클라이언트로 1회 응답
    Gateway->>Client: HandshakeRes (교환된 대칭키, SessionId, Reconnect Key 발급)
    note over Gateway, Client: 💡 이 시점부터 모든 Payload는 암호화/압축 대상이 됨

    %% ---- 4. Login & Authentication ----
    note right of Client: 4. 신규 로그인 (Authentication)
    Client->>Gateway: Login Packet 전송<br/>(EndPointHeader[Encrypted] + [ 암호화된 데이터 ])
    Gateway->>Gateway: 1. Rate Limit 통과<br/>2. 복호화: 순수 [Game Header + MsgPayload] 추출
    
    Gateway->>GameLibrary: (Pinned Routing) 타겟 서버로 포워딩 (+ G2G Header 부착)
    
    GameLibrary->>GameLogic: 로그인 비즈니스 모델 실행 (상태 = Authenticating 진입)
    GameLogic->>Redis: Session 메타데이터 및 NodeId 매핑 저장 (SSOT)
    
    GameLibrary->>Gateway: Login Response (SUCCESS) 전달
    Gateway->>Gateway: Payload 압축 & 암호화 후 Client 전송
    note over GameLogic: 상태 = Active 전환 (인게임 시작)

    %% ---- 5. Encrypted Business Layer ----
    note right of Client: 5. 게임 플레이루프 (Gateway Offloading)
    Client->>Gateway: 인게임 액션 패킷 (암호화) 전송
    Gateway->>Gateway: [I/O 스레드] DoS 방어 및 버퍼 복호화
    Gateway->>GameLibrary: Pinned GameServer로 [G2G + GameHeader + Msg] 다이렉트 전송
    GameLibrary->>GameLogic: Active 상태 검증 후 인게임 모델 동기화 및 실행
    GameLogic->>Gateway: 인게임 결과 
    Gateway->>Gateway: 재 암호화 후 Client 전송
    
    %% ---- 6. Network Disconnect ----
    note right of Client: 6. 연결 해제
    Client-xGateway: TCP Connection Dropped
    Gateway-->>Gateway: I/O 자원 반환, 라우팅 테이블 파기
    Gateway->>GameLibrary: [NodePacket] Client Disconnect 알림

    %% ---- 7. Reconnection (빠른 복구) ----
    note right of Client: 7. 빠른 네트워크 단절 극복 (Reconnect)
    Client->>Gateway: 새로운 TCP Connect
    Client->>Gateway: Reconnect Request (EndPointHeader + 이전 Reconnect Key)
    
    Gateway->>GameLibrary: 임의 노드에 Reconnect 검증 의뢰
    GameLibrary->>Redis: 접속키 검증 및 기존 소유 NodeId 조회
    GameLibrary->>Gateway: [NodePacket] Reroute Socket (방향 재설정)
    Gateway-->>Gateway: 타겟 Node로 라우팅 즉시 복구 (Session Re-Pinning)
```
