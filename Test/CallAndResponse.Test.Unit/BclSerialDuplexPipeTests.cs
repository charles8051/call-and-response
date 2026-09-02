using CallAndResponse.Test.Unit.Helpers;
using CallAndResponse.Transport.Serial;
using FluentAssertions;
using System.Buffers;
using Step = CallAndResponse.Test.Unit.Helpers.FakeSyncSerialStream.Step;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Exercises <see cref="BclSerialDuplexPipe"/>'s synchronous read pump against a
/// <see cref="FakeSyncSerialStream"/>. The subject is almost entirely the exception
/// classifier: the BCL backend's loop tick arrives as an exception, so the pump has to tell
/// a tick apart from a dead port without hardware to ask.
/// </summary>
public class BclSerialDuplexPipeTests
{
    /// <summary><c>ERROR_TIMEOUT</c> (1460), the HResult .NET 7 gives a timed-out read.</summary>
    private const int ErrorTimeoutHResult = unchecked((int)0x800705B4);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private static CancellationToken Token(int ms = 5000) =>
        new CancellationTokenSource(ms).Token;

    private static async Task<byte[]> ReadOnce(BclSerialDuplexPipe pipe)
    {
        var read = await pipe.Input.ReadAsync(Token());
        var bytes = read.Buffer.ToArray();
        pipe.Input.AdvanceTo(read.Buffer.End);
        return bytes;
    }

    [Fact]
    public async Task ReadPump_TimeoutException_IsALoopTickAndNotAFailure()
    {
        // .NET 6 and earlier, and .NET 8 if the dotnet/runtime#80079 fix landed.
        var stream = new FakeSyncSerialStream(
            Step.Throw(new TimeoutException("The operation has timed out.")),
            Step.Throw(new TimeoutException("The operation has timed out.")),
            Step.Data(0x01, 0x02));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        // Reaching the bytes at all proves the two timeouts neither faulted the pipe
        // nor stopped the pump.
        (await ReadOnce(pipe)).Should().Equal(0x01, 0x02);
    }

    [Fact]
    public async Task ReadPump_IOExceptionCarryingErrorTimeout_IsALoopTickAndNotAFailure()
    {
        // .NET 7 turned a timed-out read into this. Same meaning, different type.
        var stream = new FakeSyncSerialStream(
            Step.Throw(new IOException("The operation has timed out.", ErrorTimeoutHResult)),
            Step.Data(0x03));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        (await ReadOnce(pipe)).Should().Equal(0x03);
    }

    [Fact]
    public async Task ReadPump_IOExceptionWithAnyOtherHResult_FaultsTheReader()
    {
        // The dangerous direction. A classifier widened to a bare IOException would
        // swallow this, spin forever on a dead port, and hang the consumer.
        var failure = new IOException("The device is not connected.", unchecked((int)0x8007048F));
        var stream = new FakeSyncSerialStream(Step.Throw(failure));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        var act = async () => await pipe.Input.ReadAsync(Token());

        (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadPump_PortFailsMidSession_PropagatesOriginalExceptionToReader()
    {
        var failure = new UnauthorizedAccessException("Access to the port is denied.");
        var stream = new FakeSyncSerialStream(Step.Data(0x01, 0x02, 0x03));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        // The bytes that arrived before the failure still come through. Injected rather
        // than scripted, so the failure cannot overtake this read.
        (await ReadOnce(pipe)).Should().Equal(0x01, 0x02, 0x03);

        stream.Fail(failure);

        var act = async () => await pipe.Input.ReadAsync(Token());

        (await act.Should().ThrowAsync<UnauthorizedAccessException>())
            .Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadPump_OperationCanceled_IsAFailureBecauseTheReadNeverCarriesOurToken()
    {
        // The synchronous read is never handed the pump's token, so cancellation cannot
        // originate from our shutdown. Anything claiming to be cancelled came from
        // elsewhere and is a failure, unlike the RJCP pump where it is the shutdown path.
        var failure = new OperationCanceledException("The read was aborted by the driver.");
        var stream = new FakeSyncSerialStream(Step.Throw(failure));

        await using var pipe = new BclSerialDuplexPipe(stream, Tick);

        var act = async () => await pipe.Input.ReadAsync(Token());

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task Dispose_OnAnIdlePort_CompletesCleanlyWithinTheJoinBudget()
    {
        var stream = new FakeSyncSerialStream(Tick);
        var pipe = new BclSerialDuplexPipe(stream, Tick);

        // Let the pump get into its tick loop rather than catching it before the first read.
        while (stream.ReadCount < 2) await Task.Delay(5, Token());

        var dispose = async () => await pipe.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        var read = await pipe.Input.ReadAsync(Token());

        read.IsCompleted.Should().BeTrue();
        read.Buffer.Length.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_StopsThePump()
    {
        var stream = new FakeSyncSerialStream(Tick);
        var pipe = new BclSerialDuplexPipe(stream, Tick);

        while (stream.ReadCount < 2) await Task.Delay(5, Token());
        await pipe.DisposeAsync();

        var afterDispose = stream.ReadCount;
        await Task.Delay(Tick + Tick + Tick, Token());

        // At most the one read that was already in flight when the token tripped.
        stream.ReadCount.Should().BeLessThanOrEqualTo(afterDispose + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]      // SerialPort.InfiniteTimeout
    [InlineData(-250)]
    public void Constructor_RejectsANonPositiveReadTick(int milliseconds)
    {
        using var port = new System.IO.Ports.SerialPort("COM255");

        var act = () => new BclSerialDuplexPipe(port, TimeSpan.FromMilliseconds(milliseconds));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("readTick");
    }

    [Fact]
    public void Constructor_RejectsANullPort()
    {
        var act = () => new BclSerialDuplexPipe(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("serialPort");
    }
}
