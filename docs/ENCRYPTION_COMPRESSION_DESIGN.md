# 압축/암호화 기능 설계 (Encryption & Compression Design)

## 1. 개요

Gateway의 I/O Offloading 역할에 따라, 클라이언트-서버 간 패킷에 **암호화**(AES-256-GCM)와 **압축**(LZ4)을 적용한다.
GameServer는 항상 평문만 다루며, Gateway가 암호화/복호화 및 압축/해제를 전담한다.

### 설계 원칙

| 원칙 | 설명 |
|------|------|
| **Gateway = I/O Offloading** | 암호화/압축은 I/O 변환이며 비즈니스 로직이 아님. Gateway의 Dumb Proxy 원칙과 양립 |
| **EndPointHeader = 항상 평문** | Gateway가 Flags를 읽어 처리 방식을 결정. Opcode 파싱 아님 (프레이밍 메타데이터) |
| **EndPointHeader = AAD** | AES-GCM의 AAD(Associated Authenticated Data)로 사용. 평문이지만 무결성 보호됨 |
| **GameServer = 평문 전용** | GameServer 내부 Zero-Copy 경로는 변경 없음 |
| **Network Dualism 유지** | Data Plane(GSC)에서만 적용. Control Plane(Node Service Mesh)은 영향 없음 |
| **MITM 방지** | ECDH P-256 키 교환 + ECDSA P-256 서명으로 중간자 공격 차단 |
| **서버 On/Off 제어** | `GatewayOptions.EnableEncryption`으로 암호화 기능 전체 활성/비활성 (개발 모드 지원) |

---

## 2. EndPointHeader 구조 (12 bytes)

```
┌──────────────┬───────────┬───────┬──────────┬────────────────┐
│ TotalLength  │ ErrorCode │ Flags │ Reserved │ OriginalLength │
│   (4 bytes)  │ (2 bytes) │(1 byte)│ (1 byte)│   (4 bytes)   │
└──────────────┴───────────┴───────┴──────────┴────────────────┘
Total: 12 bytes (Pack = 1)
```

- **TotalLength**: 헤더 포함 패킷 전체 길이
- **OriginalLength**: 압축 전 원본 크기 (FlagCompressed일 때만 유효, 해제 시 버퍼 크기 결정용)

### Flags 비트 정의

| Bit | 상수 | 의미 |
|-----|------|------|
| 0   | `FlagEncrypted`  (0x01) | GameHeader + Payload가 암호화됨 |
| 1   | `FlagCompressed` (0x02) | 암호화 전에 압축이 적용됨 |
| 2   | `FlagHandshake`  (0x04) | Handshake 패킷 (항상 평문, 암호화/압축 스킵) |
| 3-7 | Reserved | 향후 확장 |

### 코드

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EndPointHeader
{
    public int TotalLength;      // 4 bytes - 헤더 포함 전체 길이
    public short ErrorCode;      // 2 bytes
    public byte Flags;           // 1 byte
    public byte Reserved;        // 1 byte
    public int OriginalLength;   // 4 bytes - 압축 전 원본 크기

    public const int Size = 12;  // sizeof(int) + sizeof(short) + sizeof(byte) * 2 + sizeof(int)

    public const byte FlagEncrypted  = 0x01;
    public const byte FlagCompressed = 0x02;
    public const byte FlagHandshake  = 0x04;

    public bool IsEncrypted  => (Flags & FlagEncrypted) != 0;
    public bool IsCompressed => (Flags & FlagCompressed) != 0;
    public bool IsHandshake  => (Flags & FlagHandshake) != 0;
}
```

---

## 3. 암호화 설계

### 3.1 알고리즘: AES-256-GCM (AEAD)

| 항목 | 값 |
|------|-----|
| 알고리즘 | AES-256-GCM |
| 키 길이 | 32 bytes (256-bit) |
| Nonce 길이 | 12 bytes (카운터 기반, **전송하지 않음**) |
| Auth Tag 길이 | 16 bytes |
| AAD | EndPointHeader (12 bytes) |
| 오버헤드 | **+16 bytes per packet** (Tag만, Nonce 미전송) |

**AES-GCM 선택 이유**:
- AEAD: 암호화 + 무결성 검증을 단일 연산으로 수행
- .NET `AesGcm` 클래스가 하드웨어 AES-NI 명령어 자동 활용 (~5 GB/s on x86)
- 게임 서버의 초저지연 요구사항에 적합
- ChaCha20-Poly1305 대비 AES-NI 지원 CPU에서 3배 이상 빠름

### 3.2 암호화 Wire Format

```
Handshake 패킷 (FlagHandshake — 항상 평문):
┌──────────────┬──────────────────────────────┐
│ EndPointHeader│ GameHeader + Payload (평문)  │
│ (12B, 평문)  │                              │
└──────────────┴──────────────────────────────┘

암호화 패킷 (FlagEncrypted):
┌──────────────┬──────────┬──────────────────────────────┐
│ EndPointHeader│ Tag(16B) │ Ciphertext                   │
│ (12B, 평문)  │          │ (Encrypted GameHeader+Payload)│
│ (AAD로 보호) │          │                              │
└──────────────┴──────────┴──────────────────────────────┘

