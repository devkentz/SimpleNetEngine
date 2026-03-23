# 라이브러리 계층 분리 계획서

**작성일:** 2024-12-01
**상태:** ✅ 완료
**커밋:** `6dd47b8`

---

## 1. 배경 및 문제점

### 현재 상황
- **SimpleNetEngine.Common** (22개 파일): 프로토콜(Tier 0) + 인프라(Tier 1)가 혼재
- **SimpleNetEngine.Node** (33개 파일): Service Mesh 구현이지만 Common에 강하게 의존
- **GatewayServer와 GameServer**: 각각 특화된 기능이 있지만 공통 라이브러리에 섞여있음

### 문제점
1. **의존성 불명확**: 각 서버가 실제로 무엇을 사용하는지 파악 어려움
2. **테스트 어려움**: 큰 라이브러리는 모킹이 어렵고 테스트 범위가 넓음
3. **재사용성 낮음**: Protocol만 필요한 경우에도 전체 Common을 참조해야 함
4. **유지보수성**: 변경 시 영향 범위 파악 어려움

### 목표
명확한 계층 구조를 갖춘 5개 패키지로 분리하여:
- Protocol (Tier 0): 순수 프로토콜 정의
- Infrastructure (Tier 1): 공유 인프라
- Node (Tier 2): Service Mesh 로직
- Gateway/Game (Tier 3): 서버별 특화 기능

---

## 2. 새로운 패키지 구조

### SimpleNetEngine.Protocol (Tier 0 - 프로토콜)
**역할**: 네트워크 프로토콜 정의 및 기본 유틸리티

```
SimpleNetEngine.Protocol/
├── Packets/
│   ├── Header.cs                    # 클라이언트 패킷 헤더
│   ├── Packet.cs                    # ExternalPacket (Gateway ↔ Client)
│   ├── InternalPacket.cs            # InternalPacket (노드 간 통신)
│   ├── P2PProtocol.cs               # P2P 프로토콜 (Gateway ↔ GameServer)
│   ├── ServiceMeshProtocol.cs       # Service Mesh 프로토콜
│   └── PacketConfig.cs              # 패킷 상수 정의
├── Memory/
│   └── ArrayPoolBufferWriter.cs     # 버퍼 풀 관리
└── Utils/
    ├── HashHelper.cs                # 해시 유틸
    ├── IpFinder.cs                  # IP 검색
    ├── LZ4.cs                       # 압축
    └── UniqueIdGenerator.cs         # ID 생성
```

### SimpleNetEngine.Infrastructure (Tier 1 - 인프라)
**역할**: 분산 시스템 인프라 컴포넌트

```
SimpleNetEngine.Infrastructure/
├── Discovery/
│   ├── IP2PDiscoveryService.cs      # P2P 노드 발견 인터페이스
│   ├── RedisP2PDiscoveryService.cs  # Redis 기반 구현
│   └── P2PNodeInfo.cs               # 노드 정보
├── DistributeLock/
│   └── RedisDistributeLock.cs       # Redis 분산 락
└── NetMQ/
    └── MsgDisposable.cs             # NetMQ Msg 래퍼
```

### SimpleNetEngine.Node (Tier 2 - Service Mesh)
**역할**: 노드 간 클러스터 통신 및 RPC

```
SimpleNetEngine.Node/
├── Core/
│   ├── NodeDispatcher.cs            # RPC 핸들러 라우팅
│   ├── NodeManager.cs               # 원격 노드 관리
│   ├── NodeService.cs               # 클러스터 생명주기
│   └── NodeMetadataKeys.cs          # 메타데이터 키
├── Network/
│   ├── NodeCommunicator.cs          # NetMQ 노드 통신
│   ├── NodeSender.cs                # RPC 송신 구현
│   └── RemoteNode.cs                # 원격 노드 표현
└── Config/
    └── NodeConfig.cs                # 노드 설정
```

---

## 3. 패키지 간 의존성 그래프

```
┌─────────────────┐
│  Protocol (T0)  │
└────────┬────────┘
         │
         ▼
┌──────────────────┐
│Infrastructure(T1)│
└────────┬─────────┘
         │
         ▼
┌──────────────────┐
│   Node (T2)      │
└────────┬─────────┘
         │
    ┌────┴────┐
    ▼         ▼
┌────────┐ ┌─────┐
│Gateway │ │Game │
└────────┘ └─────┘
   (T3)      (T3)
```

**의존성 규칙**:
- 하위 계층은 상위 계층을 참조할 수 없음
- Gateway와 Game은 서로 참조하지 않음
- Protocol은 외부 의존성 최소화

---

## 4. 마이그레이션 단계

