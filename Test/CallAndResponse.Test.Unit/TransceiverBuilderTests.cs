using Moq;
using Serilog;

namespace CallAndResponse.Test.Unit;

public class TransceiverBuilderTests
{
    [Fact]
    public void Build_WithoutFactory_ThrowsInvalidOperationException()
    {
        var builder = new TransceiverBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_WithFactory_ReturnsTransceiverFromFactory()
    {
        var mockTransceiver = new Mock<ITransceiver>();
        var mockFactory = new Mock<ITransceiverFactory>();
        mockFactory
            .Setup(f => f.CreateTransceiver(It.IsAny<ILogger>()))
            .Returns(mockTransceiver.Object);

        var builder = new TransceiverBuilder() with { _transceiverFactory = mockFactory.Object };
        var result = builder.Build();

        Assert.Same(mockTransceiver.Object, result);
        mockFactory.Verify(f => f.CreateTransceiver(It.IsAny<ILogger>()), Times.Once);
    }

    [Fact]
    public void UseLogger_ReturnsNewBuilderWithLogger()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var builder = new TransceiverBuilder();

        var newBuilder = builder.UseLogger(logger);

        Assert.Same(logger, newBuilder._logger);
        Assert.NotSame(builder, newBuilder);
    }

    [Fact]
    public void UseLogger_PassesLoggerToFactory()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        ILogger? capturedLogger = null;

        var mockTransceiver = new Mock<ITransceiver>();
        var mockFactory = new Mock<ITransceiverFactory>();
        mockFactory
            .Setup(f => f.CreateTransceiver(It.IsAny<ILogger>()))
            .Callback<ILogger>(l => capturedLogger = l)
            .Returns(mockTransceiver.Object);

        var builder = new TransceiverBuilder()
            .UseLogger(logger) with
        { _transceiverFactory = mockFactory.Object };

        builder.Build();

        Assert.Same(logger, capturedLogger);
    }

    [Fact]
    public void DefaultLogger_IsNotNull()
    {
        var builder = new TransceiverBuilder();
        Assert.NotNull(builder._logger);
    }
}