TotalLength = 12 + 16 + Ciphertext.Length

암호화+압축 패킷 (FlagEncrypted | FlagCompressed):
┌──────────────┬──────────┬──────────────────────────────┐
│ EndPointHeader│ Tag(16B) │ Ciphertext                   │
│ (12B, 평문)  │          │ (Encrypted LZ4Data)          │
│ OriginalLength│         │                              │
│ =압축전크기  │          │                              │
└──────────────┴──────────┴──────────────────────────────┘
```

- **EndPointHeader**: 항상 평문이지만 AAD로 무결성 보호 (변조 시 복호화 실패)
- **Tag**: EndPointHeader 직후 16바이트 (AES-GCM 인증 태그)
- **Ciphertext**: GameHeader + Payload (또는 LZ4 압축된 데이터)를 AES-GCM으로 암호화
- **Nonce 미전송**: 양측 카운터 동기화로 Nonce 12바이트 절약

### 3.3 Nonce 전략: 카운터 기반 (전송하지 않음)

패킷 카운터(uint64) + 방향 비트(1 byte)로 구성. **양측이 동일한 카운터를 유지하므로 Nonce를 네트워크로 전송할 필요가 없다.**

```
Nonce (12 bytes) — 양측 로컬 생성:
┌──────────────────────────┬───────────┬──────────┐
│ PacketCounter (8 bytes)  │ Direction │ Padding  │
│    (LE, 1부터 시작)      │  (1 byte) │ (3 bytes)│
└──────────────────────────┴───────────┴──────────┘

Direction: 0x00 = Client→Server (C2S), 0x01 = Server→Client (S2C)
```

- 카운터는 세션별 독립 관리 (GatewaySession + NetClient 각각)
- `Interlocked.Increment(ref counter)` — 원자적 증가, 1부터 시작
- 방향 비트로 같은 카운터 값이라도 C2S/S2C Nonce가 다름
- uint64 범위 내에서 Nonce 고갈 불가 (18경 패킷)
- **카운터 불일치 = 복호화 실패** (AuthenticationTagMismatchException) → 연결 종료

---

## 4. 압축 설계

### 4.1 알고리즘: LZ4

| 항목 | 값 |
|------|-----|
| 라이브러리 | K4os.Compression.LZ4 |
| 모드 | LZ4Codec.Encode (Fast 모드) |
| 압축 임계값 | 128 bytes (미만은 압축 안 함) |

**LZ4 선택 이유**:
- 압축/해제 속도가 zstd, deflate보다 월등히 빠름 (게임 서버 지연시간 요구)
- 이미 프로젝트 의존성에 포함됨
- 소규모 패킷에서도 합리적인 압축률

### 4.2 압축 적용 조건

```csharp
// GameHeader + Payload 원본 크기가 임계값 이상일 때만 압축
const int CompressionThreshold = 128;
bool shouldCompress = gameData.Length >= CompressionThreshold;
```

- 128바이트 미만: 압축 오버헤드가 이득보다 큼 → 스킵
- 압축 후 원본보다 커지면: FlagCompressed 설정하지 않고 원본 사용

### 4.3 압축 + 암호화 순서: Compress-Then-Encrypt

```
[원본 GameHeader + Payload]
        │
        ▼ (압축 — 원본 크기 ≥ 128B일 때)
[LZ4 압축 데이터] + FlagCompressed 설정 + OriginalLength 기록
        │
        ▼ (암호화 — Handshake 이후 활성화)
[EndPointHeader(AAD)][Tag(16B)][Ciphertext] + FlagEncrypted 설정
```

**이 순서의 이유**:
1. 암호화된 데이터는 최대 엔트로피를 가져 압축 불가
2. Encrypt-Then-Compress는 CRIME/BREACH 유사 공격에 취약

---

## 5. Wire Format

### 5.1 클라이언트 ↔ Gateway (TCP)

**Handshake 패킷** (암호화 전):
```
┌──────────────────────────────────────────────┐
│ EndPointHeader (12B, Flags=FlagHandshake)     │
│ GameHeader (8B)                              │
│ Protobuf Payload                             │
└──────────────────────────────────────────────┘
```

**일반 패킷** (암호화 + 압축):
```
┌──────────────────────────────────────────────────────┐
│ EndPointHeader (12B, Flags=Encrypted|Compressed)      │
│ Tag (16B)                                             │
│ Ciphertext = Encrypted(LZ4(GameHeader + Payload))    │
└──────────────────────────────────────────────────────┘

