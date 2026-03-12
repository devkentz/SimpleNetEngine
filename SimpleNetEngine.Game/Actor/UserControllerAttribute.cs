namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// User Controller Attribute (Game Session Channel - Data Plane).
/// 클라이언트 패킷을 처리하는 Controller 클래스에 적용됩니다.
/// Source Generator가 컴파일 타임에 핸들러 등록 코드를 자동 생성합니다.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class UserControllerAttribute : Attribute { }
