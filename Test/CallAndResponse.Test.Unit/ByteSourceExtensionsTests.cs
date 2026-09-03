using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Tests for <see cref="DuplexPipeExtensions.AsTransceiver"/>.
/// </summary>
public class DuplexPipeExtensionsTests
{
    private static CancellationToken Token(int ms = 2000) =>
        new CancellationTokenSource(ms).Token;

    [Fact]
    public void AsTransceiver_ReturnsNonNullTransceiver()
    {
        var pipe = new FakeDuplexPipe();

        ITransceiver transceiver = pipe.AsTransceiver();

        transceiver.Should().NotBeNull();
    }

    [Fact]
    public async Task AsTransceiver_CanSendAndReceive()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        ITransceiver transceiver = pipe.AsTransceiver();
        var result = await transceiver.SendReceive(new byte[] { 0xAA }, Frame.Exactly(3), Token());

        pipe.SentBytes.Should().Equal(0xAA);
        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public void AsTransceiver_ReturnsSealedTransceiverType()
    {
        var pipe = new FakeDuplexPipe();

        ITransceiver transceiver = pipe.AsTransceiver();

        transceiver.Should().BeOfType<Transceiver>();
    }
}