TotalLength = 12 + 16 + Ciphertext.Length
OriginalLength = GameHeader.Length + Payload.Length (압축 전)
```

**일반 패킷** (암호화만, 압축 안 함):
```
┌──────────────────────────────────────────────────────┐
│ EndPointHeader (12B, Flags=Encrypted)                 │
│ Tag (16B)                                             │
│ Ciphertext = Encrypted(GameHeader + Payload)          │
└──────────────────────────────────────────────────────┘
```

### 5.2 Gateway ↔ GameServer (GSC — 내부망)

**변경 없음**. Gateway가 복호화/해제 후 평문으로 전달:
```
┌──────────────────────────────────────────────────────┐
│ GSCHeader (49B)                                       │
│ EndPointHeader (12B, Flags=0)                         │
│ GameHeader (8B)                                       │
│ Protobuf Payload (평문)                               │
└──────────────────────────────────────────────────────┘
```

---

## 6. ECDH P-256 키 교환 + ECDSA P-256 서명

### 6.1 왜 TLS가 아닌가

| 문제 | 설명 |
|------|------|
| **선택적 암호화 불가** | TLS는 연결 전체를 암호화. 패킷 단위로 Handshake=평문, 이후=암호화 같은 제어 불가 |
| **선택적 압축 불가** | 패킷별 Flags로 압축 여부를 결정하는 현재 설계와 양립 불가 |
| **EndPointHeader 평문 유지 불가** | TLS에서는 모든 데이터가 암호화되어 Gateway가 Flags를 읽을 수 없음 |

### 6.2 키 교환 방식: ECDH P-256

**핵심**: 대칭키(AES Key)가 네트워크를 통해 전송되지 않는다.
양쪽이 ECDH로 독립적으로 동일한 SharedSecret을 도출하고, HKDF로 대칭키를 파생한다.

| 항목 | 값 |
|------|-----|
| 곡선 | NIST P-256 (secp256r1) |
| 공개키 형식 | DER (SubjectPublicKeyInfo) |
| SharedSecret 도출 | `ECDiffieHellman.DeriveKeyFromHash(SHA256)` |
| 키 파생 | HKDF-SHA256 (SharedSecret → AES-256 Key) |
| HKDF info | `"NetworkEngine-AES256-GCM"` |
| HKDF salt | 없음 (null) |
| 서명 | ECDSA P-256 + SHA-256 (Gateway 장기 키로 ECDH 공개키 서명) |

### 6.3 ECDSA 서명 키 관리

**Gateway가 서명 키를 보유한다** — Gateway가 ECDH 임시 키를 생성하는 주체이므로, 자신의 공개키에 직접 서명하는 것이 자연스럽다.

```
┌────────────┬──────────────────────────────────────────────┐
│ 주체       │ 역할                                          │
├────────────┼──────────────────────────────────────────────┤
│ Gateway    │ ECDSA P-256 장기 서명 비밀키 보유              │
│            │ ECDH P-256 임시 키 생성 + 서명                │
│            │ SharedSecret → HKDF → AES-256 키 도출         │
│            │ AES-GCM 암호화/복호화 수행                     │
│ GameServer │ Gateway ECDH 공개키+서명을 Actor.State에 저장   │
│            │ 클라이언트 ECDH 공개키를 Gateway로 중계          │
│            │ ActivateEncryption RPC → Gateway 암호화 활성화  │
│ Client     │ ECDH P-256 임시 키 생성                       │
│            │ ECDSA 서명 검증 (MITM 방지)                    │
│            │ SharedSecret → HKDF → AES-256 키 도출          │
└────────────┴──────────────────────────────────────────────┘
```

### 6.4 ECDH 키 교환 시퀀스 다이어그램

```
  Client                          Gateway                         GameServer
    │                               │                                │
    │  [1] TCP Connect              │                                │
    │ ─────────────────────────────▶│                                │
    │                               │ ① SessionId 발급               │
    │                               │ ② ECDH P-256 임시 키쌍 생성     │
    │                               │    _ecdh = ECDiffieHellman     │
    │                               │    .Create(nistP256)           │
    │                               │ ③ ECDSA 서명 생성               │
    │                               │    sig = _signingKey.SignData( │
    │                               │      ephPubKeyDER, SHA256)     │
    │                               │                                │
    │                               │  [2] ServiceMeshNewUserNtfReq  │
    │                               │     (Service Mesh RPC)         │
    │                               │ ──────────────────────────────▶│
    │                               │  { GatewayNodeId,              │
    │                               │    SessionId,                  │
    │                               │    GatewayEphemeralPublicKey,   │
    │                               │    GatewayEphemeralSignature }  │
    │                               │                                │
    │                               │                                │ ④ Actor 생성
    │                               │                                │ ⑤ ECDH 공개키 + 서명을
    │                               │                                │    Actor.State에 저장
    │                               │  [3] ServiceMeshNewUserNtfRes  │
    │                               │ ◀──────────────────────────────│
    │                               │                                │
    │  [4] ReadyToHandshakeNtf     │                                │
    │ ◀────────────────────────────│◀────── GSC ───────────────────│
    │  (평문, FlagHandshake)       │                                │
    │                               │                                │
    │  [5] HandshakeReq            │                                │
    │ ─────────────────────────────▶│─────── GSC ────────────────▶│
    │  { ClientEphemeralPublicKey } │  (평문)                        │
    │                               │                                │
    │                               │                                │ ⑥ Actor.State에서
    │                               │                                │    GW 공개키+서명 추출
    │                               │                                │
    │                               │  [6] ServiceMeshActivateEncryptionReq
    │                               │ ◀──────────────────────────────│
    │                               │  { SessionId,                  │
    │                               │    ClientEphemeralPublicKey }   │
    │                               │                                │
    │                               │ ⑦ ECDH SharedSecret 도출       │
    │                               │    clientEcdh.ImportSubject    │
    │                               │    PublicKeyInfo(clientPubKey) │
    │                               │    sharedSecret = _ecdh       │
    │                               │    .DeriveKeyFromHash(SHA256)  │
    │                               │                                │
    │                               │ ⑧ HKDF → AES-256 키 도출       │
    │                               │    aesKey = HKDF.DeriveKey(    │
    │                               │      SHA256, sharedSecret,     │
    │                               │      32, info:"NetworkEngine  │
    │                               │      -AES256-GCM")             │
    │                               │                                │
    │                               │ ⑨ AesGcm 인스턴스 생성          │
    │                               │    _encryptAesGcm = new(key)   │
    │                               │    _decryptAesGcm = new(key)   │
    │                               │    _encryptionActive = true    │
    │                               │                                │
    │                               │  [7] ServiceMeshActivateEncryptionRes
    │                               │ ──────────────────────────────▶│
    │                               │    { Success = true }          │
    │                               │                                │
    │  [8] HandshakeRes       │                                │
    │ ◀────────────────────────────│◀────── GSC ───────────────────│
    │  { ServerEphemeralPublicKey,  │  (평문, FlagHandshake)         │
    │    ServerEphemeralSignature } │                                │
    │  (ReconnectKey 미포함)        │                                │
    │                               │                                │
    │ ⑩ ECDSA 서명 검증             │                                │
    │    _serverVerificationKey    │                                │
    │    .VerifyData(pubKey,       │                                │
    │     signature, SHA256)       │                                │
    │    (실패 → 연결 종료)         │                                │
    │                               │                                │
    │ ⑪ ECDH SharedSecret 도출      │                                │
    │    serverEcdh.ImportSubject  │                                │
    │    PublicKeyInfo(serverPubKey)│                                │
    │    sharedSecret = _ecdh     │                                │
    │    .DeriveKeyFromHash(SHA256)│                                │
    │                               │                                │
    │ ⑫ HKDF → AES-256 키 도출      │                                │
    │    (Gateway와 동일 파라미터)   │                                │
    │                               │                                │
    │ ⑬ ActivateEncryption()       │                                │
    │    _encryptAesGcm = new(key) │                                │
    │    _decryptAesGcm(parser)    │                                │
    │                               │                                │
    ╞══════════════════════════════╪════════════════════════════════╡
    │  [암호화 채널 확립 완료]        │                                │
    │                               │                                │
    │  [9] LoginGameReq            │                                │
    ╞══[Encrypted C2S]════════════▶╡                                │
    │  { credential }              │ Decrypt                        │
    │                               │ ────[Plaintext]──GSC────────▶│
    │                               │                                │ ⑭ LoginGameRes 생성
    │                               │                                │    { ReconnectKey }
    │  [10] LoginGameRes            │                                │
    ╡◀═[Encrypted S2C]═════════════╡                                │
    │  { ReconnectKey }            │ ◀───[Plaintext+UseEncrypt]────│
    │  (암호화 전송)                │ Encrypt                        │
    │                               │                                │
    │ ⑮ ReconnectKey 저장           │                                │
    │                               │                                │
    ╞══════════════════════════════╪════════════════════════════════╡
    │                               │                                │
    │  [이후] 모든 패킷 암호화       │                                │
    │                               │                                │
    ╞══[Encrypted C2S]════════════▶╡                                │
    │  [EP(Encrypted)][Tag][Cipher]│ Decrypt → Decompress           │
    │                               │ ────[Plaintext]──GSC────────▶│
    │                               │                                │
    │                               │                    처리         │
    │                               │                                │
    ╡◀═[Encrypted S2C]═════════════╡                                │
    │  [EP(Encrypted)][Tag][Cipher]│ ◀───[Plaintext]──GSC──────────│
    │                               │ Compress → Encrypt             │
    │                               │                                │
```

### 6.5 키 도출 과정 (Gateway & Client 양측 동일)

```csharp
// 1. ECDH SharedSecret 도출 (양쪽 독립적으로 동일한 값 도출)
using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
byte[] sharedSecret = ecdh.DeriveKeyFromHash(
    peerPublicKey,
    HashAlgorithmName.SHA256);

// 2. HKDF로 AES-256 대칭키 파생
byte[] aesKey = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    ikm: sharedSecret,
    outputLength: 32,                              // AES-256 = 32 bytes
    info: "NetworkEngine-AES256-GCM"u8.ToArray()); // 양측 동일 문자열 필수

