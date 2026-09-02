using System.IO.Pipelines;

namespace CallAndResponse.Transport.Serial;

/// <summary>
/// What the read pump should do about an exception a read threw.
/// </summary>
/// <remarks>
/// The two serial backends disagree completely about this, which is why it is a
/// parameter of <see cref="SerialReadPump"/> rather than something the pump decides.
/// See ADR-0019 DEC-009a.
/// </remarks>
internal enum ReadDisposition
{
    /// <summary>
    /// Not an error. Keep reading. The <c>System.IO.Ports</c> backend's loop tick is an
    /// expired <c>ReadTimeout</c>, which arrives as an exception several times a second on
    /// an idle port. The RJCP backend has no benign exception at all.
    /// </summary>
    Benign,

    /// <summary>
    /// A deliberate shutdown. Stop and complete the pipe cleanly, the way an ordinary end
    /// of stream would.
    /// </summary>
    Shutdown,

    /// <summary>
    /// The port died under us. Stop and complete the pipe with this exception so the
    /// consumer sees the real cause.
    /// </summary>
    Failure,
}

/// <summary>
/// The read loop shared by both serial transports: pull bytes off a stream, write them into
/// a <see cref="Pipe"/>, and complete that pipe in a way that tells the consumer why it ended.
/// </summary>
/// <remarks>
/// Everything backend-specific is a parameter. The two implementations differ in how a read
/// is issued, in how an exception from that read is classified, and in how disposal joins the
/// pump — nothing else.
/// </remarks>
internal static class SerialReadPump
{
    private const int BufferSize = 512;

    /// <param name="writer">The pipe to fill. The pump completes it and nothing else does.</param>
    /// <param name="readAsync">
    /// Issues one read. The RJCP backend awaits <c>Stream.ReadAsync</c> with the token; the
    /// <c>System.IO.Ports</c> backend blocks in the synchronous <c>Read</c> and ignores the
    /// token, because on Windows the async path honours neither cancellation nor
    /// <c>ReadTimeout</c>.
    /// </param>
    /// <param name="classify">
    /// Decides what an exception from <paramref name="readAsync"/> means. Write it narrow:
    /// a predicate one clause too wide turns a dead port into an indefinite hang, because the
    /// pump keeps reading and the consumer never learns the port is gone. When in doubt return
    /// <see cref="ReadDisposition.Failure"/> and let the exception through.
    /// </param>
    /// <param name="token">Cancelled by disposal to ask the pump to stop.</param>
    internal static async Task RunAsync(
        PipeWriter writer,
        Func<byte[], CancellationToken, ValueTask<int>> readAsync,
        Func<Exception, ReadDisposition> classify,
        CancellationToken token)
    {
        var readBuffer = new byte[BufferSize];

        // Non-null once the port has failed. Handed to Complete so the reader sees the
        // real cause instead of an end of stream indistinguishable from a clean close.
        Exception? failure = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await readAsync(readBuffer, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var disposition = classify(ex);

                    // A benign exception is the loop tick, not an event. Nothing is
                    // written and nothing is recorded; go straight back to reading.
                    if (disposition == ReadDisposition.Benign) continue;

                    if (disposition == ReadDisposition.Failure) failure = ex;
                    break;
                }

                if (bytesRead == 0) break;

                readBuffer.AsSpan(0, bytesRead).CopyTo(writer.GetSpan(bytesRead));
                writer.Advance(bytesRead);

                var flush = await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        finally
        {
            writer.Complete(failure);
        }
    }
}
