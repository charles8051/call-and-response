using System.IO.Pipelines;

namespace CallAndResponse.Test.Unit.Helpers;

/// <summary>
/// An in-memory <see cref="IDuplexPipe"/> for unit testing.
/// Pre-load bytes via <see cref="EnqueueRx"/>; they are delivered through the pipe
/// to the real <see cref="Transceiver.ReceiveMessage"/> accumulation loop.
/// Bytes written via <see cref="IDuplexPipe.Output"/> are captured in
/// <see cref="SentBytes"/> for assertion.
/// </summary>
internal sealed class FakeDuplexPipe : IDuplexPipe
{
    private readonly Pipe _rxPipe = new();
    private readonly MemoryStream _txStream = new();

    public PipeReader Input => _rxPipe.Reader;
    public PipeWriter Output { get; }

    public IReadOnlyList<byte> SentBytes => _txStream.ToArray();

    public FakeDuplexPipe()
    {
        Output = PipeWriter.Create(_txStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Enqueue bytes that will be delivered to the accumulation loop.</summary>
    public void EnqueueRx(params byte[] bytes)
    {
        _rxPipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(bytes))
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Close the receive side, as a transport does when the link drops.</summary>
    public void CompleteRx(Exception? failure = null) => _rxPipe.Writer.Complete(failure);
}
