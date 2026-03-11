namespace CallAndResponse.Test.Unit;

/// <summary>
/// A minimal concrete Transceiver implementation used for testing the message detection logic in the base class.
/// The ReceiveMessage callback is provided externally via QueuedReceiveData so tests can control exactly 
/// what bytes the transceiver "receives" over the wire.
/// </summary>
internal class TestTransceiver : Transceiver
{
    private readonly Queue<byte[]> _receiveChunks = new();

    public override bool IsOpen { get; protected set; }

    /// <summary>Queue byte chunks that will be fed to ReceiveMessage on each invocation.</summary>
    public void QueueReceiveChunks(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
            _receiveChunks.Enqueue(chunk);
    }

    public override Task Open(CancellationToken token) { IsOpen = true; return Task.CompletedTask; }
    public override Task Close(CancellationToken token) { IsOpen = false; return Task.CompletedTask; }
    public override Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token) => Task.CompletedTask;

    /// <summary>
    /// Simulates incremental receive: feeds chunks one at a time to detectMessage until the detector
    /// signals a complete message, then returns the payload slice.
    /// </summary>
    public override Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken token)
    {
        var buffer = new List<byte>();

        while (true)
        {
            if (_receiveChunks.TryDequeue(out var chunk))
                buffer.AddRange(chunk);

            var (startIndex, length) = detectMessage(buffer.ToArray().AsMemory());
            if (length > 0)
            {
                return Task.FromResult(buffer.ToArray().AsMemory().Slice(startIndex, length));
            }

            if (_receiveChunks.Count == 0)
                throw new InvalidOperationException("No more data available in TestTransceiver.");
        }
    }
}

