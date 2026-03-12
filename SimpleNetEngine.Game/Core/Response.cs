using Google.Protobuf;

namespace SimpleNetEngine.Game.Core;

/// <summary>
/// Controller 핸들러의 응답 타입
/// TcpServer의 Response 패턴을 GameServer에 적응
/// </summary>
public class Response(IMessage? message)
{
    /// <summary>
    /// Protobuf 메시지. IsError == true이면 null (Payload 없는 에러 전용 응답)
    /// </summary>
    public IMessage? Message { get; } = message;

    public bool IsCompress { get; private set; }
    public bool IsEncryption { get; private set; }
    public int ErrorCode { get; private set; }

    /// <summary>
    /// true이면 Payload 없는 에러 전용 응답 → EndPointHeader.ErrorCode만 전송
    /// </summary>
    public bool IsError => Message == null;

    public Response UseCompress()
    {
        IsCompress = true;
        return this;
    }

    public Response UseEncrypt()
    {
        IsEncryption = true;
        return this;
    }

    /// <summary>
    /// 성공 응답 생성
    /// </summary>
    public static Response Ok(IMessage message)
    {
        return new Response(message);
    }

    /// <summary>
    /// Payload 없는 에러 전용 응답 생성 (EndPointHeader.ErrorCode만 전송)
    /// </summary>
    public static Response Error(short errorCode)
    {
        var res = new Response(null);
        res.ErrorCode = errorCode;
        return res;
    }

    /// <summary>
    /// Protobuf 메시지를 포함한 에러 응답 생성 (기존 호환)
    /// </summary>
    public static Response Error(ushort errorCode, IMessage message)
    {
        var res = new Response(message);
        res.ErrorCode = errorCode;
        return res;
    }
}
