using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Game.Protocol;
using Google.Protobuf;

namespace SimpleNetEngine.Client.Network;

/// <summary>
/// Game.Protocol 기반 Handshake 처리기 (ECDH 키 교환 + ECDSA 서명 검증)
/// 1. ReadyToHandshakeNtf 수신 대기 (GameServer가 Actor 생성 완료 후 전송)
/// 2. ECDH P-256 키쌍 생성
/// 3. HandshakeReq (클라이언트 공개키 포함) 전송 → HandshakeRes 수신 (평문)
/// 4. ECDSA 서명 검증 (MITM 방지)
/// 5. Gateway 공개키로 SharedSecret 도출 → HKDF → AES-256 키 생성 → 암호화 활성화
/// 6. LoginGameRes(암호화)에서 ReconnectKey 수신
/// </summary>
public class GameHandshakeHandler : IHandshakeHandler
{
    // HKDF 파라미터 (서버와 동일해야 함)
    private static readonly byte[] HkdfSalt = "NE-v1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "NetworkEngine-AES256-GCM"u8.ToArray();

    private readonly ECDsa? _serverVerificationKey;

    /// <summary>
    /// 서명 검증 없이 생성 (개발/테스트용, MITM 취약)
    /// </summary>
    public GameHandshakeHandler() : this((ECDsa?)null)
    {
    }

    /// <summary>
    /// 서버 공개키로 생성 (MITM 방지)
    /// </summary>
    public GameHandshakeHandler(ECDsa? serverVerificationKey)
    {
        _serverVerificationKey = serverVerificationKey;
    }

    /// <summary>
    /// PEM 문자열에서 서버 공개키 로드하여 생성
    /// </summary>
    public GameHandshakeHandler(string serverPublicKeyPem)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(serverPublicKeyPem);
        _serverVerificationKey = ecdsa;
    }

    /// <summary>
    /// PEM 파일 경로에서 서버 공개키를 로드하여 생성.
    /// 파일이 존재하면 ECDSA 서명 검증 활성화, 없으면 서명 검증 없이 동작 (개발 모드).
    /// </summary>
    /// <param name="pemFilePath">서버 공개키 PEM 파일 경로 (null이면 서명 검증 없음)</param>
    /// <returns>GameHandshakeHandler 인스턴스</returns>
    public static GameHandshakeHandler CreateFromPemFile(string? pemFilePath)
    {
        if (pemFilePath != null && File.Exists(pemFilePath))
        {
            var pem = File.ReadAllText(pemFilePath);
            return new GameHandshakeHandler(pem);
        }

        return new GameHandshakeHandler();
    }

    public async Task<HandshakeResult> PerformHandshakeAsync(NetClient client, CancellationToken cancellationToken)
    {
        // 1. GameServer가 Actor 생성 완료 후 보내는 ReadyToHandshakeNtf 대기
        await client.WaitForMessageAsync<ReadyToHandshakeNtf>(
            ReadyToHandshakeNtf.MsgId, cancellationToken).ConfigureAwait(false);

        // 2. ECDH P-256 키쌍 생성
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPublicKeyDer = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        // 3. HandshakeReq 전송 (클라이언트 공개키 포함) → HandshakeRes 수신 (평문)
        var response = await client.RequestAsync<HandshakeReq, HandshakeRes>(new HandshakeReq
            {
                ClientEphemeralPublicKey = ByteString.CopyFrom(clientPublicKeyDer)
            }, cancellationToken).ConfigureAwait(false);

        // 4. Gateway ECDH 공개키로 SharedSecret 도출 → AES-256 키 생성
        if (!response.ServerEphemeralPublicKey.IsEmpty)
        {
            // 4a. ECDSA 서명 검증 (MITM 방지)
            if (_serverVerificationKey != null)
            {
                if (response.ServerEphemeralSignature.IsEmpty)
                    throw new CryptographicException("Server did not provide signature for ECDH public key");

                var isValid = _serverVerificationKey.VerifyData(
                    response.ServerEphemeralPublicKey.Span,
                    response.ServerEphemeralSignature.Span,
                    HashAlgorithmName.SHA256);

                if (!isValid)
                    throw new CryptographicException("Server ECDH public key signature verification failed (possible MITM attack)");
            }

            // 4b. SharedSecret 도출 (NIST SP 800-56C 준수: raw secret → HKDF)
            using var serverEcdh = ECDiffieHellman.Create();
            serverEcdh.ImportSubjectPublicKeyInfo(response.ServerEphemeralPublicKey.Span, out _);

            var sharedSecret = ecdh.DeriveRawSecretAgreement(serverEcdh.PublicKey);

            var aesKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                outputLength: 32,
                salt: HkdfSalt,
                info: HkdfInfo);

            CryptographicOperations.ZeroMemory(sharedSecret);

            // 5. 암호화 즉시 활성화 (LoginGameReq 전에 필요)
            //    Gateway는 이미 ActivateEncryption RPC 완료 상태
            client.ActivateEncryption(aesKey);
        }

        // 6. 서버 옵션 적용: Idle Ping 활성화 (서버가 지정한 간격)
        if (response.PingIntervalMs > 0)
            client.EnablePing(response.PingIntervalMs);

        return new HandshakeResult();
    }
}
