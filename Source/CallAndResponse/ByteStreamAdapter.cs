using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;

namespace CallAndResponse
{
    /// <summary>
    /// Presents a message channel as a byte channel, for a protocol client written against
    /// <see cref="ITransceiver"/> that has to run over a self-delimiting link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adaptation is lossy in both directions, and deliberately visible rather than hidden.
    /// <b>Receive</b> concatenates messages: a decoder asking for more bytes than the current
    /// message holds is satisfied from the next one, so a read that spans two messages succeeds and
    /// the boundary between them is gone. <b>Send</b> emits exactly one message per call — a client
    /// that builds one logical frame from two <see cref="Send"/> calls produces two messages, and no
    /// adapter can know it meant one.
    /// </para>
    /// <para>
    /// Callers whose sends do not already align with message boundaries should use
    /// <see cref="IMessageTransceiver"/> directly instead.
    /// </para>
    /// </remarks>
    internal sealed class ByteStreamAdapter : ITransceiver
    {
        private readonly IMessageTransceiver _inner;
        private readonly SemaphoreSlim _receiveGate = new(1, 1);

        // Decoded bytes received but not yet handed to a caller. This is why receives are serialised:
        // two concurrent reads would interleave into it.
        private byte[] _buffered = Array.Empty<byte>();
        private int _bufferedCount;

        internal ByteStreamAdapter(IMessageTransceiver inner)
            => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        /// <inheritdoc />
        public Task Send(ReadOnlyMemory<byte> bytes, CancellationToken token)
            => _inner.SendMessage(bytes, token);

        /// <inheritdoc />
        public async Task<Memory<byte>> Receive(IFrameDecoder decoder, CancellationToken token)
        {
            var destination = new ArrayBufferWriter<byte>();
            await Receive(decoder, destination, token).ConfigureAwait(false);
            return destination.WrittenMemory.ToArray();
        }

        /// <inheritdoc />
        public async Task Receive(IFrameDecoder decoder, IBufferWriter<byte> destination, CancellationToken token)
        {
            if (decoder is null) throw new ArgumentNullException(nameof(decoder));
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            if (decoder.IdleTimeout is not null)
            {
                throw new ArgumentException(
                    "A message channel has no inter-byte gap to frame on, so a decoder with an idle timeout " +
                    "cannot run over one. Frame on content, or hold the underlying byte channel.",
                    nameof(decoder));
            }

            await _receiveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var staging = new ArrayBufferWriter<byte>();
                bool transportComplete = false;
                TransceiverTransportException? transportFailure = null;

                while (true)
                {
                    staging.Clear();
                    var buffer = new ReadOnlySequence<byte>(_buffered, 0, _bufferedCount);
                    var result = decoder.Decode(new FrameContext(buffer, false, transportComplete), staging);

                    switch (result.Status)
                    {
                        case FrameDecodeStatus.Frame:
                            RequireFits(result, _bufferedCount);
                            destination.Write(staging.WrittenSpan);
                            Consume(result.ConsumedLength);
                            return;

                        case FrameDecodeStatus.Discard:
                            RequireFits(result, _bufferedCount);
                            if (result.ConsumedLength == 0)
                            {
                                throw new ArgumentException(
                                    "The decoder discarded zero bytes, which would loop forever.",
                                    nameof(decoder));
                            }

                            Consume(result.ConsumedLength);
                            continue;

                        case FrameDecodeStatus.Invalid:
                            RequireFits(result, _bufferedCount);
                            Consume(result.ConsumedLength);
                            throw new FramingException(result.Reason ?? "The decoder rejected the frame.");

                        default:
                            if (transportComplete)
                            {
                                // The decoder has had its look at the final bytes and still wants
                                // more. Surface the original failure rather than a summary of it:
                                // a dead link and a clean close arrive as the same exception type
                                // here, and only the first one carries a cause worth reporting.
                                throw new TransceiverTransportException(
                                    $"Transport closed with {_bufferedCount} byte(s) left unframed.",
                                    transportFailure!);
                            }

                            try
                            {
                                var message = await _inner.ReceiveMessage(token).ConfigureAwait(false);
                                Append(message.Span);
                            }
                            catch (TransceiverTransportException e)
                            {
                                // Give the decoder its one look at the final bytes before failing,
                                // so a decoder that can complete at end of stream still can.
                                transportComplete = true;
                                transportFailure = e;
                            }

                            continue;
                    }
                }
            }
            finally
            {
                _receiveGate.Release();
            }
        }

        private static void RequireFits(in FrameDecodeResult result, int bufferedCount)
        {
            if (result.ConsumedLength > bufferedCount)
            {
                throw new ArgumentException(
                    $"The decoder consumed {result.ConsumedLength} bytes from a {bufferedCount}-byte buffer.",
                    "decoder");
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;

            int required = _bufferedCount + bytes.Length;
            if (required > _buffered.Length)
            {
                Array.Resize(ref _buffered, Math.Max(required, _buffered.Length * 2));
            }

            bytes.CopyTo(_buffered.AsSpan(_bufferedCount));
            _bufferedCount = required;
        }

        private void Consume(int count)
        {
            int remaining = _bufferedCount - count;
            if (remaining > 0)
            {
                Array.Copy(_buffered, count, _buffered, 0, remaining);
            }

            _bufferedCount = remaining;
        }
    }
}