// 3. 보안: SharedSecret 즉시 제거
CryptographicOperations.ZeroMemory(sharedSecret);
```

**양측 일치 필수 항목**:
- `HashAlgorithmName.SHA256` (HKDF 해시)
- `outputLength: 32`
- `info: "NetworkEngine-AES256-GCM"` (하나라도 다르면 다른 AES 키 → Tag mismatch)

### 6.6 MITM 방어

| 공격 | 방어 |
|------|------|
| MITM이 ServerEphemeralPubKey를 교체 | ECDSA 서명 검증 실패 → `CryptographicException` → 연결 종료 |
| 서명 없이 ECDH만 수행 | ECDH 자체는 MITM 취약 → 반드시 ECDSA 서명 검증 필요 |
| 서명 키 유출 | 키 교체 후 클라이언트에 새 공개키 배포 |
| Replay (이전 HandshakeRes 재사용) | 임시 키 쌍은 세션마다 새로 생성 → SharedSecret이 달라짐 |
| Forward Secrecy | 세션마다 임시 ECDH 키 → 장기 키 유출되어도 과거 세션 복호화 불가 |

### 6.7 Proto 정의

```protobuf
// handshake.proto
message HandshakeReq {
  bytes client_ephemeral_public_key = 1;  // ECDH P-256 공개키 (DER)
}