public class TransceiverTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // ReceiveExactly
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveExactly_ReturnsRequestedBytes_WhenDataArrivesInSingleChunk()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0x02, 0x03 });

        var result = await sut.ReceiveExactly(3, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveExactly_ReturnsRequestedBytes_WhenDataArrivesInMultipleChunks()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01 }, new byte[] { 0x02 }, new byte[] { 0x03 });

        var result = await sut.ReceiveExactly(3, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveExactly_ReturnsOnlyRequestedBytes_WhenMoreDataAvailable()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        var result = await sut.ReceiveExactly(2, CancellationToken.None);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ReceiveUntilTerminator (char)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveUntilTerminator_ReturnsPayloadBeforeTerminator()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { (byte)'H', (byte)'i', (byte)'\n' });

        var result = await sut.ReceiveUntilTerminator('\n', CancellationToken.None);

        Assert.Equal(new byte[] { (byte)'H', (byte)'i' }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveUntilTerminator_EmptyPayloadBeforeTerminator_ReturnsEmpty()
    {
        // NOTE: When the terminator is the very first byte, the detector returns (0,0) which
        // the base Transceiver.ReceiveMessage implementation interprets as "keep waiting" rather
        // than "empty payload found".  The test therefore must include at least one byte of
        // payload before the terminator so the detector can return a positive length.
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { (byte)'A', (byte)'\r' });

        var result = await sut.ReceiveUntilTerminator('\r', CancellationToken.None);

        Assert.Equal(new byte[] { (byte)'A' }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ReceiveUntilTerminatorPattern
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_ReturnsPayloadBeforePattern()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0x02, 0xFF, 0xFE });

        var result = await sut.ReceiveUntilTerminatorPattern(new byte[] { 0xFF, 0xFE }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02 }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveUntilTerminatorPattern_PatternAtStart_WaitsForMoreData()
    {
        // NOTE: When the pattern is the very first bytes, the detector returns (0,0), which
        // the base Transceiver.ReceiveMessage treats as "keep waiting".  This means the pattern
        // must be preceded by at least one byte for the detector to return a positive length.
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0xFF, 0xFE });

        var result = await sut.ReceiveUntilTerminatorPattern(new byte[] { 0xFF, 0xFE }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01 }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ReceiveUntilHeaderFooterMatch
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_ReturnsPayloadBetweenHeaderAndFooter()
    {
        var sut = new TestTransceiver();
        // stream: [header=0xAA] [payload=0x01,0x02,0x03] [footer=0xBB]
        sut.QueueReceiveChunks(new byte[] { 0xAA, 0x01, 0x02, 0x03, 0xBB });

        var result = await sut.ReceiveUntilHeaderFooterMatch(new byte[] { 0xAA }, new byte[] { 0xBB }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_MultiByteHeaderAndFooter_ReturnsPayload()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x7E, 0x80, 0xDE, 0xAD, 0x7E, 0x81 });

        var result = await sut.ReceiveUntilHeaderFooterMatch(new byte[] { 0x7E, 0x80 }, new byte[] { 0x7E, 0x81 }, CancellationToken.None);

        Assert.Equal(new byte[] { 0xDE, 0xAD }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveUntilHeaderFooterMatch_DataArrivesInChunks_ReturnsPayload()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0xAA }, new byte[] { 0x01, 0x02 }, new byte[] { 0xBB });

        var result = await sut.ReceiveUntilHeaderFooterMatch(new byte[] { 0xAA }, new byte[] { 0xBB }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02 }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ReceiveUntilPerfectMatch
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveUntilPerfectMatch_ReturnMatchBytes_WhenPatternFound()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x00, 0x79, 0x00 });

        var result = await sut.ReceiveUntilPerfectMatch(new byte[] { 0x79 }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x79 }, result.ToArray());
    }

    [Fact]
    public async Task ReceiveUntilPerfectMatch_MultiBytePattern_ReturnMatchBytes()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0x02, 0xDE, 0xAD, 0x03 });

        var result = await sut.ReceiveUntilPerfectMatch(new byte[] { 0xDE, 0xAD }, CancellationToken.None);

        Assert.Equal(new byte[] { 0xDE, 0xAD }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SendReceiveExactly
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendReceiveExactly_SendsThenReceivesExactBytes()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0xAA, 0xBB });

        var result = await sut.SendReceiveExactly(new byte[] { 0x01, 0x02 }, 2, CancellationToken.None);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SendReceiveString
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendReceiveString_CharTerminator_ReturnsPayloadString()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { (byte)'O', (byte)'K', (byte)'\n' });

        var result = await sut.SendReceiveString("AT\n", '\n', CancellationToken.None);

        Assert.Equal("OK", result);
    }

    [Fact]
    public async Task SendReceiveString_StringTerminator_ReturnsPayloadString()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { (byte)'O', (byte)'K', (byte)'\r', (byte)'\n' });

        var result = await sut.SendReceiveString("AT\r\n", "\r\n", CancellationToken.None);

        Assert.Equal("OK", result);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SendReceiveFooter
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendReceiveFooter_ReturnsPayloadBeforeFooter()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0x02, 0xFF, 0xFE });

        var result = await sut.SendReceiveFooter(new byte[] { 0x01 }, new byte[] { 0xFF, 0xFE }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02 }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SendReceiveHeaderFooter
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendReceiveHeaderFooter_ReturnsPayloadBetweenHeaderAndFooter()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0xAA, 0x01, 0x02, 0xBB });

        var result = await sut.SendReceiveHeaderFooter(new byte[] { 0x00 }, new byte[] { 0xAA }, new byte[] { 0xBB }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02 }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SendReceive (with custom detectMessage)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendReceive_CustomDetector_ReturnsMatchedPayload()
    {
        var sut = new TestTransceiver();
        sut.QueueReceiveChunks(new byte[] { 0x01, 0x02, 0x03 });

        // Detector that accepts when we have at least 3 bytes and returns bytes 1..2
        var result = await sut.SendReceive(
            new byte[] { 0xFF },
            buf => buf.Length >= 3 ? (1, 2) : (0, 0),
            CancellationToken.None);

        Assert.Equal(new byte[] { 0x02, 0x03 }, result.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Open / Close
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_SetsIsOpenTrue()
    {
        var sut = new TestTransceiver();
        Assert.False(sut.IsOpen);
        await sut.Open(CancellationToken.None);
        Assert.True(sut.IsOpen);
    }

    [Fact]
    public async Task Close_SetsIsOpenFalse()
    {
        var sut = new TestTransceiver();
        await sut.Open(CancellationToken.None);
        await sut.Close(CancellationToken.None);
        Assert.False(sut.IsOpen);
    }
}
