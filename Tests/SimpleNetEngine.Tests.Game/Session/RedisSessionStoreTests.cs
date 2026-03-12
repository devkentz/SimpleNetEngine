using FluentAssertions;
using SimpleNetEngine.Game.Session;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;

namespace SimpleNetEngine.Tests.Game.Session;

public class RedisSessionStoreTests
{
    private readonly Mock<ILogger<RedisSessionStore>> _loggerMock;
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly RedisSessionStore _sut;

    public RedisSessionStoreTests()
    {
        _loggerMock = new Mock<ILogger<RedisSessionStore>>();
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();

        _redisMock.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);

        _sut = new RedisSessionStore(_loggerMock.Object, _redisMock.Object);
    }

    [Fact]
    public async Task GetSessionAsync_WhenSessionExists_ShouldReturnSessionInfo()
    {
        // Arrange
        var userId = 123L;
        var sessionInfo = new SessionInfo
        {
            GameServerNodeId = 1,
            SessionId = 456L,
            GatewayNodeId = 789L,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(sessionInfo);
        _dbMock.Setup(x => x.StringGetAsync("session:123", It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        // Act
        var result = await _sut.GetSessionAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.GameServerNodeId.Should().Be(sessionInfo.GameServerNodeId);
        result.SessionId.Should().Be(sessionInfo.SessionId);
    }

    [Fact]
    public async Task GetSessionAsync_WhenSessionDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var userId = 123L;
        _dbMock.Setup(x => x.StringGetAsync("session:123", It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await _sut.GetSessionAsync(userId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSessionAsync_WhenRedisThrows_ShouldPropagateException()
    {
        // Arrange
        var userId = 123L;
        _dbMock.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection failed"));

        // Act
        Func<Task> act = async () => await _sut.GetSessionAsync(userId);

        // Assert
        await act.Should().ThrowAsync<RedisConnectionException>()
            .WithMessage("*Connection failed*");
    }

    [Fact]
    public async Task SetSessionAsync_ShouldCallRedisStringSet()
    {
        // Arrange
        var userId = 123L;
        var sessionInfo = new SessionInfo
        {
            GameServerNodeId = 1,
            SessionId = 456L,
            GatewayNodeId = 789L,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        };

        // Act
        var act = async () => await _sut.SetSessionAsync(userId, sessionInfo);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteSessionAsync_ShouldCallRedisKeyDelete()
    {
        // Arrange
        var userId = 123L;

        // Act
        await _sut.DeleteSessionAsync(userId);

        // Assert
        _dbMock.Verify(x => x.KeyDeleteAsync(
            "session:123",
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task IsSessionOnNodeAsync_WhenSessionOnNode_ShouldReturnTrue()
    {
        // Arrange
        var userId = 123L;
        var gameServerNodeId = 1L;
        var sessionInfo = new SessionInfo
        {
            GameServerNodeId = gameServerNodeId,
            SessionId = 456L
        };

        var json = JsonSerializer.Serialize(sessionInfo);
        _dbMock.Setup(x => x.StringGetAsync("session:123", It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        // Act
        var result = await _sut.IsSessionOnNodeAsync(userId, gameServerNodeId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsSessionOnNodeAsync_WhenSessionOnDifferentNode_ShouldReturnFalse()
    {
        // Arrange
        var userId = 123L;
        var gameServerNodeId = 1L;
        var sessionInfo = new SessionInfo
        {
            GameServerNodeId = 2L, // Different node
            SessionId = 456L
        };

        var json = JsonSerializer.Serialize(sessionInfo);
        _dbMock.Setup(x => x.StringGetAsync("session:123", It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        // Act
        var result = await _sut.IsSessionOnNodeAsync(userId, gameServerNodeId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsSessionOnNodeAsync_WhenRedisThrows_ShouldPropagateException()
    {
        // Arrange
        var userId = 123L;
        var gameServerNodeId = 1L;

        _dbMock.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        // Act
        Func<Task> act = async () => await _sut.IsSessionOnNodeAsync(userId, gameServerNodeId);

        // Assert
        await act.Should().ThrowAsync<RedisConnectionException>()
            .WithMessage("*Redis down*");
    }
}