message HandshakeRes {
  // field 2 제거 (ReconnectKey → LoginGameRes로 이동, 평문 노출 방지)
  bytes server_ephemeral_public_key = 3;  // Gateway ECDH P-256 공개키 (DER)
  bytes server_ephemeral_signature = 4;   // ECDSA P-256 서명 (MITM 방지)
}

// LoginGameRes에 ReconnectKey 포함 (암호화 채널 확립 후 전송)
// SessionKeyReq/Res는 RTT 최적화(3→2)로 제거됨

message ReadyToHandshakeNtf {}           // Actor 생성 완료 알림

// service_mesh.proto (내부 RPC)
message ServiceMeshNewUserNtfReq {
  int64 gateway_node_id = 1;
  int64 session_id = 2;
  bytes gateway_ephemeral_public_key = 3;  // Gateway ECDH 공개키
  bytes gateway_ephemeral_signature = 4;   // ECDSA 서명
}

message ServiceMeshActivateEncryptionReq {
  int64 session_id = 1;
  bytes client_ephemeral_public_key = 2;   // 클라이언트 ECDH 공개키
}

message ServiceMeshActivateEncryptionRes {
  bool success = 1;
}
```

### 6.8 서명 키 설정

#### Gateway 설정

```csharp
public class GatewayOptions
{
    /// <summary>
    /// 암호화 활성화 여부 (ECDH 키 교환 + AES-256-GCM)
    /// false: ECDH 키 교환 생략, 모든 패킷 평문 전송 (개발/테스트용)
    /// true: ECDH 키 교환 수행, 선택적 암호화 지원 (프로덕션)
    /// </summary>
    public bool EnableEncryption { get; set; } = true;

    /// <summary>
    /// ECDSA P-256 서명 개인키 PEM 파일 경로 (MITM 방지)
    /// 미설정 시 서명 없이 동작 (개발 모드)
    /// EnableEncryption이 false이면 무시됨
    /// </summary>
    public string? SigningKeyPath { get; set; }
}
```

```json
{
  "Gateway": {
    "EnableEncryption": true,
    "SigningKeyPath": "../certs/server_signing_key.pem"
  }
}
```

**`EnableEncryption: false` 동작**:
- ECDH 키 쌍 생성 생략 (세션당 ECDiffieHellman 인스턴스 미생성)
- NewUserNtfReq에 빈 공개키 전송 → GameServer가 Actor.State에 저장하지 않음
- HandshakeController가 빈 ServerEphemeralPublicKey 응답 → 클라이언트가 암호화 스킵
- LoginGameRes에 ReconnectKey가 null → 재접속 불가
- 모든 패킷이 평문으로 전송됨

#### 키 생성

```bash
# ECDSA P-256 서명 키쌍 생성
openssl ecparam -name prime256v1 -genkey -noout | \
  openssl pkcs8 -topk8 -nocrypt -out server_signing_key.pem

# 공개키 추출 (클라이언트 배포용)
openssl ec -in server_signing_key.pem -pubout -out server_signing_key.pub.pem
```

#### 클라이언트

```csharp
// PEM 파일에서 검증 공개키 로드
var pubKeyPem = File.ReadAllText("server_signing_key.pub.pem");
var handshakeHandler = new GameHandshakeHandler(pubKeyPem);

