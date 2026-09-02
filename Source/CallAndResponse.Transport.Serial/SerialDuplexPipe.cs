using System.IO.Pipelines;
using RJCP.IO.Ports;

namespace CallAndResponse.Transport.Serial;

/// <summary>
/// An <see cref="IDuplexPipe"/> backed by an already-open <see cref="SerialPortStream"/>.
/// The caller owns the serial port lifecycle (open/close/dispose).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="System.IO.Ports.SerialPort"/>, <see cref="SerialPortStream"/> is itself a
/// <see cref="System.IO.Stream"/> with its own internal I/O thread that buffers bytes out of the
/// kernel. Its <see cref="System.IO.Stream.ReadAsync"/> waits on that in-memory buffer, so
/// cancellation is reliable without any Win32 <c>CancelIoEx</c> involvement.
/// </para>
/// <para>
/// However, passing the stream directly to <see cref="PipeReader.Create"/> causes
/// <see cref="System.IO.Pipelines.StreamPipeReader"/> to forward its own internal cancellation
/// token into every <c>ReadAsync</c> call. RJCP calls
/// <c>cancellationToken.ThrowIfCancellationRequested()</c> after its internal wait returns,
/// which means the exception escapes rather than being translated into
/// <see cref="PipeReader.CancelPendingRead"/>&#8203;s <c>IsCanceled</c> flag. The result is
/// <see cref="OperationCanceledException"/> spam in the debugger on every idle timeout.
/// </para>
/// <para>
/// The fix is a background pump on a dedicated OS thread: because RJCP's <c>ReadAsync</c>
/// reliably honours cancellation, the pump can use <c>ReadAsync</c> directly without any
/// <c>ReadTimeout</c> or <c>BytesToRead</c> polling. Received bytes are written into an
/// internal <see cref="Pipe"/> whose <see cref="PipeReader"/> properly honours
/// <see cref="PipeReader.CancelPendingRead"/>.
/// </para>
/// </remarks>
public sealed class SerialDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly Pipe _rxPipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    /// <inheritdoc />
    public PipeReader Input => _rxPipe.Reader;

    /// <inheritdoc />
    public PipeWriter Output { get; }

    public SerialDuplexPipe(SerialPortStream serialPort)
        // The cast picks the Stream overload; without it this would call itself.
        : this((Stream)(serialPort ?? throw new ArgumentNullException(nameof(serialPort))))
    {
    }

    /// <summary>
    /// Test seam. The pump needs nothing beyond <see cref="Stream"/>, so the unit tests can
    /// drive it with a stream that fails on demand instead of with real hardware. The public
    /// surface stays pinned to <see cref="SerialPortStream"/>.
    /// </summary>
    internal SerialDuplexPipe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Output = PipeWriter.Create(stream);

        var token = _cts.Token;

        _pumpTask = Task.Factory.StartNew(
            () => SerialReadPump.RunAsync(
                    _rxPipe.Writer,
                    // RJCP's ReadAsync waits on an in-memory buffer and reliably
                    // honours the cancellation token without Win32 CancelIoEx.
                    (buffer, ct) => new ValueTask<int>(stream.ReadAsync(buffer, 0, buffer.Length, ct)),
                    ex => Classify(ex, token),
                    token)
                .GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// RJCP's read path has no benign exception, so every exception is either our own
    /// shutdown or a failure the consumer has to see.
    /// </summary>
    private static ReadDisposition Classify(Exception exception, CancellationToken token) =>
        // DisposeAsync asked the pump to stop. A deliberate shutdown is a clean
        // completion, not a failure. Matching on the exception's own token rather than on
        // token.IsCancellationRequested keeps a driver that cancels a read for its own
        // reasons from being mistaken for our shutdown when the two happen at once.
        exception is OperationCanceledException canceled && canceled.CancellationToken == token
            ? ReadDisposition.Shutdown
            // The port died under us: adapter unplugged, driver error, another process
            // taking the handle. Anything that is not our own cancellation has to reach
            // the consumer.
            : ReadDisposition.Failure;

    /// <summary>
    /// Signals the background pump to stop and waits for it to finish cleanly.
    /// Does not close or dispose the underlying <see cref="SerialPortStream"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _pumpTask.ConfigureAwait(false);
        _cts.Dispose();
    }
}
