# NetworkEngine.Merged - NetMQ 기반 게임 서버 아키텍처

## 🎯 핵심 원칙: 모든 코드는 NetMQ 아키텍처를 따릅니다

이 프로젝트는 **NetMQ 기반의 고성능 분산 게임 서버** 아키텍처를 구현합니다.
**모든 코드 작업 시 반드시 아래의 아키텍처 원칙을 준수해야 합니다.**

## 🚨 **필수 규칙 0: task-tracker (노션 기반 작업 추적)**

**모든 코드 작업은 노션 '네트워크 엔진 할일' DB와 연동하여 추적해야 합니다.**
워크플로우: 문서 검토 → 일감 생성 → TDD(테스트 먼저) → 구현 → 검증 → 완료 보고
상세 플로우는 `.claude/skills/task-tracker/SKILL.md`를 참조하세요.

## 🚨 **필수 규칙 0.1: docs-first (프로젝트 문서 사전 참조)**

**모든 코드 작업 전에 `docs/` 디렉토리의 관련 문서를 반드시 먼저 읽어야 합니다.**
문서에 이미 정의된 패턴, 최적화 기법, 설계 원칙을 무시하고 코드를 작성하는 것은 금지됩니다.
상세 매핑은 `.claude/skills/docs-first/SKILL.md`를 참조하세요.

## 🚨 **필수 규칙 1: netmq-architect 에이전트 우선 사용**

**모든 코드 작업 전에 netmq-architect 에이전트를 먼저 호출하여 아키텍처 검증을 받아야 합니다.**

✅ **에이전트를 반드시 사용해야 하는 경우**:
- Gateway, GameServer, Stateless Service 코드 수정
- 새로운 패킷 핸들러 추가
- 네트워크 통신 로직 변경
- 세션/인증 관련 코드 작성
- Redis/DB 접근 코드 추가
- 라우팅 또는 메시지 전달 로직 수정

❌ **에이전트 없이 코드 작성 금지**:
- 아키텍처 위반 가능성이 있는 모든 코드는 에이전트 검증 필수
- "간단한 수정"도 예외 없음 (아키텍처 위반은 작은 실수에서 시작)

**사용 방법**:
```
Task tool with subagent_type: "netmq-architect"
prompt: "작업 내용 설명 및 아키텍처 검증 요청"
```

---

## 📐 Network Dualism (네트워크 이원론)

이 시스템은 **두 개의 독립적인 네트워크 계층**으로 구성됩니다:

### 1. Game Session Channel (Data Plane)
- **목적**: 클라이언트 게임 데이터 전송 (초저지연)
- **토폴로지**: 1:N Star (Gateway 중심)
- **프로토콜**: NetMQ Router-Router (Direct P2P)
- **참여자**: Gateway ↔ GameServer
- **특징**: Session-Based Routing, Zero-Copy 전송

### 2. Node Service Mesh (Control Plane)
- **목적**: 서버 간 제어/관리 통신 (RPC)
- **토폴로지**: Full Mesh (모든 노드가 직접 연결)
- **프로토콜**: NetMQ Router-Router (Service Mesh)
- **참여자**: Gateway, GameServer, Stateless Services
- **특징**: Request-Response RPC, Redis Registry

**❌ 절대 금지**: TCP + HTTP 혼용, 두 계층의 혼합, 추가 통신 채널 생성

---

## 🏗️ 서버 역할 정의 (엄격히 준수)

### Gateway Server: Dumb Proxy (멍청한 프록시)
**역할**: 오직 I/O와 패킷 전달만 수행

✅ **허용**:
- 클라이언트 TCP 소켓 연결 관리
- 패킷을 고정된(pinned) GameServer로 전달
- GameServer 응답을 클라이언트로 전달

❌ **금지** (절대 구현하지 말 것):
- Opcode 파싱 또는 패킷 분석
- 비즈니스 로직 구현
- Redis/SSOT 직접 접근
- 세션 검증 수행
- 라우팅 결정 (GameServer가 지시한 pinning만 따름)

### GameServer: Smart Hub / BFF (Backend-for-Frontend)
**역할**: 모든 클라이언트 요청의 진입점 및 중앙 허브

✅ **책임**:
- 모든 클라이언트 패킷 파싱 및 분석
- Redis SSOT를 통한 세션 검증
- Gateway에 세션 pinning 지시
- 중복 로그인 처리 (기존 세션 kick-out)
- Stateful 로직 직접 처리 (전투, 이동)
- Stateless 로직을 Service Mesh로 위임 (상점, 우편함)

❌ **금지**:
- 세션 검증을 Gateway나 Stateless Service에 위임
- 클라이언트가 Gateway를 우회해서 직접 접근 허용

### Stateless Services: Internal Web Services
**역할**: 내부 전용 비즈니스 로직 처리

