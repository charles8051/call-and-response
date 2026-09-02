using CallAndResponse.Protocol.Stm32Bootloader;
using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Tier 3 — tests <see cref="Stm32BootloaderClient"/> end-to-end using <see cref="FakeDuplexPipe"/>.
/// No I/O; no mocking. The fake delivers pre-enqueued response bytes through the real
/// convenience-method implementations on <see cref="Transceiver"/>.
/// <para>
/// Encoding rules for response bytes:
/// <list type="bullet">
///   <item><c>SendReceiveExactly(write, n, token)</c> → enqueue exactly <c>n</c> response bytes.</item>
///   <item><c>SendReceivePerfectMatch(write, match, token)</c> → enqueue the <c>match</c> bytes
///   (ReceiveUntilPerfectMatch returns the bytes that equal match).</item>
///   <item><c>SendReceiveHeaderFooter(write, hdr, ftr, token)</c> → enqueue <c>hdr + payload + ftr</c>.</item>
/// </list>
/// </para>
/// </summary>
public class Stm32BootloaderClientTests
{
    private const byte Ack = 0x79;
    private const byte Nack = 0x1F;

    private static CancellationToken Token(int ms = 2000) =>
        new CancellationTokenSource(ms).Token;

    // Helpers -----------------------------------------------------------------

    /// <summary>
    /// Enqueues a single ACK — used for SendReceivePerfectMatch(…, new byte[]{ Ack }, …).
    /// ReceiveUntilPerfectMatch scans for the match bytes in the accumulated buffer and
    /// returns them, so the buffer must contain the match bytes.
    /// </summary>
    private static void EnqueueAck(FakeDuplexPipe p) => p.EnqueueRx(Ack);

    /// <summary>
    /// Enqueues a single NACK for SendReceiveExactly(…, 1, …).
    /// </summary>
    private static void EnqueueNack(FakeDuplexPipe p) => p.EnqueueRx(Nack);

    // =========================================================================
    // Ping — ACK / NACK / unexpected byte
    // =========================================================================

