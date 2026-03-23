# Sample - NetworkEngine Echo 데모

NetworkEngine.Merged 라이브러리를 사용하는 샘플 프로젝트입니다.
Gateway + GameServer + TestClient 구성으로 Echo 요청/응답 플로우를 시연합니다.

## 프로젝트 구성

| 프로젝트 | 역할 |
|----------|------|
| **Sample.AppHost** | .NET Aspire 오케스트레이터 (Garnet + Gateway + GameServer 일괄 실행) |
| **GatewaySample** | Gateway 서버 — TCP 소켓 관리, 패킷 전달 (Dumb Proxy) |
| **GameSample** | GameServer — Actor 기반 패킷 처리, EchoController 포함 |
| **TestClient** | 콘솔 클라이언트 — 핸드셰이크 + 로그인 + Echo 요청 |
| **Protocol.Sample** | Protobuf 메시지 정의 (EchoReq, EchoRes, LoginGameReq 등) |
| **NetworkServer.Sample** | 확장 샘플 (커스텀 Actor, Controller, Handler) |

## 실행 방법

### Aspire로 실행 (권장)

```bash
cd Sample/Sample.AppHost
dotnet run
```

- Garnet(Redis 호환) 인프로세스 서버 자동 시작 (port 6379)
- Gateway 1개 + GameServer 2개 자동 실행
- Aspire Dashboard에서 로그/트레이스/메트릭 확인 가능

### 개별 실행

```bash
# 1. Redis 또는 Garnet 실행
# 2. Gateway
cd Sample/GatewaySample && dotnet run
# 3. GameServer
cd Sample/GameSample && dotnet run
# 4. Client
cd Sample/TestClient && dotnet run [host] [port]
```

## Echo 플로우

```
Client → Gateway → GameServer → EchoController → GameServer → Gateway → Client
```

1. **연결**: Client가 Gateway에 TCP 연결
2. **핸드셰이크**: ECDH P-256 키 교환 + ECDSA 서명 검증 → AES-256-GCM 암호화 활성화
3. **로그인**: LoginGameReq → Redis 세션 등록 → Actor 활성화
4. **Echo**: EchoReq (암호화) → Gateway 전달 → Actor Mailbox → EchoController → EchoRes (압축+암호화)

## 프로토콜 정의

`Protocol.Sample/sample.proto`:

```protobuf
message EchoReq {
    string message = 1;
    int32 timestamp = 2;
}

message EchoRes {
    string message = 1;
    int32 timestamp = 2;
}

message LoginGameReq {
    string ExternalId = 1;
}

message LoginGameRes {
    bool Success = 1;
}
```

## 인증서

`certs/` 디렉토리에 개발용 ECDSA P-256 키쌍이 포함되어 있습니다.

- `server_signing_key.pem` — 서버 서명용 개인키 (개발 전용)
- `server_signing_key.pub.pem` — 클라이언트 검증용 공개키

> **주의**: 프로덕션에서는 KMS/Vault 등 외부 키 관리 시스템을 사용하세요.
