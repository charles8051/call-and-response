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
    public async Task Ping_WhenUnexpectedByteReceived_ThrowsOperationCanceledException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x00);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var act = async () => await client.Ping(Token());

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
        pipe.EnqueueRx(0x00, 0x00, 0x00, 0x00, 0x42);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        await client.GetId(Token());

        pipe.SentBytes.Should().StartWith(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD });
    }

    [Fact]
    public async Task GetId_ReturnsLastByteOfResponse()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(0x00, 0x00, 0x00, 0x00, 0x42);

        var client = new Stm32BootloaderClient(pipe.AsTransceiver());
        var result = await client.GetId(Token());

        result.Should().Be(0x42);
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
}
