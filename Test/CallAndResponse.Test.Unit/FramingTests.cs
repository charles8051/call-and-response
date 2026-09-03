using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Tier 2 — the framing codecs and the decoder contract they rely on. No I/O and no mocking:
/// bytes go through the real <see cref="Transceiver"/> receive loop over a fake pipe.
/// </summary>
public class FramingTests
{
    private static CancellationToken Token(int ms = 2000) => new CancellationTokenSource(ms).Token;

    private static byte[] Encode(IFrameCodec codec, params byte[] payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Encode(payload, writer);
        return writer.WrittenMemory.ToArray();
    }

    // =========================================================================
    // SLIP — round trip
    // =========================================================================

    public static TheoryData<byte[]> RoundTripPayloads => new()
    {
        new byte[] { 0x01 },
        new byte[] { 0xC0, 0xC0, 0xC0 },                 // all delimiters
        new byte[] { 0xDB, 0xDB, 0xDB },                 // all escapes
        new byte[] { 0xC0, 0xDB, 0xDC, 0xDD, 0x00, 0xFF },
    };

    [Theory]
    [MemberData(nameof(RoundTripPayloads))]
    public async Task Slip_RoundTrip_ReturnsTheOriginalPayload(byte[] payload)
    {
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Encode(codec, payload));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task Slip_EmptyPayload_IsNotDeliverable()
    {
        // An encoded empty SLIP frame is two delimiters, which is also what inter-frame fill
        // looks like. RFC 1055 says to discard empty frames, so an empty payload cannot survive
        // the round trip — a limit of the framing, not of this codec.
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Encode(codec, Array.Empty<byte>()));
        pipe.EnqueueRx(Encode(codec, 0x07));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x07);
    }

    [Theory]
    [MemberData(nameof(RoundTripPayloads))]
    [InlineData(new byte[0])]
    public async Task Hdlc_RoundTrip_ReturnsTheOriginalPayload(byte[] payload)
    {
        var codec = new HdlcCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Encode(codec, payload));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Slip_Encode_EscapesDelimiterAndEscapeOnly()
    {
        var encoded = Encode(new SlipCodec(), 0x01, 0xC0, 0xDB, 0x02);

        encoded.Should().Equal(0xC0, 0x01, 0xDB, 0xDC, 0xDB, 0xDD, 0x02, 0xC0);
    }

    [Fact]
    public void Slip_EmitLeadingEndDisabled_WritesOneDelimiterOnly()
    {
        var encoded = Encode(new SlipCodec { EmitLeadingEnd = false }, 0x01, 0x02);

        encoded.Should().Equal(0x01, 0x02, 0xC0);
    }

    // =========================================================================
    // SLIP — boundaries
    // =========================================================================

    [Fact]
    public async Task Slip_LeadingDelimitersAndEmptyFrames_AreSkipped()
    {
        var pipe = new FakeDuplexPipe();
        // Line noise flushed by a leading END, then an empty frame, then the real one.
        pipe.EnqueueRx(0xC0, 0xC0, 0xC0, 0x41, 0x42, 0xC0);

        var result = await pipe.AsTransceiver().Receive(new SlipCodec(), Token());

        result.ToArray().Should().Equal(0x41, 0x42);
    }

    [Fact]
    public async Task Slip_TwoFramesBackToBack_DecodeIndependently()
    {
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Encode(codec, 0x01, 0x02));
        pipe.EnqueueRx(Encode(codec, 0x03, 0x04));

        var sut = pipe.AsTransceiver();
        var first = await sut.Receive(codec, Token());
        var second = await sut.Receive(codec, Token());

        first.ToArray().Should().Equal(0x01, 0x02);
        second.ToArray().Should().Equal(0x03, 0x04);
    }

    [Fact]
    public async Task Hdlc_FramesSharingOneFlag_BothDecode()
    {
        // RFC 1662 lets one flag close a frame and open the next. The closing flag is consumed,
        // so the second frame arrives with no opener and must still be framed.
        var codec = new HdlcCodec();
        var first = Encode(codec, 0x01, 0x02);
        var second = Encode(codec, 0x03, 0x04);

        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(first);
        pipe.EnqueueRx(second.AsSpan(1).ToArray());   // drop the second frame's opening flag

        var sut = pipe.AsTransceiver();
        var a = await sut.Receive(codec, Token());
        var b = await sut.Receive(codec, Token());

        a.ToArray().Should().Equal(0x01, 0x02);
        b.ToArray().Should().Equal(0x03, 0x04);
    }

    // =========================================================================
    // SLIP — rejections
    // =========================================================================

    [Fact]
    public async Task Slip_EscapeAtEndOfFrame_Throws()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x41, 0xDB, 0xC0);

        var act = async () => await pipe.AsTransceiver().Receive(new SlipCodec(), Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("*escape*");
    }

    [Fact]
    public async Task Slip_UnknownEscapeByte_Throws()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x41, 0xDB, 0x00, 0xC0);

        var act = async () => await pipe.AsTransceiver().Receive(new SlipCodec(), Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("*neither ESC_END nor ESC_ESC*");
    }

    [Fact]
    public async Task Slip_InvalidEscapeWithDiscardPolicy_SkipsTheFrameAndReturnsTheNextOne()
    {
        var codec = new SlipCodec { OnInvalidEscape = InvalidFrameAction.Discard };
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x41, 0xDB, 0x00, 0xC0);
        pipe.EnqueueRx(Encode(codec, 0x07));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x07);
    }

    [Fact]
    public async Task Slip_OverLengthFrame_ThrowsAndLeavesTheLinkUsable()
    {
        var codec = new SlipCodec { MaxFrameLength = 4 };
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06);

        var sut = pipe.AsTransceiver();
        var act = async () => await sut.Receive(codec, Token());
        await act.Should().ThrowAsync<FramingException>();

        // The point of consuming an invalid frame: the link still works afterwards.
        pipe.EnqueueRx(Encode(codec, 0x09));
        var recovered = await sut.Receive(codec, Token());
        recovered.ToArray().Should().Equal(0x09);
    }

    // =========================================================================
    // HDLC — integrity and the ACCM
    // =========================================================================

    [Fact]
    public async Task Hdlc_FcsMismatch_Throws()
    {
        var codec = new HdlcCodec();
        var frame = Encode(codec, 0x01, 0x02, 0x03);
        frame[2] ^= 0xFF;   // corrupt a payload octet, leaving the FCS describing the original

        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(frame);

        var act = async () => await pipe.AsTransceiver().Receive(codec, Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("*frame check sequence*");
    }

    [Fact]
    public async Task Hdlc_FcsMismatchWithDiscardPolicy_SkipsTheFrame()
    {
        var codec = new HdlcCodec(new HdlcOptions { OnFcsMismatch = InvalidFrameAction.Discard });
        var corrupt = Encode(codec, 0x01, 0x02, 0x03);
        corrupt[2] ^= 0xFF;

        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(corrupt);
        pipe.EnqueueRx(Encode(codec, 0x09));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x09);
    }

    [Fact]
    public void Hdlc_DefaultAccm_EscapesControlOctets()
    {
        // 0x11 is XON. Under the default ACCM every octet below 0x20 is escaped.
        var encoded = Encode(new HdlcCodec(), 0x11);

        encoded.Should().Contain(HdlcCodec.ControlEscape);
        encoded.Should().Contain((byte)(0x11 ^ HdlcCodec.EscapeXor));
        encoded.Should().NotContain((byte)0x11);
    }

    [Fact]
    public void Hdlc_ClearedSendAccm_LeavesControlOctetsUnescaped()
    {
        var encoded = Encode(new HdlcCodec(new HdlcOptions { SendAccm = 0 }), 0x11);

        encoded.Should().HaveCount(5, "flag, the unescaped octet, two FCS octets, flag");
        encoded[0].Should().Be(HdlcCodec.Flag);
        encoded[1].Should().Be(0x11);
        encoded[4].Should().Be(HdlcCodec.Flag);
    }

    [Fact]
    public void Hdlc_Fcs_MatchesThePublishedCrc16X25CheckValue()
    {
        // The FCS is CRC-16/X-25, whose published check value over the ASCII digits "123456789"
        // is 0x906E. Asserting against that rather than against a round trip is the difference
        // between "the codec agrees with itself" and "the codec agrees with the RFC".
        var payload = System.Text.Encoding.ASCII.GetBytes("123456789");
        var encoded = Encode(new HdlcCodec(new HdlcOptions { SendAccm = 0 }), payload);

        // Frame layout: flag, payload, FCS low, FCS high, flag. None of these octets escape.
        var fcs = (ushort)(encoded[^3] | (encoded[^2] << 8));

        fcs.Should().Be(0x906E);
    }

    [Fact]
    public async Task Hdlc_UnescapedFlaggedControlOctet_IsDiscardedOnReceive()
    {
        // RFC 1662: an unescaped control octet the receive ACCM flags was inserted by the link,
        // not sent by the peer, so it must not reach the payload — or the FCS.
        var codec = new HdlcCodec();
        var frame = Encode(codec, 0x41, 0x42).ToList();
        frame.Insert(2, 0x11);

        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(frame.ToArray());

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x41, 0x42);
    }

    [Fact]
    public async Task Hdlc_AddressAndControl_AreAddedOnSendAndStrippedOnReceive()
    {
        var codec = new HdlcCodec(new HdlcOptions
        {
            AddressAndControl = new byte[] { 0xFF, 0x03 },
            SendAccm = 0,
            ReceiveAccm = 0,
        });

        var encoded = Encode(codec, 0x41);
        encoded.AsSpan(1, 2).ToArray().Should().Equal(0xFF, 0x03);

        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(encoded);
        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x41);
    }

    [Fact]
    public async Task Hdlc_FrameTooShortForAnFcs_IsDiscardedAsInterFrameFill()
    {
        var codec = new HdlcCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x7E, 0x41, 0x7E);          // one octet between flags: not a frame
        pipe.EnqueueRx(Encode(codec, 0x09));

        var result = await pipe.AsTransceiver().Receive(codec, Token());

        result.ToArray().Should().Equal(0x09);
    }

    // =========================================================================
    // The decoder contract
    // =========================================================================

    [Fact]
    public async Task Receive_DecoderThrows_DoesNotWedgeTheLink()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var sut = pipe.AsTransceiver();
        var act = async () => await sut.Receive(
            Frame.OverSpan((_, _, _, _) => throw new InvalidOperationException("boom")), Token());

        await act.Should().ThrowAsync<InvalidOperationException>();

        // A decoder that throws leaves the read unadvanced unless the loop advances for it, and
        // an unadvanced PipeReader refuses every later read.
        var recovered = await sut.Receive(Frame.Exactly(3), Token());
        recovered.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task Receive_DecoderWritesThenAsksForMoreData_DoesNotDuplicateThePayload()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02);
        pipe.EnqueueRx(0x03, 0x04);

        // Badly behaved on purpose: it writes on every call, including the ones that ask for
        // more data. No correct decoder does this, which is exactly why it needs a test.
        var decoder = Frame.OverSpan((received, _, _, payload) =>
        {
            payload.Write(received);
            return received.Length < 4 ? FrameDecodeResult.NeedMoreData : FrameDecodeResult.Frame(4);
        });

        var result = await pipe.AsTransceiver().Receive(decoder, Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public async Task Receive_DecoderWritesThenDiscards_DoesNotLeakThoseBytes()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04);

        var discardedFirst = false;
        var decoder = Frame.OverSpan((received, _, _, payload) =>
        {
            payload.Write(received);
            if (!discardedFirst)
            {
                discardedFirst = true;
                return FrameDecodeResult.Discard(2);
            }

            return FrameDecodeResult.Frame(received.Length);
        });

        var result = await pipe.AsTransceiver().Receive(decoder, Token());

        result.ToArray().Should().Equal(0x03, 0x04);
    }

    [Fact]
    public async Task Receive_DecoderRejectsFrame_ThrowsFramingExceptionCarryingTheReason()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02);

        var act = async () => await pipe.AsTransceiver().Receive(
            Frame.OverSpan((received, _, _, _) => FrameDecodeResult.Invalid(received.Length, "bad magic")),
            Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("bad magic");
    }

    [Fact]
    public async Task Receive_DecoderStillWantsDataAtEndOfStream_ThrowsNamingTheUnframedBytes()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02);
        pipe.CompleteRx();

        var act = async () => await pipe.AsTransceiver().Receive(Frame.Exactly(5), Token());

        await act.Should().ThrowAsync<TransceiverTransportException>().WithMessage("*2 byte(s) left unframed*");
    }

    [Fact]
    public async Task Receive_UntilTransportComplete_GetsAFinalLookAtTheBufferedBytes()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);
        pipe.CompleteRx();

        var result = await pipe.AsTransceiver().Receive(Frame.UntilTransportComplete(), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task Receive_ZeroLengthDiscard_IsRejectedRatherThanLoopingForever()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01);

        var act = async () => await pipe.AsTransceiver().Receive(
            Frame.OverSpan((_, _, _, _) => FrameDecodeResult.Discard(0)), Token());

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("decoder");
    }

    // =========================================================================
    // Combinators
    // =========================================================================

    [Fact]
    public async Task WithMaxLength_NoFrameWithinTheBound_Throws()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03, 0x04, 0x05);

        var act = async () => await pipe.AsTransceiver()
            .Receive(Frame.UntilTerminator(0xFF).WithMaxLength(3), Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("*within 3 bytes*");
    }

    [Fact]
    public async Task WithIdleTimeout_InnerDecoderCannotFinish_FailsRatherThanWaiting()
    {
        // Arming the timer is not enough on its own: the content decoders ignore IsIdle and would
        // wait for a terminator that is never coming. The gap is a deadline, so it fails here —
        // returning the three buffered bytes would be inventing a frame out of a partial one.
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var act = async () => await pipe.AsTransceiver().Receive(
            Frame.UntilTerminator(0x0A).WithIdleTimeout(TimeSpan.FromMilliseconds(30)), Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("*3 byte(s) buffered*");
    }

    [Fact]
    public async Task WithIdleTimeout_InnerDecoderCanFinishAtEndOfInput_ReturnsItsFrame()
    {
        // A decoder that knows how to finish on its final bytes gets to, because the gap is
        // presented to it the same way the transport closing would be.
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var result = await pipe.AsTransceiver().Receive(
            Frame.UntilTransportComplete().WithIdleTimeout(TimeSpan.FromMilliseconds(30)), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task WithIdleTimeout_DoesNotHandBackUndecodedWireBytes()
    {
        // The failure this guards: completing from the buffer on idle would return the escaped
        // frame, delimiters and all, in place of the payload the codec would have produced.
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x41, 0xDB, 0xDC);   // an unterminated SLIP frame

        var act = async () => await pipe.AsTransceiver().Receive(
            codec.WithIdleTimeout(TimeSpan.FromMilliseconds(30)), Token());

        await act.Should().ThrowAsync<FramingException>();
    }

    [Fact]
    public async Task WithIdleTimeout_ValidationStillRunsAtTheGap()
    {
        // Whatever the deadline does, it must not route around Validated.
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var decoder = Frame.UntilTransportComplete()
            .Validated((ReadOnlySpan<byte> payload, out string? reason) =>
            {
                reason = "checksum";
                return false;
            })
            .WithIdleTimeout(TimeSpan.FromMilliseconds(30));

        var act = async () => await pipe.AsTransceiver().Receive(decoder, Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("checksum");
    }

    [Fact]
    public async Task WithIdleTimeout_TerminatorArrivesFirst_TheContentFramingStillWins()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x0A, 0x03);

        var sut = pipe.AsTransceiver();
        var framed = await sut.Receive(
            Frame.UntilTerminator(0x0A).WithIdleTimeout(TimeSpan.FromSeconds(5)), Token());
        var remainder = await sut.Receive(Frame.Exactly(1), Token());

        framed.ToArray().Should().Equal(0x01, 0x02);
        remainder.ToArray().Should().Equal(0x03);
    }

    [Fact]
    public async Task WithIdleTimeout_NothingArrives_KeepsWaitingRatherThanReturningEmpty()
    {
        var pipe = new FakeDuplexPipe();

        var act = async () => await pipe.AsTransceiver().Receive(
            Frame.UntilTerminator(0x0A).WithIdleTimeout(TimeSpan.FromMilliseconds(20)), Token(300));

        // Silence before the first byte is the wait for the device to answer, not a frame boundary.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AsByteStream_UnderlyingTransportFails_TheRealCauseReachesTheCaller()
    {
        var pipe = new FakeDuplexPipe();
        pipe.CompleteRx(new IOException("the adapter was unplugged"));

        var stream = pipe.AsTransceiver().WithFraming(new SlipCodec()).AsByteStream();
        var act = async () => await stream.Receive(Frame.Exactly(4), Token());

        // A pipe completed with a failure surfaces that failure, so the adapter must not mistake
        // it for the end of the data and swallow it.
        await act.Should().ThrowAsync<IOException>().WithMessage("the adapter was unplugged");
    }

    [Fact]
    public async Task AsByteStream_TransportClosesMidMessage_KeepsTheUnderlyingReasonAsTheInnerException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xC0, 0x01, 0x02);   // a SLIP frame with no closing delimiter
        pipe.CompleteRx();

        var stream = pipe.AsTransceiver().WithFraming(new SlipCodec()).AsByteStream();
        var act = async () => await stream.Receive(Frame.Exactly(4), Token());

        // Two different truncations — the message and the byte-level read — and the caller should
        // be able to see both rather than only the outer one.
        var thrown = (await act.Should().ThrowAsync<TransceiverTransportException>()).Which;
        thrown.InnerException.Should().BeOfType<TransceiverTransportException>();
    }

    [Fact]
    public async Task Validated_RejectedPayload_ThrowsAndDoesNotReachTheCaller()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x01, 0x02, 0x03);

        var decoder = Frame.Exactly(3).Validated((ReadOnlySpan<byte> payload, out string? reason) =>
        {
            reason = "checksum";
            return payload[0] == 0xFF;
        });

        var act = async () => await pipe.AsTransceiver().Receive(decoder, Token());

        await act.Should().ThrowAsync<FramingException>().WithMessage("checksum");
    }

    [Fact]
    public async Task LengthPrefixed_SizesTheFrameFromThePrefixField()
    {
        var pipe = new FakeDuplexPipe();
        // Prefix says 3 bytes follow; the 0xFF is the start of whatever comes next.
        pipe.EnqueueRx(0x03, 0x0A, 0x0B, 0x0C, 0xFF);

        var sut = pipe.AsTransceiver();
        var frame = await sut.Receive(Frame.LengthPrefixed(prefixOffset: 0, prefixSize: 1), Token());
        var next = await sut.Receive(Frame.Exactly(1), Token());

        frame.ToArray().Should().Equal(0x03, 0x0A, 0x0B, 0x0C);
        next.ToArray().Should().Equal(0xFF);
    }

    [Fact]
    public async Task LengthPrefixed_PayloadOffsetAndTrailer_TrimTheFrameToThePayload()
    {
        var pipe = new FakeDuplexPipe();
        // ACK, N = 2, version, two command bytes, ACK — the AN3155 Get reply shape.
        pipe.EnqueueRx(0x79, 0x02, 0x31, 0x00, 0x01, 0x79);

        var result = await pipe.AsTransceiver().Receive(
            Frame.LengthPrefixed(prefixOffset: 1, prefixSize: 1, lengthAdjustment: 2, payloadOffset: 2, trailerLength: 1),
            Token());

        result.ToArray().Should().Equal(0x31, 0x00, 0x01);
    }

    // =========================================================================
    // Message channel and the byte-stream adapter
    // =========================================================================

    [Fact]
    public async Task WithFraming_SendMessage_PutsAnEncodedFrameOnTheWire()
    {
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();

        await pipe.AsTransceiver().WithFraming(codec).SendMessage(new byte[] { 0xC0 }, Token());

        pipe.SentBytes.Should().Equal(0xC0, 0xDB, 0xDC, 0xC0);
    }

    [Fact]
    public async Task AsByteStream_ReadSpanningTwoMessages_IsSatisfiedByConcatenation()
    {
        // Documented behaviour, not desirable behaviour: asking a stream question of a message
        // link loses the boundary. The test exists so a later change cannot quietly alter it.
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Encode(codec, 0x01, 0x02));
        pipe.EnqueueRx(Encode(codec, 0x03, 0x04));

        var stream = pipe.AsTransceiver().WithFraming(codec).AsByteStream();
        var result = await stream.Receive(Frame.Exactly(3), Token());

        result.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task AsByteStream_TwoSends_ProduceTwoMessages()
    {
        var codec = new SlipCodec();
        var pipe = new FakeDuplexPipe();
        var stream = pipe.AsTransceiver().WithFraming(codec).AsByteStream();

        await stream.Send(new byte[] { 0x01 }, Token());
        await stream.Send(new byte[] { 0x02 }, Token());

        pipe.SentBytes.Should().Equal(0xC0, 0x01, 0xC0, 0xC0, 0x02, 0xC0);
    }

    [Fact]
    public async Task AsByteStream_IdleFramedDecoder_IsRejected()
    {
        var stream = new FakeDuplexPipe().AsTransceiver().WithFraming(new SlipCodec()).AsByteStream();

        var act = async () => await stream.Receive(Frame.UntilIdle(TimeSpan.FromMilliseconds(10)), Token());

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("decoder");
    }
}