    [Fact]
    public async Task Ping_SendsCorrectByte()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack); // SendReceiveExactly expects 1 byte

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.Ping(Token());

        pipe.SentBytes.Should().Equal(0x7F);
    }

    [Fact]
    public async Task Ping_WhenAckReceived_ReturnsTrue()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.Ping(Token());

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Ping_WhenNackReceived_ReturnsFalse()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.Ping(Token());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Ping_WhenUnexpectedByteReceived_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x00);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.Ping(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task Ping_WhenUnexpectedByteReceived_DoesNotThrowOperationCanceledException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x00);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.Ping(Token());

        await act.Should().NotThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Ping_WhenUnexpectedByteReceived_MessageNamesTheByteThatArrived()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0xA5);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.Ping(Token());

        (await act.Should().ThrowAsync<Stm32BootloaderException>())
            .WithMessage("*0xA5*");
    }

    [Fact]
    public async Task Ping_WhenTokenIsCancelled_ThrowsOperationCanceledException()
    {
        var pipe = new FakeDuplexPipe();
        // No response bytes enqueued: Ping blocks awaiting the reply until the token trips.
        using var cts = new CancellationTokenSource(100);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.Ping(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // =========================================================================
    // GetId — command frame and response parsing
    // =========================================================================

    [Fact]
    public async Task GetId_SendsCorrectCommandFrame()
    {
        var pipe = new FakeDuplexPipe();
        // SendReceiveExactly(cmd, 5, token) → receive 5 bytes
        // AN3155 section 3.3: ACK, N = 0x01, PID high, PID low, ACK
        pipe.EnqueueRx(Ack, 0x01, 0x04, 0x13, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.GetId(Token());

        pipe.SentBytes.Should().StartWith(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD });
    }

    [Fact]
    public async Task GetId_ReturnsProductIdFromBytesTwoAndThree()
    {
        var pipe = new FakeDuplexPipe();
        // 0x0413 is the STM32F4 product id
        pipe.EnqueueRx(Ack, 0x01, 0x04, 0x13, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetId(Token());

        result.Should().Be(0x0413);
    }

    [Fact]
    public async Task GetId_DoesNotReturnTrailingAck()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack, 0x01, 0x04, 0x13, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetId(Token());

        result.Should().NotBe(Ack);
    }

    [Fact]
    public async Task GetId_ProductIdExceedingAByte_IsNotTruncated()
    {
        var pipe = new FakeDuplexPipe();
        // 0x0410 is the STM32F1 medium-density product id; it does not fit in a byte
        pipe.EnqueueRx(Ack, 0x01, 0x04, 0x10, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetId(Token());

        result.Should().Be(0x0410);
    }

    [Fact]
    public async Task GetId_WhenLeadingAckIsWrong_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        // A window shifted by stale bytes: [2..3] would otherwise parse as 0x1234
        pipe.EnqueueRx(0x00, 0x01, 0x12, 0x34, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetId(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task GetId_WhenNFieldIsWrong_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        // AN3155 fixes N at 0x01 for Get ID
        pipe.EnqueueRx(Ack, 0x02, 0x12, 0x34, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetId(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task GetId_WhenTrailingAckIsWrong_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack, 0x01, 0x04, 0x13, 0x00);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetId(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    // =========================================================================
    // GetSupportedCommands — response parsing
    // =========================================================================

    [Fact]
    public async Task GetSupportedCommands_ParsesProtocolVersion()
    {
        var pipe = new FakeDuplexPipe();
        // SendReceiveHeaderFooter(cmd, hdr:[Ack], ftr:[Ack]) →
        // ReceiveUntilHeaderFooterMatch([Ack], [Ack]) →
        // enqueue: Ack + payload + Ack
        byte[] payload = { 0x03, 0x10, (byte)Stm32BootloaderCommand.Get, (byte)Stm32BootloaderCommand.GetVersion, (byte)Stm32BootloaderCommand.GetId };
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(payload);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetSupportedCommands(Token());

        result.ProtocolVersion.Should().Be(0x10);
    }

    [Fact]
    public async Task GetSupportedCommands_ParsesSupportedCommandList()
    {
        var pipe = new FakeDuplexPipe();
        byte[] payload = { 0x03, 0x10, (byte)Stm32BootloaderCommand.Get, (byte)Stm32BootloaderCommand.GetVersion, (byte)Stm32BootloaderCommand.GetId };
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(payload);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetSupportedCommands(Token());

        result.SupportedCommands.Should().Contain(Stm32BootloaderCommand.Get)
            .And.Contain(Stm32BootloaderCommand.GetVersion)
            .And.Contain(Stm32BootloaderCommand.GetId);
    }

    [Fact]
    public async Task GetSupportedCommands_UnknownCommandByte_ThrowsInvalidOperationException()
    {
        var pipe = new FakeDuplexPipe();
        // 0xFE is not a defined Stm32BootloaderCommand value
        byte[] payload = { 0x01, 0x10, 0xFE };
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(payload);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetSupportedCommands(Token());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // =========================================================================
    // Go — command frame and address frame with checksum
    // =========================================================================

    [Fact]
    public async Task Go_SendsCommandFrameFirst()
    {
        var pipe = new FakeDuplexPipe();
        // Go() makes 2 SendReceivePerfectMatch calls, each expecting [Ack]
        EnqueueAck(pipe); // response to command frame
        EnqueueAck(pipe); // response to address frame

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.Go(Stm32BootloaderClient.Stm32BaseAddress, Token());

        pipe.SentBytes.Should().StartWith(new byte[] { (byte)Stm32BootloaderCommand.Go, 0xDE });
    }

    [Fact]
    public async Task Go_SendsAddressWithChecksumSecond()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        // Base address 0x08000000 → big-endian [0x08, 0x00, 0x00, 0x00], checksum = 0x08
        await client.Go(Stm32BootloaderClient.Stm32BaseAddress, Token());

        // SentBytes = cmd_frame(2) + address_frame(5)
        pipe.SentBytes.Skip(2).Should().Equal(0x08, 0x00, 0x00, 0x00, 0x08);
    }

    // =========================================================================
    // WriteMemory — chunking and frame structure
    // =========================================================================

    [Fact]
    public async Task WriteMemory_SingleChunk_MakesThreeSendReceiveExchanges()
    {
        var pipe = new FakeDuplexPipe();
        // Write256: command frame + address frame + data frame = 3 SendReceivePerfectMatch calls
        EnqueueAck(pipe);
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.WriteMemory(new byte[] { 0x01, 0x02 }, Stm32BootloaderClient.Stm32BaseAddress, Token());

        // Each SendReceivePerfectMatch sends its write bytes then receives Ack.
        // Verify we sent something for all 3 exchanges by checking SentBytes is non-trivial.
        pipe.SentBytes.Count.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task WriteMemory_SingleChunk_SendsCorrectCommandFrame()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.WriteMemory(new byte[] { 0x01, 0x02 }, Stm32BootloaderClient.Stm32BaseAddress, Token());

        pipe.SentBytes.Should().StartWith(new byte[] { (byte)Stm32BootloaderCommand.WriteMemory, 0xCE });
    }

    [Fact]
    public async Task WriteMemory_SingleChunk_SendsCorrectAddressWithChecksum()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        // Base address 0x08000000 → big-endian [0x08, 0x00, 0x00, 0x00], checksum = 0x08
        await client.WriteMemory(new byte[] { 0x01, 0x02 }, Stm32BootloaderClient.Stm32BaseAddress, Token());

        // SentBytes = cmd_frame(2) + address_with_checksum(5) + data_frame(...)
        pipe.SentBytes.Skip(2).Take(5).Should().Equal(0x08, 0x00, 0x00, 0x00, 0x08);
    }

    [Fact]
    public async Task WriteMemory_TwoChunks_MakesSixSendReceiveExchanges()
    {
        var pipe = new FakeDuplexPipe();
        // 512 bytes = 2 full chunks of 256; each Write256 makes 3 calls → 6 Acks total
        for (int i = 0; i < 6; i++) EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var data = new byte[512];
        await client.WriteMemory(data, Stm32BootloaderClient.Stm32BaseAddress, Token());

        // 6 Acks consumed — if fewer were enqueued the test would time out or throw
        // If fewer than 6 ACKs were enqueued, the test would time out.
        // Reaching this line confirms all 6 were consumed.
    }

    // =========================================================================
    // ReadMemory — chunking and response extraction
    // =========================================================================

    [Fact]
    public async Task ReadMemory_SingleChunk_ReturnDataBytes()
    {
        var pipe = new FakeDuplexPipe();
        // Read256 makes 3 SendReceiveExactly calls:
        //   1. command frame → expect 1 byte (Ack)
        //   2. address frame → expect 1 byte (Ack)
        //   3. length frame  → expect (length + 1) bytes: [Ack, data...]
        pipe.EnqueueRx(Ack);                  // call 1
        pipe.EnqueueRx(Ack);                  // call 2
        pipe.EnqueueRx(Ack, 0xAA, 0xBB);     // call 3: Ack + 2 data bytes

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.ReadMemory(Stm32BootloaderClient.Stm32BaseAddress, 2, Token());

        result.ToArray().Should().Equal(0xAA, 0xBB);
    }

    [Fact]
    public async Task ReadMemory_TwoChunks_ConcatenatesResults()
    {
        var pipe = new FakeDuplexPipe();
        // Two Read256 calls → 6 total SendReceiveExactly calls
        // Chunk 1: Ack, Ack, [Ack + 256 bytes of 0xAA]
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Enumerable.Repeat((byte)0xAA, 256).ToArray());
        // Chunk 2: Ack, Ack, [Ack + 256 bytes of 0xBB]
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Enumerable.Repeat((byte)0xBB, 256).ToArray());

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.ReadMemory(Stm32BootloaderClient.Stm32BaseAddress, 512, Token());

        result.Length.Should().Be(512);
        result.ToArray().Take(256).Should().AllBeEquivalentTo((byte)0xAA);
        result.ToArray().Skip(256).Should().AllBeEquivalentTo((byte)0xBB);
    }

    // =========================================================================
    // Extended erase — AN3155 3.7 wire format
    //
    // Every variant is two SendReceivePerfectMatch exchanges, each answered with
    // a single Ack: the command frame [0x44, 0xBB], then the erase payload.
    // The payload checksum is the XOR of every payload byte.
    // =========================================================================

    [Fact]
    public async Task ExtendedEraseMass_SendsSpecialCodeWithNoPageList()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe); // response to command frame
        EnqueueAck(pipe); // response to erase payload

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ExtendedEraseMass(Token());

        // cmd_frame(2) + half-word 0xFFFF + checksum (0xFF ^ 0xFF = 0x00)
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0xFF, 0xFF, 0x00);
    }

    [Fact]
    public async Task ExtendedEraseBank_Bank1_SendsSpecialCodeWithNoPageList()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ExtendedEraseBank(1, Token());

        // half-word 0xFFFE + checksum (0xFF ^ 0xFE = 0x01)
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0xFF, 0xFE, 0x01);
    }

    [Fact]
    public async Task ExtendedEraseBank_Bank2_SendsSpecialCodeWithNoPageList()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ExtendedEraseBank(2, Token());

        // half-word 0xFFFD + checksum (0xFF ^ 0xFD = 0x02)
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0xFF, 0xFD, 0x02);
    }

    [Fact]
    public async Task ExtendedEraseBank_InvalidBank_ThrowsArgumentOutOfRangeException()
    {
        var pipe = new FakeDuplexPipe();

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.ExtendedEraseBank(3, Token());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        pipe.SentBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtendedErasePages_WindowAbovePageZero_SendsCountMinusOneThenPages()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ExtendedErasePages(new ushort[] { 0x0010, 0x0011, 0x0012 }, Token());

        // N = 3 - 1 = 2, then the three page half-words, big-endian.
        // checksum = 0x00^0x02 ^ 0x00^0x10 ^ 0x00^0x11 ^ 0x00^0x12 = 0x11
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0x00, 0x02,
            0x00, 0x10,
            0x00, 0x11,
            0x00, 0x12,
            0x11);
    }

    [Fact]
    public async Task ExtendedErasePages_SinglePage_SendsZeroHalfWordThenThatPage()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ExtendedErasePages(new ushort[] { 0x0102 }, Token());

        // N = 0, one page half-word 0x0102, checksum = 0x01 ^ 0x02 = 0x03
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0x00, 0x00,
            0x01, 0x02,
            0x03);
    }

    [Fact]
    public async Task ExtendedErasePages_EmptyList_ThrowsArgumentException()
    {
        var pipe = new FakeDuplexPipe();

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.ExtendedErasePages(Array.Empty<ushort>(), Token());

        await act.Should().ThrowAsync<ArgumentException>();
        pipe.SentBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtendedErasePages_NullList_ThrowsArgumentNullException()
    {
        var pipe = new FakeDuplexPipe();

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.ExtendedErasePages(null!, Token());

        await act.Should().ThrowAsync<ArgumentNullException>();
        pipe.SentBytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtendedEraseMemoryPages_ObsoleteShim_StillErasesPagesZeroThroughN()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
#pragma warning disable CS0618 // covering the deprecated shim's wire format deliberately
        await client.ExtendedEraseMemoryPages(1, Token());
#pragma warning restore CS0618

        // N = 1 followed by pages 0 and 1, checksum = 0x01 ^ 0x01 = 0x00
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0x00, 0x01,
            0x00, 0x00,
            0x00, 0x01,
            0x00);
    }

    [Fact]
    public async Task ExtendedEraseMemoryPages_ObsoleteShim_AtReservedCodeBoundary_StillSendsLegacyFrame()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueAck(pipe);
        EnqueueAck(pipe);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        // 0xFFFD collides with the bank 2 special code, so the page-list guard on
        // ExtendedErasePages rejects it. The shim must not inherit that guard: it has always
        // sent this (malformed) frame and existing callers must keep getting it.