// 또는 서명 검증 없이 (개발/테스트, MITM 취약)
var handshakeHandler = new GameHandshakeHandler();
```

---

## 7. 처리 흐름 상세

### 7.1 Outbound (GameServer → Client): Compress → Encrypt

```
GamePacketRouter.HandleServerPacket() — GSC에서 수신
│
├── 1. GSCHeader 파싱 → SessionId로 GatewaySession 조회
│
├── 2. GSCHeader 이후 데이터 = [EndPointHeader][GameHeader][Payload] (평문)
│
├── 3. Response.UseCompress() 힌트 확인
│   └── FlagCompressed 설정됨?
│       ├── Yes → PacketCompressor.TryCompress()
│       │         Input:  [EndPointHeader][GameHeader][Payload]
│       │         Output: [EndPointHeader(FlagCompressed, OriginalLength)][LZ4Data]
│       └── No  → 원본 유지
│
├── 4. Response.UseEncrypt() 힌트 확인 + 암호화 활성화 여부
│   └── session.IsEncryptionActive?
│       ├── Yes → session.SendEncrypted()
│       │         → PacketEncryptor.TryEncrypt()
│       │           Input:  [EndPointHeader][Data]
│       │           AAD:    EndPointHeader (12B)
│       │           Nonce:  카운터 기반 (S2C, Direction=1)
│       │           Output: [EndPointHeader(FlagEncrypted)][Tag(16B)][Ciphertext]
│       └── No  → session.SendFromGameServer() (평문)
│
└── 5. TCP 전송 → Client
```

### 7.2 Inbound (Client → Gateway → GameServer): Decrypt → Decompress

```
GatewaySession.OnReceived(buffer, offset, size)
│
├── 1. EndPointHeader 읽기 (항상 평문)
│
├── 2. Step 1: 복호화 (암호화가 마지막에 적용되었으므로 먼저 해제)
│   └── header.IsEncrypted?
│       ├── Yes → PacketEncryptor.TryDecrypt()
│       │         AAD:    EndPointHeader (원본, 12B)
│       │         Nonce:  카운터 기반 (C2S, Direction=0)
│       │         Output: [EndPointHeader(FlagEncrypted 제거)][Plaintext]
│       │         실패 → Disconnect() (변조 또는 카운터 desync)
│       └── No  → 다음 단계
│
├── 3. Step 2: 압축 해제
│   └── header.IsCompressed?
│       ├── Yes → PacketCompressor.TryDecompress()
│       │         Output: [EndPointHeader(FlagCompressed 제거)][GameHeader][Payload]
│       └── No  → 원본 유지
│
└── 4. ForwardToGameServer(plaintext)
    └── GSCHeader 부착 → NetMQ 전송 (기존 로직 동일)
```

### 7.3 클라이언트 Inbound 파이프라인 (ProtoPacketParser)

```
ProtoPacketParser.Parse(buffer)
│
├── 1. TotalLength 읽기 → 완전한 패킷 도착 확인
│
├── 2. EndPointHeader 읽기
│   └── header.IsEncrypted?
│       ├── Yes → DecryptPacket()
│       │         Tag = buffer[12..28]
│       │         Ciphertext = buffer[28..]
│       │         Nonce: 카운터 기반 (S2C, Direction=1)
│       │         AAD: 원본 EndPointHeader
│       │         Output: [EndPointHeader(FlagEncrypted 제거)][Plaintext]
│       │
│       │         ⚠️ ArrayPool 버퍼 슬라이싱 주의:
│       │         decryptedPacket.AsSpan(0, decHeader.TotalLength)
│       │         (Rent 버퍼는 요청보다 클 수 있음)
│       │
│       │         → ParseInnerPacket(decHeader, innerData)
│       │            └── header.IsCompressed?
│       │                ├── Yes → LZ4Codec.Decode(innerData, OriginalLength)
│       │                │         → ParseGamePacket(decompressed)
│       │                └── No  → ParseGamePacket(innerData)
│       │
│       └── No  → 기존 ParseFromBuffer() (평문/압축만)
│
└── 3. GameHeader 파싱 → Protobuf 역직렬화 → NetworkPacket 반환
```

---

## 8. GatewaySession 암호화 상태

```csharp
public class GatewaySession : TcpSession
{
    // --- ECDH 키 교환 + ECDSA 서명 ---
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa? _signingKey;        // Gateway 장기 서명키 (null = 서명 없음)
    private byte[]? _ephemeralPublicKeyDer;      // ECDH P-256 공개키 (DER)
    private byte[]? _ephemeralSignature;          // ECDSA 서명

    // --- 암호화 상태 ---
    private AesGcm? _encryptAesGcm;   // S2C 암호화용
    private AesGcm? _decryptAesGcm;   // C2S 복호화용
    private ulong _encryptCounter;     // S2C Nonce 카운터
    private ulong _decryptCounter;     // C2S Nonce 카운터
    private volatile bool _encryptionActive;
}
```

**AesGcm 인스턴스가 2개인 이유**: TCP 수신 스레드(Decrypt)와 NetMQ Poller 스레드(Encrypt)가 동시에 접근할 수 있으므로, lock 없이 스레드 안전성을 보장하기 위해 분리.

### 키 생성 (OnConnected)

```csharp
// GatewaySession.GetEphemeralPublicKey()
_ephemeralPublicKeyDer = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

if (_signingKey != null)
    _ephemeralSignature = _signingKey.SignData(_ephemeralPublicKeyDer, HashAlgorithmName.SHA256);
```

### SharedSecret 도출 (ActivateEncryption RPC 수신 시)

```csharp
// GatewaySession.DeriveAndActivateEncryption(clientPubKeyDer)
using var clientEcdh = ECDiffieHellman.Create();
clientEcdh.ImportSubjectPublicKeyInfo(clientPubKeyDer, out _);

var sharedSecret = _ecdh.DeriveKeyFromHash(clientEcdh.PublicKey, HashAlgorithmName.SHA256);
var aesKey = HKDF.DeriveKey(SHA256, sharedSecret, 32, info: "NetworkEngine-AES256-GCM");

