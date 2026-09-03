using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>Byte order of a length prefix.</summary>
    public enum Endianness
    {
        /// <summary>Most significant byte first. The usual choice in embedded protocols.</summary>
        BigEndian,

        /// <summary>Least significant byte first.</summary>
        LittleEndian,
    }

    /// <summary>A caller-supplied decode function. See <see cref="IFrameDecoder.Decode"/> for the contract.</summary>
    public delegate FrameDecodeResult FrameDecodeCallback(in FrameContext context, IBufferWriter<byte> payload);

    /// <summary>
    /// A decode function written against a flattened span rather than a sequence. Easier to write,
    /// at the cost of a copy per call when the transport hands back segmented buffers.
    /// </summary>
    public delegate FrameDecodeResult SpanFrameDecodeCallback(
        ReadOnlySpan<byte> received, bool isIdle, bool isTransportComplete, IBufferWriter<byte> payload);

    /// <summary>Decides whether a decoded payload is acceptable. See <see cref="Frame.Validated"/>.</summary>
    public delegate bool FrameValidator(ReadOnlySpan<byte> payload, out string? reason);

    /// <summary>
    /// The built-in decoders, and the combinators that adapt them.
    /// </summary>
    /// <remarks>
    /// Every decoder here frames on content or on time and returns the payload verbatim. Framings
    /// that transform the payload — SLIP, RFC 1662 — are codecs rather than decoders, because they
    /// have a send half too; see <see cref="SlipCodec"/> and <see cref="HdlcCodec"/>.
    /// </remarks>
    public static class Frame
    {
        /// <summary>Exactly <paramref name="count"/> bytes.</summary>
        public static IFrameDecoder Exactly(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");
            return new ExactlyDecoder(count);
        }

        /// <summary>
        /// Everything up to the first <paramref name="terminator"/> byte. The terminator is consumed
        /// either way, and included in the payload only if <paramref name="keepInPayload"/> is set.
        /// </summary>
        public static IFrameDecoder UntilTerminator(byte terminator, bool keepInPayload = false)
            => new PatternDecoder(new[] { terminator }, keepInPayload);

        /// <summary>
        /// Everything up to the first occurrence of <paramref name="pattern"/>. The pattern is
        /// consumed either way, and included in the payload only if <paramref name="keepInPayload"/> is set.
        /// </summary>
        public static IFrameDecoder UntilPattern(ReadOnlyMemory<byte> pattern, bool keepInPayload = false)
        {
            if (pattern.IsEmpty) throw new ArgumentException("Pattern cannot be empty.", nameof(pattern));
            return new PatternDecoder(pattern.ToArray(), keepInPayload);
        }

        /// <summary>
        /// The bytes between <paramref name="header"/> and the first <paramref name="footer"/> that
        /// follows it. Anything before the header is consumed and dropped.
        /// </summary>
        public static IFrameDecoder Between(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer)
        {
            if (header.IsEmpty) throw new ArgumentException("Header cannot be empty.", nameof(header));
            if (footer.IsEmpty) throw new ArgumentException("Footer cannot be empty.", nameof(footer));
            return new BetweenDecoder(header.ToArray(), footer.ToArray());
        }

        /// <summary>
        /// Everything accumulated when the line has been silent for <paramref name="gap"/>. Frames on
        /// time rather than content, for protocols whose boundary is the inter-frame gap and for
        /// unsolicited bursts.
        /// </summary>
        /// <remarks>
        /// The gap is measured between bytes, so it starts once the first one arrives. Silence before
        /// that is the device thinking, not a boundary, and this waits through it indefinitely — bound
        /// that wait with the cancellation token.
        /// </remarks>
        public static IFrameDecoder UntilIdle(TimeSpan gap)
        {
            if (gap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gap), gap, "Idle gap must be positive.");
            return new UntilIdleDecoder(gap);
        }

        /// <summary>Everything received when the transport closes. Empty input yields an empty frame.</summary>
        public static IFrameDecoder UntilTransportComplete() => new UntilTransportCompleteDecoder();

        /// <summary>
        /// A frame whose length is carried in a prefix field.
        /// </summary>
        /// <param name="prefixOffset">Where the length field starts, from the head of the frame.</param>
        /// <param name="prefixSize">Width of the length field: 1, 2, or 4 bytes.</param>
        /// <param name="endianness">Byte order of the length field.</param>
        /// <param name="lengthAdjustment">
        /// Added to the decoded length to get the number of bytes that follow the field. Use it when
        /// the field counts something other than the remaining bytes.
        /// </param>
        /// <param name="payloadOffset">Where the payload starts, from the head of the frame.</param>
        /// <param name="trailerLength">Bytes after the payload that belong to the frame but not the payload.</param>
        public static IFrameDecoder LengthPrefixed(
            int prefixOffset,
            int prefixSize,
            Endianness endianness = Endianness.BigEndian,
            int lengthAdjustment = 0,
            int payloadOffset = 0,
            int trailerLength = 0)
        {
            if (prefixOffset < 0) throw new ArgumentOutOfRangeException(nameof(prefixOffset), prefixOffset, "Prefix offset cannot be negative.");
            if (prefixSize is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(prefixSize), prefixSize, "Prefix size must be 1, 2, or 4 bytes.");
            if (payloadOffset < 0) throw new ArgumentOutOfRangeException(nameof(payloadOffset), payloadOffset, "Payload offset cannot be negative.");
            if (trailerLength < 0) throw new ArgumentOutOfRangeException(nameof(trailerLength), trailerLength, "Trailer length cannot be negative.");

            return new LengthPrefixedDecoder(prefixOffset, prefixSize, endianness, lengthAdjustment, payloadOffset, trailerLength);
        }

        /// <summary>A decoder from a delegate, for framing no built-in covers.</summary>
        public static IFrameDecoder Custom(FrameDecodeCallback decode, TimeSpan? idleTimeout = null)
        {
            if (decode is null) throw new ArgumentNullException(nameof(decode));
            return new CustomDecoder(decode, idleTimeout);
        }

        /// <summary>
        /// A decoder from a delegate that reads a flattened span. Costs a copy per call when the
        /// received buffer is segmented; prefer <see cref="Custom"/> on a hot path.
        /// </summary>
        public static IFrameDecoder OverSpan(SpanFrameDecodeCallback decode, TimeSpan? idleTimeout = null)
        {
            if (decode is null) throw new ArgumentNullException(nameof(decode));
            return new SpanDecoder(decode, idleTimeout);
        }

        /// <summary>
        /// Stop waiting once a reply has stalled for <paramref name="gap"/>. On the gap,
        /// <paramref name="inner"/> is asked once more with the transport treated as complete: a
        /// decoder that can finish on its final bytes does, and one that cannot fails with a
        /// <see cref="FramingException"/> instead of waiting for data that is not coming.
        /// </summary>
        /// <remarks>
        /// Like <see cref="UntilIdle"/>, the gap is measured between bytes and so applies to a reply
        /// that started and stopped, not to one that never began. A device that says nothing at all is
        /// the cancellation token's business, not this decorator's.
        /// <para>
        /// This is a timeout, not a framing rule. It never invents a frame out of a partial one, and
        /// it never returns the buffered wire bytes in place of what the inner decoder would have
        /// produced — that would skip unescaping, checksums, and anything
        /// <see cref="Validated"/> wrapped around it. To frame on the gap itself, use
        /// <see cref="UntilIdle"/>, which treats silence as the boundary rather than as a deadline.
        /// </para>
        /// </remarks>
        public static IFrameDecoder WithIdleTimeout(this IFrameDecoder inner, TimeSpan gap)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            if (gap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gap), gap, "Idle gap must be positive.");
            return new IdleTimeoutDecorator(inner, gap);
        }

        /// <summary>
        /// Fail once the buffer passes <paramref name="maxFrameLength"/> without <paramref name="inner"/>
        /// finding a frame. Without this a peer that never sends a delimiter grows the buffer until
        /// memory runs out.
        /// </summary>
        public static IFrameDecoder WithMaxLength(this IFrameDecoder inner, int maxFrameLength)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            if (maxFrameLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameLength), maxFrameLength, "Maximum frame length must be positive.");
            return new MaxLengthDecorator(inner, maxFrameLength);
        }

        /// <summary>
        /// Check each decoded payload before it reaches the caller — a CRC, a length field, a magic
        /// byte. A payload the validator rejects becomes an <see cref="FrameDecodeStatus.Invalid"/>
        /// frame, whose bytes are consumed before the receive call throws.
        /// </summary>
        public static IFrameDecoder Validated(this IFrameDecoder inner, FrameValidator validate)
        {
            if (inner is null) throw new ArgumentNullException(nameof(inner));
            if (validate is null) throw new ArgumentNullException(nameof(validate));
            return new ValidatedDecorator(inner, validate);
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        internal static void CopyTo(in ReadOnlySequence<byte> source, long start, long length, IBufferWriter<byte> destination)
        {
            if (length <= 0) return;

            var slice = source.Slice(start, length);
            foreach (var segment in slice)
            {
                destination.Write(segment.Span);
            }
        }

        /// <summary>
        /// Index of the first occurrence of <paramref name="pattern"/>, or -1. Scans the sequence
        /// without flattening it, which matters because the receive loop re-scans on every read.
        /// </summary>
        internal static long IndexOf(in ReadOnlySequence<byte> source, ReadOnlySpan<byte> pattern, long startAt = 0)
        {
            if (pattern.IsEmpty || source.Length - startAt < pattern.Length) return -1;

            if (source.IsSingleSegment)
            {
                int found = source.FirstSpan.Slice((int)startAt).IndexOf(pattern);
                return found < 0 ? -1 : startAt + found;
            }

            var reader = new SequenceReader<byte>(source);
            reader.Advance(startAt);

            while (reader.TryReadTo(out ReadOnlySequence<byte> _, pattern[0], advancePastDelimiter: false))
            {
                if (reader.Remaining < pattern.Length) return -1;

                if (MatchesAt(source, reader.Consumed, pattern)) return reader.Consumed;

                reader.Advance(1);
            }

            return -1;
        }

        private static bool MatchesAt(in ReadOnlySequence<byte> source, long position, ReadOnlySpan<byte> pattern)
        {
            var candidate = source.Slice(position, pattern.Length);
            if (candidate.IsSingleSegment) return candidate.FirstSpan.SequenceEqual(pattern);

            int index = 0;
            foreach (var segment in candidate)
            {
                if (!segment.Span.SequenceEqual(pattern.Slice(index, segment.Length))) return false;
                index += segment.Length;
            }

            return true;
        }
    }
}