✅ **특징**:
- Service Mesh를 통해서만 접근 가능
- 인증/세션 검증 불필요 (GameServer를 신뢰)
- 순수 비즈니스 로직 구현 (CQRS Query)
- GameServer로 결과 반환

❌ **금지**:
- 클라이언트 또는 Gateway로부터 직접 패킷 수신
- 인증 로직 재구현 (중복 검증)
- 상태 관리 (Stateless여야 함)

---

## 🔄 표준 데이터 플로우

### Stateless 요청 (예: 우편함 조회)
```
Client → Gateway (TCP 전달, 검사 없음)
  ↓
Gateway → GameServer (P2P Direct 전달)
  ↓
GameServer → Stateless Service (Service Mesh RPC)
  ↓
Stateless Service → GameServer (결과 반환)
  ↓
GameServer → Gateway (클라이언트 패킷 생성)
  ↓
Gateway → Client (TCP 응답)
```

### Stateful 요청 (예: 플레이어 이동)
```
Client → Gateway → GameServer (P2P Direct)
  ↓
GameServer 내부 처리 (Actor 시스템)
  ↓
GameServer → Gateway → Client (응답)
```

**❌ 금지된 플로우**:
- Client → Gateway → Stateless Service (Gateway는 라우팅 불가)
- Client → HTTP API → GameServer (이중 통신 채널 금지)
- Stateless Service → Client (직접 응답 불가)

---

## ⚡ 성능 최적화 원칙

모든 코드는 다음을 우선시해야 합니다:

1. **Zero-Copy 패턴**:
   - `Span<T>`, `Memory<T>`, `stackalloc` 활용
   - NetMQ `Msg.Move()` 사용 (소유권 이전)
   - `ArrayPool<byte>` 재사용

2. **최소 할당**:
   - Hot path에서 Heap 할당 최소화
   - Struct 기반 메시지 타입 선호
   - Primary Constructor 사용

3. **비동기 최적화**:
   - `ValueTask` 사용 (성능 중요 경로)
   - `ConfigureAwait(false)` 명시
   - Channel<T> 기반 Actor 모델

---

## 🛡️ 아키텍처 검증 체크리스트

코드 작성/리뷰 시 반드시 확인:

- [ ] Network Dualism 유지? (두 계층 분리)
- [ ] Gateway가 Dumb Proxy 역할만? (로직 없음)
- [ ] GameServer가 Smart Hub 역할? (세션 검증, 라우팅 결정)
- [ ] Stateless Service가 격리됨? (내부 전용, 인증 재구현 없음)
- [ ] 클라이언트가 단일 TCP 연결만 사용?
- [ ] 성능 최적화됨? (할당, 지연시간, 메모리)

---

## 🤖 NetMQ Architect Agent 강제 사용 규칙

**❗ 중요: 모든 아키텍처 관련 코드 작업 시 netmq-architect 에이전트를 먼저 호출해야 합니다.**

### 에이전트 호출이 필수인 작업:

1. **모든 서버 코드 수정**:
   - Gateway, GameServer, Stateless Service 코드 변경
   - 새로운 Controller, Handler, Middleware 추가
   - 기존 네트워크 로직 수정

2. **데이터 플로우 변경**:
   - 새로운 패킷 타입 추가
   - 라우팅 로직 수정
   - 메시지 전달 경로 변경

3. **상태 관리 및 인증**:
   - 세션 저장/조회 로직
   - 인증/권한 검증 코드
   - Redis SSOT 접근 코드

4. **성능 최적화**:
   - NetMQ 설정 변경
   - Zero-Copy 패턴 적용
   - 메모리 할당 최적화

### 에이전트 호출 예외 (호출 불필요):

- 단순 로깅 추가
- 주석 수정
- 테스트 코드 작성 (단, 통합 테스트는 호출 권장)
- 설정 파일 값 변경 (아키텍처 영향 없는 경우)

### 사용 예시:

**❌ 나쁜 예 (에이전트 없이 바로 코드 작성)**:
```
사용자: "우편함 시스템 구현해줘"
어시스턴트: "바로 코드 작성을 시작합니다..." [X]
```

**✅ 좋은 예 (에이전트 먼저 호출)**:
```
사용자: "우편함 시스템 구현해줘"
어시스턴트: "netmq-architect 에이전트로 아키텍처를 먼저 검증하겠습니다."
[Task tool with subagent_type: netmq-architect]
  → 에이전트 결과: "우편함은 Stateless Service로 구현, GameServer가 중재"
  → 이후 코드 작성 시작
```

---

## 📚 필수 참고 문서

모든 개발자는 아래 문서를 숙지해야 합니다:

