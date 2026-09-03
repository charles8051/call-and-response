using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>What to do with a frame that does not decode.</summary>
    public enum InvalidFrameAction
    {
        /// <summary>Consume the frame and throw a <see cref="FramingException"/>.</summary>
        Throw,

        /// <summary>Consume the frame and keep reading, as if it had never arrived.</summary>
        Discard,
    }

    /// <summary>
    /// SLIP framing, RFC 1055. Frames are delimited by <c>0xC0</c>, and the payload is escaped so a
    /// delimiter cannot occur inside one.
    /// </summary>
    /// <remarks>
    /// SLIP has no checksum, no length field, and no error detection of any kind. After a
    /// desynchronisation, noise between two delimiters decodes into a payload that looks valid and
    /// is returned as one. Use <see cref="HdlcCodec"/>, or carry a checksum at the protocol level,
    /// if you need to know that a frame arrived intact.
    /// <para>
    /// An empty payload does not survive the round trip. Encoded, it is two delimiters, which is
    /// indistinguishable from the inter-frame fill RFC 1055 requires receivers to discard.
    /// </para>
    /// </remarks>
    public sealed class SlipCodec : IFrameCodec
    {
        /// <summary>Frame delimiter.</summary>
        public const byte End = 0xC0;

        /// <summary>Escape prefix.</summary>
        public const byte Esc = 0xDB;

        /// <summary>Follows <see cref="Esc"/> to mean a literal <see cref="End"/>.</summary>
        public const byte EscEnd = 0xDC;

        /// <summary>Follows <see cref="Esc"/> to mean a literal <see cref="Esc"/>.</summary>
        public const byte EscEsc = 0xDD;

        /// <summary>RFC 1055 states hosts should be able to receive 1006-byte datagrams.</summary>
        public const int DefaultMaxFrameLength = 1006;

        /// <summary>
        /// Whether to emit a delimiter before each frame as well as after it. RFC 1055 recommends
        /// it: line noise preceding a frame then forms its own empty frame and is discarded, rather
        /// than being prepended to a real payload.
        /// </summary>
        public bool EmitLeadingEnd { get; init; } = true;

        /// <summary>
        /// What to do with an illegal escape sequence. Defaults to throwing. RFC 1055 permits the
        /// lenient reading where the escape is dropped and the octet passed through; this codec does
        /// not offer it, because handing a caller silently altered bytes is worse than either
        /// failing or dropping the frame.
        /// </summary>
        public InvalidFrameAction OnInvalidEscape { get; init; } = InvalidFrameAction.Throw;

        /// <summary>
        /// Longest encoded frame body accepted. Bounds how far the decoder accumulates when the
        /// peer never sends a delimiter.
        /// </summary>
        public int MaxFrameLength { get; init; } = DefaultMaxFrameLength;

        /// <inheritdoc />
        public TimeSpan? IdleTimeout => null;

        /// <inheritdoc />
        public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            // Worst case is every byte escaping, plus both delimiters.
            var span = destination.GetSpan(payload.Length * 2 + 2);
            int written = 0;

            if (EmitLeadingEnd) span[written++] = End;

            foreach (byte b in payload)
            {
                switch (b)
                {
                    case End:
                        span[written++] = Esc;
                        span[written++] = EscEnd;
                        break;
                    case Esc:
                        span[written++] = Esc;
                        span[written++] = EscEsc;
                        break;
                    default:
                        span[written++] = b;
                        break;
                }
            }

            span[written++] = End;
            destination.Advance(written);
        }

        /// <inheritdoc />
        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            var received = context.Received;

            // Leading delimiters are frame openers, empty frames, or noise flushed by the sender's
            // leading END. All three are skipped the same way.
            long bodyStart = CountLeading(received, End);
            if (bodyStart >= received.Length)
            {
                // Nothing but delimiters so far. Drop them rather than rescanning them forever.
                return bodyStart > 0
                    ? FrameDecodeResult.Discard((int)bodyStart)
                    : FrameDecodeResult.NeedMoreData;
            }

            long endIndex = Frame.IndexOf(received, stackalloc byte[] { End }, bodyStart);
            if (endIndex < 0)
            {
                return received.Length - bodyStart > MaxFrameLength
                    ? FrameDecodeResult.Invalid(
                        (int)received.Length,
                        $"No SLIP delimiter within {MaxFrameLength} bytes ({received.Length - bodyStart} accumulated).")
                    : FrameDecodeResult.NeedMoreData;
            }

            long bodyLength = endIndex - bodyStart;
            int consumed = (int)(endIndex + 1);

            if (bodyLength > MaxFrameLength)
            {
                return Reject(consumed, $"SLIP frame of {bodyLength} bytes exceeds the {MaxFrameLength}-byte maximum.");
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)bodyLength);
            try
            {
                var body = rented.AsSpan(0, (int)bodyLength);
                received.Slice(bodyStart, bodyLength).CopyTo(body);

                if (!TryUnescape(body, payload, out string? reason))
                {
                    return Reject(consumed, reason!);
                }

                return FrameDecodeResult.Frame(consumed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private FrameDecodeResult Reject(int consumed, string reason)
            => OnInvalidEscape == InvalidFrameAction.Discard
                ? FrameDecodeResult.Discard(consumed)
                : FrameDecodeResult.Invalid(consumed, reason);

        private static bool TryUnescape(ReadOnlySpan<byte> body, IBufferWriter<byte> payload, out string? reason)
        {
            var span = payload.GetSpan(body.Length);
            int written = 0;

            for (int i = 0; i < body.Length; i++)
            {
                byte b = body[i];
                if (b != Esc)
                {
                    span[written++] = b;
                    continue;
                }

                if (++i == body.Length)
                {
                    reason = "SLIP frame ends with an escape and no escaped byte.";
                    return false;
                }

                switch (body[i])
                {
                    case EscEnd: span[written++] = End; break;
                    case EscEsc: span[written++] = Esc; break;
                    default:
                        reason = $"SLIP escape followed by 0x{body[i]:X2}, which is neither ESC_END nor ESC_ESC.";
                        return false;
                }
            }

            payload.Advance(written);
            reason = null;
            return true;
        }

        internal static long CountLeading(in ReadOnlySequence<byte> source, byte value)
        {
            var reader = new SequenceReader<byte>(source);
            reader.AdvancePast(value);
            return reader.Consumed;
        }
    }
}