### Phase 1: Protocol 프로젝트 생성
- SimpleNetEngine.Protocol.csproj 생성
- 11개 파일 이동 (Packets, Memory, Utils)
- 네임스페이스 변경: `SimpleNetEngine.Common.*` → `SimpleNetEngine.Protocol.*`

### Phase 2: Infrastructure 프로젝트 생성
- SimpleNetEngine.Infrastructure.csproj 생성
- 5개 파일 이동 (Discovery, DistributeLock, NetMQ)
- 네임스페이스 변경: `SimpleNetEngine.Common.*` → `SimpleNetEngine.Infrastructure.*`

### Phase 3: Node 프로젝트 리팩토링
- 33개 파일 using 문 일괄 업데이트
- NodeMetadataKeys.cs 추가
- Common 참조 제거 → Protocol/Infrastructure 참조

### Phase 4: Gateway/GameServer 업데이트
- 프로젝트 참조 변경
- using 문 일괄 업데이트
- 빌드 검증

### Phase 5: Common 프로젝트 제거
- SimpleNetEngine.Common 삭제
- 솔루션 파일 업데이트

---

## 5. 실행 결과

### 변경 통계
```
96 files changed, 4617 insertions(+), 4631 deletions(-)
```

### 생성된 프로젝트
- ✅ SimpleNetEngine.Protocol (12개 파일)
- ✅ SimpleNetEngine.Infrastructure (7개 파일)
- ✅ SimpleNetEngine.Node (리팩토링, 34개 파일)

### 검증 결과
- ✅ 전체 빌드: 경고 0개, 오류 0개
- ✅ 유닛 테스트: 18/18 통과
- ✅ 의존성 계층: Protocol → Infrastructure → Node → Gateway/Game

---

## 6. 네임스페이스 매핑

| Before | After |
|--------|-------|
| `SimpleNetEngine.Common.Packets` | `SimpleNetEngine.Protocol.Packets` |
| `SimpleNetEngine.Common.Memory` | `SimpleNetEngine.Protocol.Memory` |
| `SimpleNetEngine.Common.Utils` | `SimpleNetEngine.Protocol.Utils` |
| `SimpleNetEngine.Common.Discovery` | `SimpleNetEngine.Infrastructure.Discovery` |
| `SimpleNetEngine.Common.NetMQ` | `SimpleNetEngine.Infrastructure.NetMQ` |
| `SimpleNetEngine.Common.DistributeLock` | `SimpleNetEngine.Infrastructure.DistributeLock` |

---

## 7. Breaking Changes 대응

### 네임스페이스 변경
- 모든 using 문 일괄 업데이트 (sed 사용)
- 컴파일 에러 즉시 해결

### 프로젝트 참조 변경
- Common 제거 → Protocol + Infrastructure + Node 추가
- 테스트 프로젝트도 동일하게 업데이트

### DI 등록 코드
- 네임스페이스만 변경, 구현 로직 동일

---

## 8. 얻은 교훈

### 성공 요인
1. **명확한 계층 설계**: Tier 0-3 구조로 의존성 방향 명확
2. **일괄 처리**: sed를 이용한 네임스페이스 자동 변경
3. **단계별 검증**: 각 Phase마다 빌드 검증
4. **테스트 우선**: 변경 후 즉시 테스트 실행

### 개선 사항
1. Protocol에 누락된 파일 발견 (Lz4Holder, NetworkHelper)
2. Infrastructure에 누락된 패키지 참조 (Microsoft.Extensions.Hosting.Abstractions)
3. NodeMetadataKeys 위치 조정 (Common → Node)

### 향후 적용
- 새로운 기능 추가 시 명확한 계층 준수
- Protocol은 외부 의존성 최소화 유지
- 각 계층의 책임 명확히 유지

---

## 9. 타임라인

| Phase | 작업 내용 | 소요 시간 |
|-------|-----------|-----------|
| P1-P2 | Protocol, Infrastructure 생성 | 2시간 |
| P3 | Node 리팩토링 | 1시간 |
| P4 | Gateway/GameServer 업데이트 | 1.5시간 |
| P5 | Common 제거 | 0.5시간 |
| P6 | 테스트 및 검증 | 1시간 |
| **총합** | | **6시간** |

---

## 10. 참고 자료

### 주요 파일
- `NetworkEngine.sln` - 솔루션 파일
- `Directory.Packages.props` - 중앙 패키지 관리
- `SimpleNetEngine.Protocol/SimpleNetEngine.Protocol.csproj`
- `SimpleNetEngine.Infrastructure/SimpleNetEngine.Infrastructure.csproj`

### 관련 커밋
- `6dd47b8` - refactor: 라이브러리 계층 분리

### 문서
- [아키텍처 개요](../architecture/01-overview.md)
- [라이브러리 구조](../architecture/04-library-structure.md)