CryptographicOperations.ZeroMemory(sharedSecret);
ActivateEncryption(aesKey);  // → AesGcm 2개 생성, _encryptionActive = true
```

---

## 9. 메모리 관리 (Zero-Copy 호환)

### 9.1 ArrayPool 기반 임시 버퍼

암호화/압축은 데이터 크기를 변경하므로 in-place 처리 불가. `ArrayPool<byte>.Shared`로 임시 버퍼를 할당한다.

```csharp
// 암호화 예시 (PacketEncryptor.TryEncrypt)
encryptedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
aesGcm.Encrypt(nonce, plaintext, ciphertextSpan, tagSpan, aad: outSpan[..EndPointHeader.Size]);
// ... 사용 후
ArrayPool<byte>.Shared.Return(encryptedBuffer);
```

### 9.2 성능 영향 분석

| 구간 | 변경 전 | 변경 후 | 영향 |
|------|---------|---------|------|
| Client → Gateway | TCP 수신 (0 alloc) | + 복호화 버퍼 + 해제 버퍼 (ArrayPool) | 최대 2회 Rent/Return |
| Gateway → GameServer (GSC) | Msg.InitPool → NetMQ 전송 | **변경 없음** (평문 전달) | 없음 |
| GameServer 내부 | Actor → Controller | **변경 없음** (평문) | 없음 |
| GameServer → Gateway (GSC) | SendResponse Zero-Copy | **변경 없음** | 없음 |
| Gateway → Client | TCP 전송 (0 alloc) | + 압축 버퍼 + 암호화 버퍼 (ArrayPool) | 최대 2회 Rent/Return |

**핵심**: GameServer 내부 경로는 전혀 변경되지 않는다. 오버헤드는 Gateway의 TCP I/O 경계에서만 발생하며, ArrayPool로 GC 압력을 최소화한다.

### 9.3 ArrayPool 버퍼 슬라이싱 주의

`ArrayPool.Rent(size)` 는 `size` **이상**의 버퍼를 반환한다.
반드시 유효 길이로 슬라이싱해야 하며, 전체 배열을 그대로 사용하면 후행 쓰레기 바이트가 포함된다.

```csharp
// ❌ 잘못된 예: 전체 배열 사용
var decSpan = decryptedPacket.AsSpan();  // 쓰레기 바이트 포함!

// ✅ 올바른 예: TotalLength로 슬라이스
var decHeader = MemoryMarshal.Read<EndPointHeader>(decryptedPacket);
var decSpan = decryptedPacket.AsSpan(0, decHeader.TotalLength);
```

---

## 10. Handshake 판별 방법

### Inbound (Client → Gateway)

`_encryptionActive` 플래그로 판별:
- `false` (Handshake 진행 중): 패킷에 FlagEncrypted가 없으므로 평문 처리
- `true` (Handshake 완료 후): FlagEncrypted 패킷은 복호화, 없으면 평문 처리

### Outbound (GameServer → Gateway)

GameServer가 `Response` 힌트로 `EndPointHeader.Flags`를 제어:
- `Response.Ok(msg)` → Flags = 0 → 평문 (암호화 미활성 시) 또는 Gateway가 `FlagEncrypted` 확인 후 처리
- `Response.Ok(msg).UseEncrypt()` → `FlagEncrypted` 설정 → Gateway가 AES-GCM 암호화 적용
- Handshake 관련 응답 (HandshakeRes, ReadyToHandshakeNtf): Flags에 Encrypt/Compress 없음 → 항상 평문

**LoginGameRes의 암호화 흐름** (ReconnectKey 보호):
```
LoginController.HandleNewLogin()
  → Response.Ok(loginGameRes).UseEncrypt()
    → FlagEncrypted 설정
      → Gateway가 session.SendEncrypted() 호출
        → AES-GCM 암호화 후 클라이언트에 전송
```

**이 방식의 장점**:
- Gateway가 MsgId(opcode)를 파싱할 필요 없음
- EndPointHeader.Flags는 프레이밍 메타데이터 → Dumb Proxy 원칙 위반 아님
- GameServer만 패킷 의미를 이해하고 Flags를 설정하는 주체

---

## 11. 보안 고려사항

| 위협 | 대응 |
|------|------|
| **MITM (중간자 공격)** | ECDH + ECDSA P-256 서명. 서명 검증 실패 시 즉시 연결 종료 |
| **Replay Attack** | Nonce 카운터 단조 증가 + 임시 키 쌍(세션마다 새로 생성) |
| **Tampering** | AES-GCM Auth Tag + EndPointHeader AAD로 무결성 검증 |
| **Key Leakage** | SharedSecret/AES키는 메모리에만 존재, 세션 종료 시 ZeroMemory() |
| **Nonce Exhaustion** | uint64 카운터 (18경 패킷까지 안전) |
| **Downgrade Attack** | 암호화 활성화 후 FlagEncrypted 없는 일반 패킷은 정상 처리 (서버 측 정책) |
| **Side-Channel** | Compress-then-Encrypt 순서로 CRIME/BREACH 유사 공격 완화 |
| **Forward Secrecy** | 세션마다 임시 ECDH 키 생성 → 장기 서명 키 유출되어도 과거 세션 복호화 불가 |
| **카운터 Desync** | 복호화 실패 = Tag mismatch → 연결 종료 (자동 복구 불가, 재접속 필요) |
| **ReconnectKey 평문 노출** | HandshakeRes(평문)에서 제거 → 암호화 채널 확립 후 LoginGameRes로 전달 |

### 11.1 선택적 암호화 (Selective Encryption)

암호화는 **패킷 단위**로 선택적 적용된다. ECDH 키 교환으로 암호화 **능력**을 확보한 후, 각 패킷에 암호화를 적용할지는 송신 측이 결정한다.

#### Server → Client (S2C)

GameServer가 `Response` 힌트로 제어:

```csharp
// 암호화 필요 (민감 데이터: ReconnectKey, 인증 토큰 등)
return Response.Ok(sessionKeyRes).UseEncrypt();

