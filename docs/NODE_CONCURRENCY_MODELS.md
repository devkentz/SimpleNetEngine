# Node Service Mesh 동시성 모델 (Node Concurrency Models)

본 문서는 `NodeEventHandler` 계층에서 지원하는 세 가지 실행 전략(Concurrency Models)의 설계 원칙과 구현 방식을 설명합니다.

---

## 1. 개요
Service Mesh를 통한 노드 간 통신(Node-to-Node RPC/Messaging) 시, 요청의 성격에 따라 최적의 실행 모델을 선택해야 합니다. 시스템은 크게 **순차 처리(Sequential)**, **병렬 처리(Parallel)**, **액터 직렬화(Actor-Serialized)** 세 가지 모델을 지원합니다.

각 모델은 `NodeEventHandler`를 상속받는 추상 베이스 클래스로 구현되어 있습니다.

---

## 2. 실행 모델 상세

### 2.1 Sequential (단일 스레드 순차 처리) — `SequentialNodeEventHandler`
모든 들어오는 요청을 하나의 전역 큐에 담아 **완전한 순차 처리**를 보장하는 모델입니다.

*   **동작 방식**: 자체 `QueuedResponseWriter<NodePacket>`(단일 리더 큐)를 소유하여, 모든 요청을 큐에 삽입하고 하나씩 꺼내어 `ProcessPacketInternalAsync`를 호출합니다.
*   **장점**: 상태 관리가 매우 단순하며, 공유 자원에 대한 락(Lock) 없이도 Race Condition을 완벽히 방지할 수 있습니다.
*   **단점**: 특정 요청의 처리가 지연되면 후속 요청들이 모두 대기하게 되는 병목 현상(Head-of-Line Blocking)이 발생할 수 있습니다.
*   **구현체**: `GameNodeEventHandler`, `GatewayNodeEventHandler`
*   **적용 사례**: 글로벌 노드 상태 관리, GameServer/Gateway의 노드 RPC 처리

### 2.2 Parallel (하이브리드 병렬 처리) — `ParallelNodeEventHandler`
`ActorId` 값에 따라 처리 방식이 분기되는 하이브리드 모델입니다.

*   **동작 방식**:
    1.  `ActorId == 0`: fire-and-forget 방식으로 `ProcessPacketInternalAsync`를 즉시 병렬 실행합니다.
    2.  `ActorId != 0`: per-ActorId 큐(`ConcurrentDictionary<long, QueuedResponseWriter>`)에 삽입하여, 같은 ActorId 내에서는 순차 처리, 다른 ActorId 간에는 병렬 처리를 보장합니다.
*   **장점**: ActorId가 없는 순수 Stateless 요청은 최대 처리량으로 병렬 실행되면서, ActorId가 있는 요청은 per-ID 순차성을 자동으로 보장합니다.
*   **단점**: ActorId == 0인 경우 동일 자원에 접근할 때 동시성 제어를 개발자가 직접 구현해야 합니다.
*   **구현체**: `StatelessEventController`
*   **적용 사례**: Stateless Service의 RPC 처리, 조회성 API

### 2.3 Actor-Serialized (액터 기반 직렬화) — `ActorNodeEventHandler`
`INodeActorManager`를 통해 등록된 Actor를 조회하고, Actor의 자체 메일박스로 패킷을 위임하는 모델입니다.

*   **동작 방식**:
    1.  패킷의 `Header.ActorId`로 `INodeActorManager.FindActor(actorId)`를 호출합니다.
    2.  Actor가 존재하면 `actor.Push(packet)`으로 전달합니다.
    3.  각 `NodeActor`가 자체 `QueuedResponseWriter<NodePacket>` 메일박스를 소유하여 per-Actor 순차 처리를 보장합니다.
    4.  Actor를 찾지 못하면 `OnActorNotFound(packet, actorId)` 가상 메서드가 호출됩니다 (기본: 경고 로그 + 패킷 해제).
*   **장점**: 서로 다른 액터 간에는 완전한 병렬 처리가 가능하면서도, 동일 액터 내에서는 완벽한 순차성을 보장합니다. `NodeActor`가 `INodeDispatcher`를 직접 사용하므로 Zero-Copy 패턴이 적용됩니다.
*   **단점**: 액터 관리(생성/삭제) 및 `INodeActorManager` 등록 오버헤드가 발생할 수 있습니다.
*   **적용 사례**: 게임 세션 처리, 유저별 상태 로직, 특정 객체 지향적 도메인 로직

---

## 3. 구현 세부 사항

### 3.1 베이스 클래스 계층

```
NodeEventHandler (abstract)
├── SequentialNodeEventHandler (abstract) — QueuedResponseWriter 소유, ProcessPacketInternalAsync 선언
├── ParallelNodeEventHandler (abstract)   — ActorId 기반 분기, ProcessPacketInternalAsync 선언
└── ActorNodeEventHandler (abstract)      — INodeActorManager → actor.Push(), OnActorNotFound virtual
```

`NodeEventHandler`는 `ProcessPacket(NodePacket)` 하나만 abstract로 선언하며, 동시성 제어는 각 서브클래스가 담당합니다.

### 3.2 서버별 적용

| 서버 타입 | 베이스 클래스 | 구현체 |
| :--- | :--- | :--- |
| **GameServer** | `SequentialNodeEventHandler` | `GameNodeEventHandler` |
| **Gateway** | `SequentialNodeEventHandler` | `GatewayNodeEventHandler` |
| **Stateless Service** | `ParallelNodeEventHandler` | `StatelessEventController` |
| **Actor 기반 서비스** | `ActorNodeEventHandler` | 사용자 정의 |

---

## 4. 선택 가이드라인

| 모델 | 처리 속도 | 상태 관리 난이도 | 주요 사용처 |
| :--- | :--- | :--- | :--- |
| **Sequential** | 낮음 | 매우 쉬움 | GameServer/Gateway 노드 RPC, 전역 설정 변경 |
| **Parallel** | 매우 높음 | 보통 (per-ID 보장) | Stateless Service, 조회성 RPC |
| **Actor-Serialized** | 높음 | 쉬움 (Actor 내부) | 게임 유저 액터, 세션 기반 상태 로직 |

---

## 5. 주의 사항
- **Sequential 모드 주의**: 모든 요청이 하나의 큐를 통과하므로, 핸들러 내부에서 무거운 블로킹 작업이나 긴 대기 시간이 발생하는 비동기 호출을 가급적 피해야 합니다.
- **Parallel 모드 주의**: `ActorId == 0`인 요청은 완전 병렬이므로 공유 자원 접근 시 락이 필요합니다. `ActorId != 0`인 요청은 per-ID 큐가 자동 생성되어 순차 보장됩니다.
- **Actor-Serialized 모드 주의**: `INodeActorManager`에 Actor가 미리 등록되어 있어야 합니다. Actor를 찾지 못하면 `OnActorNotFound`가 호출되며, 기본 동작은 경고 로그 후 패킷 해제입니다.
