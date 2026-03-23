# Node Service Mesh 동시성 모델 (Node Concurrency Models)

본 문서는 `NodeEventHandler` 계층에서 지원하는 두 가지 실행 전략(Concurrency Models)의 설계 원칙과 구현 방식을 설명합니다.

---

## 1. 개요
Service Mesh를 통한 노드 간 통신(Node-to-Node RPC/Messaging) 시, 요청의 성격에 따라 최적의 실행 모델을 선택해야 합니다. 시스템은 **순차 처리(Sequential)**와 **병렬 처리(Parallel)** 두 가지 모델을 지원합니다.

각 모델은 `NodeEventHandler`를 상속받는 추상 베이스 클래스로 구현되어 있습니다.

---

## 2. 실행 모델 상세

### 2.1 Sequential (싱글 스레드 이벤트 루프) — `SequentialNodeEventHandler`
전용 스레드의 `SingleThreadEventLoop`에서 모든 패킷을 순차 처리하는 모델입니다. Node.js 스타일의 async interleaving을 사용하여, await 시 이벤트 루프를 블로킹하지 않으면서도 싱글 스레드 보장을 유지합니다.

*   **동작 방식**: `SingleThreadEventLoop`(전용 스레드 + custom SynchronizationContext)에 패킷 처리를 Schedule합니다. await에서 양보하면 다음 패킷을 즉시 처리하고, continuation은 같은 이벤트 루프 스레드에서 실행됩니다.
*   **장점**: 싱글 스레드 보장으로 lock 없이 Race Condition을 방지합니다. await 시에도 이벤트 루프가 멈추지 않아 HOL blocking이 발생하지 않습니다.
*   **단점**: await 이후 상태가 변경될 수 있으므로 yield point 이후 mutable state 재검증이 필요합니다.
*   **구현체**: `GameNodeEventHandler`, `GatewayNodeEventHandler`
*   **적용 사례**: 글로벌 노드 상태 관리, GameServer/Gateway의 노드 RPC 처리

### 2.2 Parallel (Per-Task 병렬 처리) — `ParallelNodeEventHandler`
모든 패킷을 독립된 Task로 fire-and-forget 실행하는 순수 병렬 모델입니다.

*   **동작 방식**: 각 패킷마다 `ProcessPacketSafeAsync`를 fire-and-forget으로 실행합니다. 순서 보장 없이 모든 요청이 동시에 처리됩니다.
*   **장점**: 최대 처리량으로 병렬 실행됩니다. Stateless Service처럼 상태가 없는 경우 가장 효율적입니다.
*   **단점**: 공유 자원 접근 시 동시성 제어를 개발자가 직접 구현해야 합니다.
*   **구현체**: `StatelessEventController`
*   **적용 사례**: Stateless Service의 RPC 처리, 조회성 API

---

## 3. 구현 세부 사항

### 3.1 베이스 클래스 계층

```
NodeEventHandler (abstract)
├── SequentialNodeEventHandler (abstract) — SingleThreadEventLoop 소유, ProcessPacketInternalAsync 선언
└── ParallelNodeEventHandler (abstract)   — Per-Task fire-and-forget, ProcessPacketInternalAsync 선언
```

`NodeEventHandler`는 `ProcessPacket(NodePacket)` 하나만 abstract로 선언하며, 동시성 제어는 각 서브클래스가 담당합니다.

### 3.2 서버별 적용

| 서버 타입 | 베이스 클래스 | 구현체 |
| :--- | :--- | :--- |
| **GameServer** | `SequentialNodeEventHandler` | `GameNodeEventHandler` |
| **Gateway** | `SequentialNodeEventHandler` | `GatewayNodeEventHandler` |
| **Stateless Service** | `ParallelNodeEventHandler` | `StatelessEventController` |

---

## 4. 선택 가이드라인

| 모델 | 처리 속도 | 상태 관리 난이도 | 주요 사용처 |
| :--- | :--- | :--- | :--- |
| **Sequential** | 보통 | 매우 쉬움 | GameServer/Gateway 노드 RPC, 전역 설정 변경 |
| **Parallel** | 매우 높음 | 보통 | Stateless Service, 조회성 RPC |

---

## 5. 주의 사항
- **Sequential 모드 주의**: 싱글 스레드에서 실행되므로 블로킹 호출(동기 I/O, Thread.Sleep 등)은 이벤트 루프 전체를 멈춥니다. 반드시 async/await를 사용해야 합니다. await 이후 상태가 변경될 수 있으므로 yield point 전후로 mutable state를 재검증해야 합니다.
- **Parallel 모드 주의**: 모든 요청이 완전 병렬이므로 공유 자원 접근 시 동시성 제어가 필요합니다. Stateless Service처럼 상태가 없는 경우에 적합합니다.