// 암호화 불필요 (일반 게임 데이터)
return Response.Ok(moveRes);
```

#### Client → Server (C2S)

`SendOptions` 구조체로 제어:

```csharp
/// <summary>
/// 클라이언트 Outbound 패킷 옵션 (C2S)
/// 서버 측 Response.UseEncrypt()/UseCompress()의 클라이언트 대칭
/// </summary>
public readonly struct SendOptions
{
    public static readonly SendOptions Default = new();
    public static readonly SendOptions Encrypted = new(encrypt: true);

    public bool Encrypt { get; init; }

    public SendOptions(bool encrypt = false)
    {
        Encrypt = encrypt;
    }
}
```

**사용 예시**:
```csharp
// 민감 데이터 전송 (JWT, ReconnectKey 등)
await client.RequestAsync<LoginGameReq, LoginGameRes>(
    new LoginGameReq { Credential = ... }, SendOptions.Encrypted, cancellationToken);

// 일반 게임 패킷 (암호화 불필요)
client.Send(new MoveReq { X = 10, Y = 20 });  // SendOptions.Default
```

**설계 원칙**:
- `SendOptions`는 `readonly struct` (GC 0, 값 복사)
- `static readonly` 프리셋으로 일반적인 케이스 커버 (`Default`, `Encrypted`)
- 서버 측은 클라이언트의 SendOptions와 무관하게 독립적으로 `Response.UseEncrypt()` 결정
- Gateway는 `EndPointHeader.IsEncrypted` 플래그만 확인 → C2S 혼합 트래픽(암호화+평문) 이미 지원

**InternalSend 동작**:
```csharp
// options.Encrypt가 true이고, 암호화 키가 활성화된 경우에만 암호화
if (options.Encrypt && _encryptionActive && _encryptAesGcm != null &&
    TryEncryptPacket(span, out encryptedBuffer, out var encLen))
{
    // 암호화 전송
}
else
{
    // 평문 전송
}
```

### 11.2 ReconnectKey 보호 설계

ReconnectKey는 세션 재접속 토큰으로, 탈취 시 세션 하이재킹이 가능하다.

**이전 (취약)**:
```
HandshakeRes (평문, FlagHandshake)
├── ServerEphemeralPublicKey
├── ServerEphemeralSignature
└── ReconnectKey              ← 평문 노출! 패시브 도청으로 탈취 가능
```

**현재 (보호)**:
```
HandshakeRes (평문, FlagHandshake)
├── ServerEphemeralPublicKey
└── ServerEphemeralSignature
                                (ReconnectKey 없음)
    ↓ 암호화 활성화 ↓

LoginGameReq   → (암호화, C2S)
LoginGameRes   ← (암호화, S2C)
└── ReconnectKey              ← AES-256-GCM으로 보호됨
```

**RTT 최적화 (3→2)**: SessionKeyReq/Res 단계를 제거하고 ReconnectKey를 LoginGameRes에 포함.
LoginGameReq는 클라이언트가 암호화 활성화 완료 후 전송하므로 타이밍 안전.

---

## 12. 구현 파일 참조

| 파일 | 역할 |
|------|------|
| `SimpleNetEngine.Protocol/Packets/Headers.cs` | EndPointHeader (12B) 정의 |
| `SimpleNetEngine.Gateway/Network/GatewaySession.cs` | ECDH 키 생성, ECDSA 서명, 암호화 활성화 |
| `SimpleNetEngine.Gateway/Network/GatewayTcpServer.cs` | ECDSA 서명키 PEM 로드 |
| `SimpleNetEngine.Gateway/Network/PacketEncryptor.cs` | AES-256-GCM 암호화/복호화 (AAD + 카운터 Nonce) |
| `SimpleNetEngine.Gateway/Network/PacketCompressor.cs` | LZ4 압축/해제 |
| `SimpleNetEngine.Gateway/Network/GamePacketRouter.cs` | Outbound 파이프라인 (Compress → Encrypt) |
| `SimpleNetEngine.Gateway/Controllers/ControlController.cs` | ActivateEncryption RPC 핸들러 |
| `SimpleNetEngine.Game/Network/ConnectionController.cs` | NewUserNtf → Actor 생성, ECDH 공개키 저장 |
| `SimpleNetEngine.Game/Controllers/HandshakeController.cs` | HandshakeReq 처리, ActivateEncryption RPC 전송 |
| `NetworkClient/NetClient.cs` | 클라이언트 암호화/복호화 활성화, SendOptions 선택적 암호화 |
| `NetworkClient/Network/ProtoPacketParser.cs` | 클라이언트 Inbound (Decrypt → Decompress) |
| `NetworkClient/Network/GameHandshakeHandler.cs` | 클라이언트 ECDH + ECDSA 서명 검증 |
