# 네트워크 아키텍처 개요

**버전:** 1.0
**최종 업데이트:** 2024-12-01

---

## 목차
1. [전체 아키텍처](#전체-아키텍처)
2. [핵심 개념](#핵심-개념)
3. [두 개의 독립적인 망](#두-개의-독립적인-망)
4. [네트워크 패킷 구조 및 시퀀스 명세](#네트워크-패킷-구조-및-시퀀스-명세)
5. [컴포넌트 계층 구조](#컴포넌트-계층-구조)
6. [통신 흐름](#통신-흐름)

---

## 전체 아키텍처

우리 시스템은 **두 개의 독립적인 네트워크 망**으로 구성됩니다:

```
┌──────────────────────────────────────────────────────────────┐
│                    전체 시스템 아키텍처                        │
└──────────────────────────────────────────────────────────────┘

          ┌─────────────────────────────────────┐
          │  Game Session Channel (Data Plane)      │
          │  - 클라이언트 ↔ 서버 게임 패킷       │
          │  - Gateway 1:N GameServer           │
          │  - 초저지연 (<1ms)                   │
          │  - Stateful (Session 기반)           │
          └─────────────────────────────────────┘
                            │
┌─────────┐                 │                 ┌──────────────┐
│ Client  │◄────────────────┼────────────────►│  Gateway (1) │
└─────────┘                 │                 └──────┬───────┘
                            │                        │ 1:N
                            ▼                        │ P2P
                    ┌──────────────┐                 ├────►GameServer(1)
                    │  GameServer  │◄────────────────┤
                    │   (N개)      │                 ├────►GameServer(2)
                    └──────┬───────┘                 └────►GameServer(N)
                           │
                           │
          ┌────────────────┴────────────────────┐
          │  Node Service Mesh (Control Plane)  │
          │  - 서버 간 제어/관리 통신             │
          │  - 모든 노드 간 Full Mesh            │
          │  - 일반 지연 (~10ms)                 │
          │  - Stateless (RPC 기반)              │
          └─────────────────────────────────────┘
                           │
            ┌──────────────┼──────────────┐
            │              │              │
         Gateway ◄────► GameServer ◄──► Stateless
            │              │              Services
            └──────────────┴──────────────┘
              (모든 노드가 서로 연결)
```

---

## 핵심 개념

### 1. Network Dualism (네트워크 이원성)
시스템은 두 개의 독립적인 네트워크 망으로 분리되어 있으며, 각각 다른 목적과 특성을 가집니다:

- **Game Session Channel**: 게임 데이터 전송 (Data Plane)
- **Node Service Mesh**: 서버 관리 통신 (Control Plane)

### 2. Gateway as Dumb Proxy (게이트웨이는 단순 프록시)
Gateway는 패킷 내용을 분석하지 않고 투명하게 포워딩만 수행합니다:
- Session ID 기반 라우팅
- 패킷 검증 없음 (GameServer가 담당)
- 최소한의 메모리 사용

### 3. GameServer as Smart Hub (게임서버는 스마트 허브)
GameServer는 모든 비즈니스 로직과 상태를 관리합니다:
- Session 생명주기 관리
- 패킷 검증 및 처리
- Stateless Service 호출 (BFF 패턴)

### 4. Stateless Services Isolation (무상태 서비스 격리)
Stateless Services는 Gateway/GameServer와 독립적으로 동작:
- Node Service Mesh를 통해서만 통신
- 사용자 세션 정보 없음
- 수평 확장 가능

---

## 두 개의 독립적인 망

### Game Session Channel (사용자 패킷 망)

**목적**: 클라이언트와 서버 간 게임 데이터 전송

| 속성 | 설명 |
|------|------|
| **레이어** | Data Plane |
| **참여자** | Client, Gateway, GameServer |
| **프로토콜** | ExternalPacket, P2PProtocol |
| **전송 방식** | TCP (Client-Gateway), NetMQ 1:N P2P (Gateway-GameServer) |
| **라우팅** | Session-Based Routing |
| **상태** | Stateful (Session 유지) |
| **성능** | 초저지연 (<1ms) |
| **구현** | GatewaySession, GamePacketRouter, GatewayPacketListener |

**특징**:
- Gateway가 Dumb Proxy로 동작
- Session ID 기반 라우팅
- 1:N P2P로 각 Gateway가 모든 GameServer와 연결
- Gateway 간, GameServer 간 직접 연결은 없음 (Star Topology)
- Redis를 통한 노드 발견 (P2P Discovery)

**상세 문서**: [Game Session Channel](02-game-session-channel.md)

---

### Node Service Mesh (노드 서비스 망)

**목적**: 서버 간 제어 및 관리 통신

| 속성 | 설명 |
|------|------|
| **레이어** | Control Plane |
| **참여자** | Gateway, GameServer, Stateless Services |
| **프로토콜** | ServiceMeshProtocol (RPC) |
| **전송 방식** | NetMQ Router-Router (Full Mesh) |
| **라우팅** | Node ID Routing |
| **상태** | Stateless (요청-응답) |
| **성능** | 일반 지연 (~10ms) |
| **구현** | NodeCommunicator, NodeDispatcher, NodeSender |

**특징**:
- **Full Mesh 토폴로지**: 모든 노드(Gateway, GameServer, Services)가 서로 연결
- Gateway ↔ Gateway, GameServer ↔ GameServer 모두 연결 가능
- RPC 기반 요청-응답 패턴
- 노드 발견 및 Heartbeat
- 분산 락 및 세션 관리
- GameServer가 Stateless Service 호출 (BFF)

**상세 문서**: [Node Service Mesh](03-node-service-mesh.md)

---

## 컴포넌트 계층 구조

### 라이브러리 계층 (Tier 0-3)

```
┌──────────────────────────────────────────────────────┐
│  SimpleNetEngine.Protocol (Tier 0)                     │
│  - Packets, Memory, Utils                            │
│  - 순수 프로토콜 정의                                 │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│  SimpleNetEngine.Infrastructure (Tier 1)               │
│  - Discovery, DistributeLock, NetMQ                  │
│  - 분산 시스템 인프라                                 │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│  SimpleNetEngine.Node (Tier 2)                         │
│  - NodeCommunicator, NodeDispatcher, RemoteNode      │
│  - Node Service Mesh 구현                            │
└────────────────────┬─────────────────────────────────┘
                     │
        ┌────────────┴────────────┐
        ▼                         ▼
┌──────────────┐          ┌──────────────┐
│  Gateway     │          │  GameServer  │
│  (Tier 3)    │          │  (Tier 3)    │
└──────────────┘          └──────────────┘
```

**상세 문서**: [라이브러리 구조](04-library-structure.md)

---

## 통신 흐름

### 1. 클라이언트 로그인 흐름

```
Client                Gateway              GameServer          Redis
  │                      │                      │                │
  │─────Login Req───────►│                      │                │
  │                      │                      │                │
  │                      │───Forward (P2P)─────►│                │
  │                      │                      │                │
  │                      │                      │─Check Session─►│
  │                      │                      │◄───Result──────│
  │                      │                      │                │
  │                      │◄────Pin Session─────│ (Service Mesh) │
  │                      │      (RPC)           │                │
  │◄───Login Res────────│                      │                │
  │                      │◄────Login Res───────│ (P2P)          │
```

**단계**:
1. Client → Gateway: TCP 연결 및 로그인 요청
2. Gateway → GameServer: P2P로 패킷 포워딩 (Dumb Proxy)
3. GameServer: Redis에서 세션 확인
4. GameServer → Gateway: Service Mesh RPC로 Session Pin 요청
5. Gateway: 해당 Client의 Session ID를 GameServer에 고정
6. GameServer → Gateway → Client: 로그인 응답

### 2. 게임 패킷 전송 흐름

```
Client                Gateway              GameServer
  │                      │                      │
  │────Game Packet──────►│                      │
  │                      │                      │
  │                      │───Forward (P2P)─────►│
  │                      │  (Session-Based      │
  │                      │   Routing)           │
  │                      │                      │
  │                      │◄───Response (P2P)───│
  │◄───Game Response────│                      │
```

**특징**:
- Gateway는 Session ID만 보고 라우팅 (Dumb Proxy)
- GameServer에서 패킷 검증 및 처리
- 1:N P2P로 직접 통신 (초저지연)

### 3. Stateless Service 호출 흐름

```
Client    Gateway    GameServer    MailService    Redis
  │          │           │              │           │
  │──Req────►│           │              │           │
  │          │──Fwd─────►│              │           │
  │          │           │──RPC────────►│           │
  │          │           │ (Service     │──Query───►│
  │          │           │  Mesh)       │◄──Data───│
  │          │           │◄─Res─────────│           │
  │          │◄──Res────│              │           │
  │◄─Res────│           │              │           │
```

**특징**:
- GameServer가 BFF 역할 (Backend for Frontend)
- Stateless Service는 Node Service Mesh로만 통신
- Service는 Session 정보 없음 (Stateless)

---

## 주요 설계 원칙

### 1. 관심사의 분리 (Separation of Concerns)
- Game Session Channel: 게임 데이터 전송에 집중
- Node Service Mesh: 서버 관리에 집중

### 2. 단일 책임 원칙 (Single Responsibility)
- Gateway: 라우팅만 담당 (Dumb Proxy)
- GameServer: 비즈니스 로직 처리 (Smart Hub)
- Stateless Services: 특정 도메인 기능만 제공

### 3. 확장성 (Scalability)
- Gateway: 수평 확장 가능 (상태 없음)
- GameServer: Session 단위 샤딩 가능
- Stateless Services: 무제한 수평 확장

### 4. 복원력 (Resilience)
- Game Session Channel: Gateway 장애 시 다른 Gateway로 재연결
- Node Service Mesh: Full Mesh로 단일 노드 장애 시에도 우회 가능
- 노드 발견 및 자동 재연결
- Redis: SSOT (Single Source of Truth)로 세션 복구

---

## 다음 단계

- [Game Session Channel 상세](02-game-session-channel.md)
- [Node Service Mesh 상세](03-node-service-mesh.md)
- [네트워크 패킷 구조 및 시퀀스 명세](07-packet-structure.md)
- [라이브러리 구조](04-library-structure.md)
- [타입 시스템](05-type-system.md)

---

## 참고 자료

### 관련 문서
- [라이브러리 분리 계획서](../planning/2024-12-library-separation.md)
- [타입 통합 계획서](../planning/2024-12-type-unification.md)

### 주요 코드
- `SimpleNetEngine.Protocol/Packets/` - 프로토콜 정의
- `SimpleNetEngine.Gateway/` - Gateway 라이브러리
- `SimpleNetEngine.Game/` - GameServer 라이브러리
- `SimpleNetEngine.Node/` - Service Mesh 구현
- `Sample/` - 실행 프로젝트 (GatewaySample, GameSample, NodeSample)
