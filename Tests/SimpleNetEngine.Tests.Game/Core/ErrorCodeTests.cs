using FluentAssertions;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// ErrorCode enum의 ABCCC 규칙 검증
/// A = 발생 계층 (1=Gateway, 2=GameServer, 3=Stateless)
/// B = 카테고리 (0=Connection, 1=Session, 2=Actor, 3=Storage, 4=Protocol, 9=Internal)
/// CCC = 상세코드 (001~999)
/// </summary>
public class ErrorCodeTests
{
    [Theory]
    [InlineData(ErrorCode.GatewayGameServerShutdown, 1, 0)]
    [InlineData(ErrorCode.GatewaySessionExpired, 1, 1)]
    [InlineData(ErrorCode.GatewayInvalidActorState, 1, 2)]
    [InlineData(ErrorCode.GameConnectionFailed, 2, 0)]
    [InlineData(ErrorCode.GameSessionNotFound, 2, 1)]
    [InlineData(ErrorCode.GameActorNotFound, 2, 2)]
    [InlineData(ErrorCode.GameStorageConnectionFailed, 2, 3)]
    [InlineData(ErrorCode.GameInvalidRequest, 2, 4)]
    [InlineData(ErrorCode.GameInternalError, 2, 9)]
    [InlineData(ErrorCode.StatelessInternalError, 3, 0)]
    public void ErrorCode_Should_Follow_ABCCC_Format(ErrorCode code, int expectedLayer, int expectedCategory)
    {
        var value = (short)code;
        var layer = value / 10000;
        var category = (value % 10000) / 1000;

        layer.Should().Be(expectedLayer, $"{code} should belong to layer {expectedLayer}");
        category.Should().Be(expectedCategory, $"{code} should belong to category {expectedCategory}");
    }

    [Fact]
    public void All_ErrorCodes_Should_Be_Within_Short_Range()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var value = (short)code;
            value.Should().BeInRange((short)0, short.MaxValue,
                $"{code}={value} should be within short range");
        }
    }

    [Fact]
    public void All_NonZero_ErrorCodes_Should_Be_5_Digits()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            if (code == ErrorCode.None) continue;

            var value = (short)code;
            value.Should().BeGreaterThanOrEqualTo((short)10001,
                $"{code}={value} should be at least 5 digits (10001+)");
            value.Should().BeLessThanOrEqualTo(short.MaxValue,
                $"{code}={value} should be within short range");
        }
    }

    [Fact]
    public void All_ErrorCodes_Should_Have_Unique_Values()
    {
        var codes = Enum.GetValues<ErrorCode>();
        var values = codes.Select(c => (short)c).ToList();

        values.Should().OnlyHaveUniqueItems("ErrorCode values must be unique");
    }

    [Fact]
    public void Detail_Code_Should_Be_Between_001_And_999()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            if (code == ErrorCode.None) continue;

            var value = (short)code;
            var detail = value % 1000;
            detail.Should().BeGreaterThanOrEqualTo(1,
                $"{code}={value} detail part should be >= 001");
            detail.Should().BeLessThanOrEqualTo(999,
                $"{code}={value} detail part should be <= 999");
        }
    }

    [Fact]
    public void ErrorCode_Should_Cast_To_Short_For_EndPointHeader()
    {
        // EndPointHeader.ErrorCode는 short 타입이므로 캐스트 가능해야 함
        short headerErrorCode = (short)ErrorCode.GatewayGameServerShutdown;
        headerErrorCode.Should().Be(10001);

        short sessionError = (short)ErrorCode.GameSessionExpired;
        sessionError.Should().Be(21002);
    }
}
