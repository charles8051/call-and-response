using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;
using System.Text;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Tier 2 — exercises every default implementation on <see cref="Transceiver"/>
/// via an in-memory <see cref="FakeDuplexPipe"/>. No real I/O; no mocking.
/// </summary>
public class TransceiverTests
{
    private static CancellationToken Token(int ms = 2000) =>
        new CancellationTokenSource(ms).Token;

    // =========================================================================
    // ReceiveExactly
    // =========================================================================

    [Fact]
    public async Task ReceiveExactly_ExactBytesEnqueued_ReturnsThem()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var result = await sut.ReceiveExactly(3, Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task ReceiveExactly_BytesArriveIncrementally_WaitsAndReturnsAll()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0xBB);
        _ = Task.Run(async () => { await Task.Delay(50); pipe.EnqueueRx(0xCC); });

        var result = await sut.ReceiveExactly(3, Token());

        result.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public async Task ReceiveExactly_ReturnsExactCount_NotMore()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04, 0x05);

        var result = await sut.ReceiveExactly(3, Token());

        result.Length.Should().Be(3);
        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    // =========================================================================
    // ReceiveUntilTerminator (char)
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilTerminator_CharTerminator_ReturnsPayloadBeforeTerminator()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\n');

        var result = await sut.ReceiveUntilTerminator('\n', Token());

        result.ToArray().Should().Equal((byte)'O', (byte)'K');
    }

    [Fact]
    public async Task ReceiveUntilTerminator_TerminatorCharNotIncludedInResult()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'H', (byte)'i', (byte)'\r');

        var result = await sut.ReceiveUntilTerminator('\r', Token());

        result.ToArray().Should().NotContain((byte)'\r');
    }

    // =========================================================================
    // ReceiveUntilTerminatorPattern
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_BinaryPattern_ReturnsPayloadBeforePattern()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0xFF, 0xFE);

        var result = await sut.ReceiveUntilTerminatorPattern(new byte[] { 0xFF, 0xFE }, Token());

        result.ToArray().Should().Equal(0x01, 0x02);
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_CrLfPattern_ReturnsLineContent()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\r', (byte)'\n');

        var result = await sut.ReceiveUntilTerminatorPattern(new byte[] { (byte)'\r', (byte)'\n' }, Token());

        Encoding.ASCII.GetString(result.ToArray()).Should().Be("OK");
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_PatternNotIncludedInResult()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0xBB, 0xCC, 0xDD);

        var result = await sut.ReceiveUntilTerminatorPattern(new byte[] { 0xCC, 0xDD }, Token());

        result.ToArray().Should().NotContain(0xCC);
        result.ToArray().Should().NotContain(0xDD);
    }

    // =========================================================================
    // ReceiveUntilPerfectMatch
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilPerfectMatch_ExactMatch_ReturnsMatchBytes()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var result = await sut.ReceiveUntilPerfectMatch(new byte[] { 0x01, 0x02, 0x03 }, Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task ReceiveUntilPerfectMatch_MatchAfterLeadingBytes_ReturnsMatchBytesOnly()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x00, 0x01, 0x02);

        var result = await sut.ReceiveUntilPerfectMatch(new byte[] { 0x01, 0x02 }, Token());

        result.ToArray().Should().Equal(0x01, 0x02);
    }

    // =========================================================================
    // ReceiveUntilHeaderFooterMatch
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_ReturnsBytesBeforeFooter()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0x01, 0x02, 0xBB);

        var result = await sut.ReceiveUntilHeaderFooterMatch(
            new byte[] { 0xAA },
            new byte[] { 0xBB },
            Token());

        result.ToArray().Should().Equal(0x01, 0x02);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_PreHeaderBytesIgnored()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x00, 0x00, 0xAA, 0x05, 0x06, 0xBB);

        var result = await sut.ReceiveUntilHeaderFooterMatch(
            new byte[] { 0xAA },
            new byte[] { 0xBB },
            Token());

        result.ToArray().Should().Equal(0x05, 0x06);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_HeaderAndFooterNotIncludedInResult()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0x07, 0x08, 0xBB);

        var result = await sut.ReceiveUntilHeaderFooterMatch(
            new byte[] { 0xAA },
            new byte[] { 0xBB },
            Token());

        result.ToArray().Should().NotContain(0xAA);
        result.ToArray().Should().NotContain(0xBB);
    }

    // =========================================================================
    // SendReceiveExactly
    // =========================================================================

    [Fact]
    public async Task SendReceiveExactly_TransmitsBytesAndReceivesResponse()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x11, 0x22, 0x33);

        var result = await sut.SendReceiveExactly(new byte[] { 0xAA, 0xBB }, 3, Token());

        pipe.SentBytes.Should().Equal(0xAA, 0xBB);
        result.ToArray().Should().Equal(0x11, 0x22, 0x33);
    }

    // =========================================================================
    // SendReceiveString (char terminator)
    // =========================================================================

    [Fact]
    public async Task SendReceiveString_CharTerminator_TransmitsStringAndReturnsPayload()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\n');

        var result = await sut.SendReceiveString("AT\r", '\n', Token());

        pipe.SentBytes.Should().Equal(Encoding.ASCII.GetBytes("AT\r"));
        result.Should().Be("OK");
    }

    // =========================================================================
    // SendReceiveString (string terminator)
    // =========================================================================

    [Fact]
    public async Task SendReceiveString_StringTerminator_TransmitsStringAndReturnsPayload()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', (byte)'\r', (byte)'\n');

        var result = await sut.SendReceiveString("QUERY\r\n", "\r\n", Token());

        result.Should().Be("HELLO");
    }

    // =========================================================================
    // SendReceivePerfectMatch
    // =========================================================================

    [Fact]
    public async Task SendReceivePerfectMatch_TransmitsBytesAndReturnsMatchedPayload()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var result = await sut.SendReceivePerfectMatch(
            new byte[] { 0xAA },
            new byte[] { 0x01, 0x02, 0x03 },
            Token());

        pipe.SentBytes.Should().Equal(0xAA);
        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    // =========================================================================
    // SendReceiveFooter
    // =========================================================================

    [Fact]
    public async Task SendReceiveFooter_TransmitsBytesAndReturnsPayloadBeforeFooter()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0xFF, 0xFE);

        var result = await sut.SendReceiveFooter(
            new byte[] { 0xAA },
            new byte[] { 0xFF, 0xFE },
            Token());

        pipe.SentBytes.Should().Equal(0xAA);
        result.ToArray().Should().Equal(0x01, 0x02);
    }

    // =========================================================================
    // SendReceive (custom detector delegate)
    // =========================================================================

    [Fact]
    public async Task SendReceive_CustomDetector_TransmitsBytesAndReturnsMessage()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04, 0x05);

        // Complete when 5 bytes have accumulated; skip the first 2, take the last 3.
        var result = await sut.SendReceive(
            new byte[] { 0xAA },
            readBytes => readBytes.Length >= 5
                ? FrameDetectionResult.Complete(2, 3)
                : FrameDetectionResult.Incomplete,
            Token());

        result.ToArray().Should().Equal(0x03, 0x04, 0x05);
    }

    // =========================================================================
    // SendReceiveHeaderFooter
    // =========================================================================

    [Fact]
    public async Task SendReceiveHeaderFooter_TransmitsBytesAndReturnsBetweenHeaderAndFooter()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0x07, 0x08, 0xBB);

        var result = await sut.SendReceiveHeaderFooter(
            new byte[] { 0xFF },
            new byte[] { 0xAA },
            new byte[] { 0xBB },
            Token());

        pipe.SentBytes.Should().Equal(0xFF);
        result.ToArray().Should().Equal(0x07, 0x08);
    }

    // =========================================================================
    // ReceiveUntilIdle
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilIdle_SingleBurst_ReturnsAllBytes()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var result = await sut.ReceiveUntilIdle(TimeSpan.FromMilliseconds(100), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task ReceiveUntilIdle_TwoBurstsWithinIdleWindow_AccumulatesBothBursts()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0x03);
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            pipe.EnqueueRx(0x04, 0x05, 0x06);
        });

        var result = await sut.ReceiveUntilIdle(TimeSpan.FromMilliseconds(200), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03, 0x04, 0x05, 0x06);
    }

    [Fact]
    public async Task ReceiveUntilIdle_IdleTimeoutResetsAfterEachBurst_DoesNotReturnEarly()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // Idle timeout = 100ms. Without a reset the method would return at ~100ms
        // with only the first burst. A correct implementation resets the clock on
        // each arrival so the second burst (at ~80ms) is included in the result.
        pipe.EnqueueRx(0xAA, 0xBB);
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            pipe.EnqueueRx(0xCC, 0xDD);
        });

        var result = await sut.ReceiveUntilIdle(TimeSpan.FromMilliseconds(100), Token());

        result.ToArray().Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
    }

    [Fact]
    public async Task ReceiveUntilIdle_KeepAlive_ManyBurstsWithinIdleWindow_AccumulatesAll()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // 5 bursts at 50 ms intervals — each well within the 200 ms idle timeout.
        // The method must stay alive through every burst and return all bytes together.
        _ = Task.Run(async () =>
        {
            for (byte i = 1; i <= 5; i++)
            {
                pipe.EnqueueRx(i);
                await Task.Delay(50);
            }
        });

        var result = await sut.ReceiveUntilIdle(TimeSpan.FromMilliseconds(200), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03, 0x04, 0x05);
    }

    [Fact]
    public async Task ReceiveUntilIdle_NoBytesArrive_KeepsWaitingUntilCancellationTokenFires()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // The idle timeout fires repeatedly but with zero bytes accumulated the
        // method must re-enter the loop; only the outer token should stop it.
        using var cts = new CancellationTokenSource(150);
        var act = async () => await sut.ReceiveUntilIdle(TimeSpan.FromMilliseconds(50), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReceiveUntilIdle_CancellationTokenTakesPriority()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        using var cts = new CancellationTokenSource(100);
        var act = async () => await sut.ReceiveUntilIdle(TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