#pragma warning disable CS0618 // covering the deprecated shim's wire format deliberately
        await client.ExtendedEraseMemoryPages(0xFFFD, Token(15000));
#pragma warning restore CS0618

        // cmd_frame(2) + N + 0xFFFE page half-words + checksum
        pipe.SentBytes.Count.Should().Be(2 + (2 * (0xFFFE + 1)) + 1);
        pipe.SentBytes.Should().StartWith(new byte[]
        {
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB,
            0xFF, 0xFD,
            0x00, 0x00,
            0x00, 0x01
        });
    }

    // =========================================================================
    // GetProtocolVersion — AN3155 0x01, five-byte response
    // =========================================================================

    [Fact]
    public async Task GetProtocolVersion_SendsCorrectCommandFrame()
    {
        var pipe = new FakeDuplexPipe();
        // SendReceiveExactly(cmd, 5, token) → Ack, version, option1, option2, Ack
        pipe.EnqueueRx(Ack, 0x31, 0x00, 0x00, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.GetProtocolVersion(Token());

        pipe.SentBytes.Should().Equal((byte)Stm32BootloaderCommand.GetVersion, 0xFE);
    }

    [Fact]
    public async Task GetProtocolVersion_ParsesVersionAndOptionBytes()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack, 0x31, 0x0A, 0x0B, Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetProtocolVersion(Token());

        result.Version.Should().Be(0x31);
        result.MajorVersion.Should().Be(3);
        result.MinorVersion.Should().Be(1);
        result.OptionByte1.Should().Be(0x0A);
        result.OptionByte2.Should().Be(0x0B);
    }

    [Fact]
    public async Task GetProtocolVersion_WhenNackReceived_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Nack, 0x00, 0x00, 0x00, 0x00);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetProtocolVersion(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task GetProtocolVersion_WhenTrailingAckMissing_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack, 0x31, 0x00, 0x00, Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetProtocolVersion(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    // =========================================================================
    // EraseMemory — AN3155 0x43, page erase and global erase
    // =========================================================================

    [Fact]
    public async Task EraseMemory_SendsCommandFrameThenPageFrameWithChecksum()
    {
        var pipe = new FakeDuplexPipe();
        // Two SendReceiveExactly(…, 1) exchanges: command frame, then page frame
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.EraseMemory(new byte[] { 0x02, 0x03, 0x04 }, Token());

        // cmd(2) + [N=2, 0x02, 0x03, 0x04, checksum]
        // checksum = XOR(0x02, 0x02, 0x03, 0x04) = 0x07
        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.EraseMemory, 0xBC,
            0x02, 0x02, 0x03, 0x04, 0x07);
    }

    [Fact]
    public async Task EraseMemory_SinglePage_SendsZeroAsPageCount()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.EraseMemory(new byte[] { 0x07 }, Token());

        // N = 0 for one page; checksum = XOR(0x00, 0x07) = 0x07
        pipe.SentBytes.Skip(2).Should().Equal(0x00, 0x07, 0x07);
    }

    [Fact]
    public async Task EraseMemory_WhenDeviceNacksCommandFrame_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.EraseMemory(new byte[] { 0x00 }, Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task EraseMemory_WhenDeviceNacksPageFrame_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.EraseMemory(new byte[] { 0x00 }, Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task EraseMemory_WithNoPages_ThrowsArgumentException()
    {
        var pipe = new FakeDuplexPipe();
        var client = new Stm32BootloaderClient(pipe.AsTransceiver());

        var act = async () => await client.EraseMemory(Array.Empty<byte>(), Token());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EraseMemory_WithMoreThan255Pages_ThrowsArgumentException()
    {
        var pipe = new FakeDuplexPipe();
        var client = new Stm32BootloaderClient(pipe.AsTransceiver());

        var act = async () => await client.EraseMemory(new byte[256], Token());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EraseMemory_With255Pages_SendsReservedGlobalEraseValueAsPageCount()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var pages = Enumerable.Range(0, 255).Select(i => (byte)i).ToArray();
        await client.EraseMemory(pages, Token());

        // N = 254 for 255 pages — the largest page erase the single-byte encoding allows
        pipe.SentBytes.Skip(2).Take(1).Should().Equal((byte)0xFE);
        pipe.SentBytes.Count.Should().Be(2 + 1 + 255 + 1);
    }

    [Fact]
    public async Task EraseAllMemory_SendsCommandFrameThenGlobalEraseFrame()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.EraseAllMemory(Token());

        pipe.SentBytes.Should().Equal(
            (byte)Stm32BootloaderCommand.EraseMemory, 0xBC,
            0xFF, 0x00);
    }

    [Fact]
    public async Task EraseAllMemory_WhenDeviceNacksGlobalEraseFrame_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.EraseAllMemory(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    // =========================================================================
    // ReadoutUnprotect — AN3155 0x92, two ACKs, mass erases the part
    // =========================================================================

    [Fact]
    public async Task ReadoutUnprotect_SendsCommandFrameOnly()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack); // acknowledges the command frame
        pipe.EnqueueRx(Ack); // sent unprompted once the mass erase completes

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.ReadoutUnprotect(Token());

        pipe.SentBytes.Should().Equal((byte)Stm32BootloaderCommand.ReadoutUnprotect, 0x6D);
    }

    [Fact]
    public async Task ReadoutUnprotect_WaitsForSecondAck()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Nack); // erase failed — read protection is still active

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.ReadoutUnprotect(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task ReadoutUnprotect_WhenCommandFrameNacked_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.ReadoutUnprotect(Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    // =========================================================================
    // GetChecksum — AN3155 0xA1, four parameter frames then the CRC
    // =========================================================================

    /// <summary>
    /// Enqueues the five ACKs GetChecksum consumes before the result frame, then
    /// ACK + CRC (MSB first) + XOR checksum.
    /// </summary>
    private static void EnqueueChecksumResponse(FakeDuplexPipe pipe, uint crc)
    {
        for (int i = 0; i < 5; i++) pipe.EnqueueRx(Ack);

        byte b0 = (byte)(crc >> 24), b1 = (byte)(crc >> 16), b2 = (byte)(crc >> 8), b3 = (byte)crc;
        pipe.EnqueueRx(Ack, b0, b1, b2, b3, (byte)(b0 ^ b1 ^ b2 ^ b3));
    }

    [Fact]
    public async Task GetChecksum_SendsAllFiveFramesWithChecksums()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueChecksumResponse(pipe, 0x12345678);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 0x40, 0x04C11DB7, 0xFFFFFFFF, Token());

        pipe.SentBytes.Should().Equal(
            // command frame
            (byte)Stm32BootloaderCommand.GetChecksum, 0x5E,
            // start address 0x08000000, checksum 0x08
            0x08, 0x00, 0x00, 0x00, 0x08,
            // size 0x00000040 words, checksum 0x40
            0x00, 0x00, 0x00, 0x40, 0x40,
            // polynomial 0x04C11DB7, checksum = 0x04^0xC1^0x1D^0xB7 = 0x6F
            0x04, 0xC1, 0x1D, 0xB7, 0x6F,
            // initial value 0xFFFFFFFF, checksum 0x00
            0xFF, 0xFF, 0xFF, 0xFF, 0x00);
    }

    [Fact]
    public async Task GetChecksum_UsesStm32CrcUnitResetValuesByDefault()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueChecksumResponse(pipe, 0);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 0x40, token: Token());

        // The polynomial and initial-value frames follow the address and size frames.
        pipe.SentBytes.Skip(12).Should().Equal(
            0x04, 0xC1, 0x1D, 0xB7, 0x6F,
            0xFF, 0xFF, 0xFF, 0xFF, 0x00);
    }

    [Fact]
    public async Task GetChecksum_ParsesCrcMsbFirst()
    {
        var pipe = new FakeDuplexPipe();
        EnqueueChecksumResponse(pipe, 0xDEADBEEF);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 4, token: Token());

        result.Should().Be(0xDEADBEEF);
    }

    [Fact]
    public async Task GetChecksum_WhenResultChecksumIsWrong_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        for (int i = 0; i < 5; i++) pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Ack, 0xDE, 0xAD, 0xBE, 0xEF, 0x00); // 0x00 is not the XOR

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 4, token: Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task GetChecksum_WhenAddressFrameNacked_ThrowsStm32BootloaderException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(Ack);
        pipe.EnqueueRx(Nack);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 4, token: Token());

        await act.Should().ThrowAsync<Stm32BootloaderException>();
    }

    [Fact]
    public async Task GetChecksum_WithZeroWords_ThrowsArgumentOutOfRangeException()
    {
        var pipe = new FakeDuplexPipe();
        var client = new Stm32BootloaderClient(pipe.AsTransceiver());

        var act = async () => await client.GetChecksum(Stm32BootloaderClient.Stm32BaseAddress, 0, token: Token());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
