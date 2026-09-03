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

        var result = await sut.Receive(Frame.Exactly(3), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task ReceiveExactly_BytesArriveIncrementally_WaitsAndReturnsAll()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0xBB);
        _ = Task.Run(async () => { await Task.Delay(50); pipe.EnqueueRx(0xCC); });

        var result = await sut.Receive(Frame.Exactly(3), Token());

        result.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public async Task ReceiveExactly_ReturnsExactCount_NotMore()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04, 0x05);

        var result = await sut.Receive(Frame.Exactly(3), Token());

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

        var result = await sut.Receive(Frame.UntilTerminator((byte)'\n'), Token());

        result.ToArray().Should().Equal((byte)'O', (byte)'K');
    }

    [Fact]
    public async Task ReceiveUntilTerminator_TerminatorCharNotIncludedInResult()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'H', (byte)'i', (byte)'\r');

        var result = await sut.Receive(Frame.UntilTerminator((byte)'\r'), Token());

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

        var result = await sut.Receive(Frame.UntilPattern(new byte[] { 0xFF, 0xFE }), Token());

        result.ToArray().Should().Equal(0x01, 0x02);
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_CrLfPattern_ReturnsLineContent()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\r', (byte)'\n');

        var result = await sut.Receive(Frame.UntilPattern(new byte[] { (byte)'\r', (byte)'\n' }), Token());

        Encoding.ASCII.GetString(result.ToArray()).Should().Be("OK");
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_ReturnsBytesBeforeFooter()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0x01, 0x02, 0xBB);

        var result = await sut.Receive(Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());

        result.ToArray().Should().Equal(0x01, 0x02);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_PreHeaderBytesIgnored()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x00, 0x00, 0xAA, 0x05, 0x06, 0xBB);

        var result = await sut.Receive(Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());

        result.ToArray().Should().Equal(0x05, 0x06);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_HeaderAndFooterNotIncludedInResult()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0xAA, 0x07, 0x08, 0xBB);

        var result = await sut.Receive(Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());

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

        var result = await sut.SendReceive(new byte[] { 0xAA, 0xBB }, Frame.Exactly(3), Token());

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
    public async Task SendReceiveFooter_TransmitsBytesAndReturnsPayloadBeforeFooter()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();
        pipe.EnqueueRx(0x01, 0x02, 0xFF, 0xFE);

        var result = await sut.SendReceive(new byte[] { 0xAA }, Frame.UntilPattern(new byte[] { 0xFF, 0xFE }), Token());

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
            Frame.OverSpan((received, _, _, payload) =>
            {
                if (received.Length < 5) return FrameDecodeResult.NeedMoreData;
                payload.Write(received.Slice(2, 3));
                return FrameDecodeResult.Frame(5);
            }),
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

        var result = await sut.SendReceive(new byte[] { 0xFF }, Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());

        pipe.SentBytes.Should().Equal(0xFF);
        result.ToArray().Should().Equal(0x07, 0x08);
    }

    // =========================================================================
    // Delimiter consumption — the delimiter a detector matched must not be left
    // in the pipe to satisfy the next command's detector (issue #7).
    // =========================================================================

    [Fact]
    public async Task ReceiveUntilTerminator_TerminatorIsConsumed_NextReceiveDoesNotSeeIt()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // Two complete responses, back to back, on one transport.
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\n');
        pipe.EnqueueRx((byte)'1', (byte)'2', (byte)'\n');

        var first = await sut.Receive(Frame.UntilTerminator((byte)'\n'), Token());
        var second = await sut.Receive(Frame.UntilTerminator((byte)'\n'), Token());

        first.ToArray().Should().Equal((byte)'O', (byte)'K');
        second.ToArray().Should().Equal((byte)'1', (byte)'2');
    }

    [Fact]
    public async Task ReceiveUntilTerminator_TerminatorIsConsumed_FollowingExactReadIsNotShifted()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // "OK\n" then a fixed-width binary reply. A leftover '\n' would shift the
        // second read by one byte.
        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\n');
        pipe.EnqueueRx(0x04, 0x13);

        _ = await sut.Receive(Frame.UntilTerminator((byte)'\n'), Token());
        var second = await sut.Receive(Frame.Exactly(2), Token());

        second.ToArray().Should().Equal(0x04, 0x13);
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_PatternIsConsumed_NextReceiveDoesNotSeeIt()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0xFF, 0xFE);
        pipe.EnqueueRx(0x03, 0x04, 0xFF, 0xFE);

        var first = await sut.Receive(Frame.UntilPattern(new byte[] { 0xFF, 0xFE }), Token());
        var second = await sut.Receive(Frame.UntilPattern(new byte[] { 0xFF, 0xFE }), Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x03, 0x04);
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_PatternIsConsumed_FollowingExactReadIsNotShifted()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx((byte)'O', (byte)'K', (byte)'\r', (byte)'\n');
        pipe.EnqueueRx(0x04, 0x13);

        _ = await sut.Receive(Frame.UntilPattern(new byte[] { (byte)'\r', (byte)'\n' }), Token());
        var second = await sut.Receive(Frame.Exactly(2), Token());

        second.ToArray().Should().Equal(0x04, 0x13);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_FooterIsConsumed_NextReceiveDoesNotSeeIt()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0xAA, 0x01, 0x02, 0xBB);
        pipe.EnqueueRx(0xAA, 0x03, 0x04, 0xBB);

        var first = await sut.Receive(
            Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());
        var second = await sut.Receive(
            Frame.Between(new byte[] { 0xAA }, new byte[] { 0xBB }), Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x03, 0x04);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_MultiByteFooterIsConsumedWhole()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0xAA, 0xAA, 0x01, 0x02, 0xBB, 0xBB);
        pipe.EnqueueRx(0x04, 0x13);

        var first = await sut.Receive(
            Frame.Between(new byte[] { 0xAA, 0xAA }, new byte[] { 0xBB, 0xBB }), Token());
        var second = await sut.Receive(Frame.Exactly(2), Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x04, 0x13);
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_LeadingBytesAndMultiByteHeader_ConsumesTheWholeFrame()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        // Every offset that could go missing from the consumed length is non-zero here:
        // two bytes of noise before a two-byte header, and a two-byte footer. The frame
        // ends at index 8, so a consumed length measured from anywhere but the start of
        // the buffer leaves part of it behind and shifts the next read.
        pipe.EnqueueRx(0x00, 0x00, 0xAA, 0xAA, 0x01, 0x02, 0xBB, 0xBB);
        pipe.EnqueueRx(0x04, 0x13);

        var first = await sut.Receive(
            Frame.Between(new byte[] { 0xAA, 0xAA }, new byte[] { 0xBB, 0xBB }), Token());
        var second = await sut.Receive(Frame.Exactly(2), Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x04, 0x13);
    }

    [Fact]
    public async Task ReceiveExactly_ConsumesOnlyWhatItReturned_RemainderStaysForNextReceive()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04, 0x05);

        var first = await sut.Receive(Frame.Exactly(2), Token());
        var second = await sut.Receive(Frame.Exactly(3), Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x03, 0x04, 0x05);
    }

    // =========================================================================
    // FrameDecodeResult
    // =========================================================================

    [Fact]
    public void FrameDecodeResult_NeedMoreData_ConsumesNothing()
    {
        var result = FrameDecodeResult.NeedMoreData;

        result.Status.Should().Be(FrameDecodeStatus.NeedMoreData);
        result.ConsumedLength.Should().Be(0);
    }

    [Fact]
    public void FrameDecodeResult_Frame_CarriesOnlyTheConsumedExtent()
    {
        // The payload is written out rather than described, so a frame result has one
        // number where the old detection result had three.
        var result = FrameDecodeResult.Frame(5);

        result.Status.Should().Be(FrameDecodeStatus.Frame);
        result.ConsumedLength.Should().Be(5);
    }

    [Fact]
    public void FrameDecodeResult_ZeroLengthFrame_IsAccepted()
    {
        var result = FrameDecodeResult.Frame(0);

        result.Status.Should().Be(FrameDecodeStatus.Frame);
        result.ConsumedLength.Should().Be(0);
    }

    [Fact]
    public void FrameDecodeResult_Discard_CarriesTheBytesToDrop()
    {
        var result = FrameDecodeResult.Discard(3);

        result.Status.Should().Be(FrameDecodeStatus.Discard);
        result.ConsumedLength.Should().Be(3);
    }

    [Fact]
    public void FrameDecodeResult_Invalid_CarriesAReason()
    {
        var result = FrameDecodeResult.Invalid(4, "bad checksum");

        result.Status.Should().Be(FrameDecodeStatus.Invalid);
        result.ConsumedLength.Should().Be(4);
        result.Reason.Should().Be("bad checksum");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void FrameDecodeResult_NegativeConsumedLength_Throws(int consumedLength)
    {
        var frame = () => FrameDecodeResult.Frame(consumedLength);
        var discard = () => FrameDecodeResult.Discard(consumedLength);
        var invalid = () => FrameDecodeResult.Invalid(consumedLength, "reason");

        frame.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("consumedLength");
        discard.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("consumedLength");
        invalid.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("consumedLength");
    }

    [Fact]
    public void FrameDecodeResult_InvalidWithoutAReason_Throws()
    {
        // An invalid frame becomes an exception message, so it has to say something.
        var act = () => FrameDecodeResult.Invalid(4, "");

        act.Should().Throw<ArgumentException>().WithParameterName("reason");
    }

    // =========================================================================
    // Decoder results that do not fit the buffer
    // =========================================================================

    [Fact]
    public async Task Receive_DecoderConsumesPastBufferEnd_ThrowsAndLeavesThePipeIntact()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var act = async () => await sut.Receive(
            Frame.OverSpan((received, _, _, payload) =>
            {
                if (received.Length < 3) return FrameDecodeResult.NeedMoreData;
                payload.Write(received);
                return FrameDecodeResult.Frame(99);
            }),
            Token());

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("decoder");

        // Nothing was consumed, so the same bytes are still there for the next receive.
        var recovered = await sut.Receive(Frame.Exactly(3), Token());
        recovered.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task Receive_DecoderDiscardsPastBufferEnd_ThrowsAndLeavesThePipeIntact()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var act = async () => await sut.Receive(
            Frame.OverSpan((received, _, _, _) => received.Length >= 3
                ? FrameDecodeResult.Discard(10)
                : FrameDecodeResult.NeedMoreData),
            Token());

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("decoder");

        var recovered = await sut.Receive(Frame.Exactly(3), Token());
        recovered.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task Receive_DecoderConsumesTheWholeBuffer_IsAccepted()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        pipe.EnqueueRx(0x01, 0x02, 0x03);

        // Consuming exactly to buffer.End is the boundary, not an overrun.
        var result = await sut.Receive(
            Frame.OverSpan((received, _, _, payload) =>
            {
                if (received.Length < 3) return FrameDecodeResult.NeedMoreData;
                payload.Write(received.Slice(0, 1));
                return FrameDecodeResult.Frame(3);
            }),
            Token());

        result.ToArray().Should().Equal(0x01);
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

        var result = await sut.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(100)), Token());

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

        var result = await sut.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(200)), Token());

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

        var result = await sut.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(100)), Token());

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

        var result = await sut.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(200)), Token());

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
        var act = async () => await sut.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(50)), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReceiveUntilIdle_CancellationTokenTakesPriority()
    {
        var pipe = new FakeDuplexPipe();
        var sut = pipe.AsTransceiver();

        using var cts = new CancellationTokenSource(100);
        var act = async () => await sut.Receive(Frame.UntilIdle(TimeSpan.FromSeconds(30)), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
