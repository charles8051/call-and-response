using CallAndResponse.Test.Unit.Helpers;
using CallAndResponse.Transport.Serial;
using FluentAssertions;
using System.Buffers;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Exercises <see cref="SerialDuplexPipe"/>'s background read pump against a
/// <see cref="FakeSerialStream"/>. No hardware, no mocking — the pump is driven by a
/// stream that fails on demand.
/// </summary>
public class SerialDuplexPipeTests
{
    private static CancellationToken Token(int ms = 2000) =>
        new CancellationTokenSource(ms).Token;

    [Fact]
    public async Task ReadPump_PortFailsMidSession_PropagatesOriginalExceptionToReader()
    {
        var failure = new UnauthorizedAccessException("Access to the port is denied.");
        var stream = new FakeSerialStream(new byte[] { 0x01, 0x02, 0x03 });
        await using var pipe = new SerialDuplexPipe(stream);

        // The bytes that arrived before the failure still come through.
        var read = await pipe.Input.ReadAsync(Token());
        read.Buffer.ToArray().Should().Equal(0x01, 0x02, 0x03);
        pipe.Input.AdvanceTo(read.Buffer.End);

        stream.Fail(failure);

        var act = async () => await pipe.Input.ReadAsync(Token());

        (await act.Should().ThrowAsync<UnauthorizedAccessException>())
            .Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadPump_FirstReadThrows_PropagatesRatherThanLookingLikeEndOfStream()
    {
        var failure = new IOException("The device is not connected.");
        var stream = new FakeSerialStream();
        stream.Fail(failure);

        await using var pipe = new SerialDuplexPipe(stream);

        var act = async () => await pipe.Input.ReadAsync(Token());

        (await act.Should().ThrowAsync<IOException>())
            .Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadPump_Cancelled_CompletesCleanlyWithNoException()
    {
        var stream = new FakeSerialStream();
        var pipe = new SerialDuplexPipe(stream);

        // DisposeAsync cancels the pump and waits for it; a deliberate shutdown must
        // not surface as a failure on either side.
        var dispose = async () => await pipe.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        var read = await pipe.Input.ReadAsync(Token());

        read.IsCompleted.Should().BeTrue();
        read.Buffer.Length.Should().Be(0);
    }
}