1. **docs/architecture/01-overview.md**: 전체 아키텍처 개요
2. **docs/architecture/02-game-session-channel.md**: Data Plane 상세
3. **docs/architecture/03-node-service-mesh.md**: Control Plane 상세
4. **docs/architecture/04-library-structure.md**: 프로젝트 계층 구조
5. **docs/architecture/05-type-system.md**: 타입 시스템 (EServerType)

---

## 🚫 금지된 안티패턴

절대 구현하지 말 것:

1. **이중 통신 트랙**: TCP + HTTP/gRPC 동시 노출
2. **Gateway 로직**: 비즈니스 로직, 라우팅 규칙, 토큰 검증
3. **Stateless Service 직접 연결**: 클라이언트 → Stateless Service
4. **인증 중복**: Stateless Service에서 인증 재구현
5. **Gateway SSOT 접근**: Gateway가 Redis/DB 직접 접근
6. **GameServer 우회**: 클라이언트 요청이 GameServer를 건너뜀
7. **게임 로직 Web API**: 클라이언트 대상 REST/GraphQL API
8. **CPU-Bound async/await**: 순수한 연산 집약적(CPU-Bound) 작업에 `async`/`await`이나 `Task.Run`을 불필요하게 사용하여 스레드 풀 오버헤드와 컨텍스트 스위칭 비용을 발생시키는 행위 (Network/I/O Bound 작업에만 한정하여 사용할 것)

---

## 🔧 개발 워크플로우 (엄격히 준수)

### 새 기능 추가 시 (단계별 필수 순서):

**Step 1: 아키텍처 검증 (netmq-architect 에이전트 호출 필수)**
- ✅ Task tool로 netmq-architect 에이전트 호출
- ✅ 작업 내용 및 구현 방향 설명
- ✅ 에이전트의 아키텍처 가이드라인 확인
- ✅ 역할 배치 결정 (Gateway/GameServer/Stateless Service)
- ✅ 데이터 플로우 정의
- ❌ **이 단계를 건너뛰면 PR 거부**

**Step 2: 코드 구현**
- CODINGGUIDE.md 스타일 가이드 준수
- Zero-Copy 패턴 적용
- Primary Constructor, `[]`, record 사용
- 에이전트가 제시한 아키텍처 패턴 따르기

**Step 3: 자체 검증**
- 아키텍처 체크리스트 확인
- Network Dualism 준수 여부
- 역할 경계 명확성

**Step 4: 성능 검증**
- Hot path 할당 체크
- 벤치마크 실행
- 필요 시 netmq-architect 에이전트로 성능 최적화 조언 받기

### 코드 리뷰 시:

**Step 1: netmq-architect 에이전트로 아키텍처 검토**
- 변경된 코드가 아키텍처 원칙을 준수하는지 검증
- 역할 경계 위반 여부 확인

**Step 2: 코드 품질 검토**
- 성능 최적화
- 코딩 스타일

---

## 📦 프로젝트 구조

```
NetworkServer.Protocol (Tier 0)      - 프로토콜 정의
NetworkServer.Infrastructure (Tier 1) - 분산 시스템 인프라
NetworkServer.Node (Tier 2)          - Service Mesh (Control Plane)
NetworkServer.Gateway (Tier 3)       - Gateway 라이브러리
NetworkServer.Game (Tier 3)          - GameServer 라이브러리
GatewaySample (App)                  - Gateway 실행 프로젝트
GameSample (App)                     - GameServer 실행 프로젝트
```

**의존성 규칙**: 하위 계층은 상위 계층을 참조할 수 없음

---

## 🎓 학습 리소스

- **NetMQ 공식 문서**: https://netmq.readthedocs.io/
- **Zero-Copy 패턴**: docs/performance/zero-copy.md
- **Actor 모델**: docs/patterns/actor-model.md
- **Service Mesh**: docs/architecture/03-node-service-mesh.md

---

## ⚠️ 중요: 코드 작성 전 반드시 확인

모든 코드 PR은 다음을 통과해야 합니다:

1. ✅ **netmq-architect 에이전트 검증 완료** (아키텍처 관련 모든 변경)
2. ✅ NetMQ 아키텍처 원칙 준수
3. ✅ 역할 경계 명확 (Gateway/GameServer/Stateless)
4. ✅ Network Dualism 유지
5. ✅ 성능 최적화 (Zero-Copy, 최소 할당)

**위반 시 PR 거부됩니다. 아키텍처 무결성이 최우선입니다.**

### 에이전트 검증 없이 코드 작성 시:
- ❌ 즉시 PR 거부
- ❌ 코드 롤백 요구
- ❌ 아키텍처 재검증 필수

---

> **"Architecture First, Code Second"**
> 모든 코드는 아키텍처의 하인이며, 아키텍처를 위반하는 코드는 삭제됩니다.
