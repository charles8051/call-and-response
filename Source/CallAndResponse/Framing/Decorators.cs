using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>
    /// Bounds how long a decoder may wait. See <see cref="Frame.WithIdleTimeout"/>.
    /// </summary>
    internal sealed class IdleTimeoutDecorator : IFrameDecoder
    {
        private readonly IFrameDecoder _inner;
        private readonly TimeSpan _gap;

        internal IdleTimeoutDecorator(IFrameDecoder inner, TimeSpan gap)
        {
            _inner = inner;
            _gap = gap;
        }

        public TimeSpan? IdleTimeout => _gap;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            // The first call is staged because a second may follow, and an inner decoder that
            // wrote before asking for more data would otherwise have its output counted twice.
            var staged = new ArrayBufferWriter<byte>();
            var result = _inner.Decode(context, staged);

            if (result.Status != FrameDecodeStatus.NeedMoreData)
            {
                if (result.Status == FrameDecodeStatus.Frame) payload.Write(staged.WrittenSpan);
                return result;
            }

            if (!context.IsIdle || context.Received.IsEmpty) return result;

            // Silence with bytes in hand means no more are coming, which is what
            // IsTransportComplete tells a decoder. Give the inner decoder that and let it decide:
            // returning the buffered wire bytes directly would bypass its unescaping, its checksum,
            // and anything Validated wrapped around it, handing the caller undecoded bytes that
            // look like a payload.
            var final = new FrameContext(context.Received, isIdle: true, isTransportComplete: true);
            result = _inner.Decode(final, payload);

            if (result.Status != FrameDecodeStatus.NeedMoreData) return result;

            // It cannot finish and nothing further will arrive. Say so rather than inventing a
            // frame out of a partial one.
            return FrameDecodeResult.Invalid(
                (int)context.Received.Length,
                $"No frame within {_gap.TotalMilliseconds:0.##}ms of silence ({context.Received.Length} byte(s) buffered).");
        }
    }

    /// <summary>Bounds how far a decoder may accumulate. See <see cref="Frame.WithMaxLength"/>.</summary>
    internal sealed class MaxLengthDecorator : IFrameDecoder
    {
        private readonly IFrameDecoder _inner;
        private readonly int _maxFrameLength;

        internal MaxLengthDecorator(IFrameDecoder inner, int maxFrameLength)
        {
            _inner = inner;
            _maxFrameLength = maxFrameLength;
        }

        public TimeSpan? IdleTimeout => _inner.IdleTimeout;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            var result = _inner.Decode(context, payload);

            // Only an inner decoder that still wants more data can run away. Anything else has
            // decided, and a frame that legitimately exceeds the bound is the inner decoder's call.
            if (result.Status != FrameDecodeStatus.NeedMoreData) return result;

            if (context.Received.Length > _maxFrameLength)
            {
                return FrameDecodeResult.Invalid(
                    (int)context.Received.Length,
                    $"No frame found within {_maxFrameLength} bytes ({context.Received.Length} accumulated).");
            }

            return result;
        }
    }

    /// <summary>Checks a decoded payload before it reaches the caller. See <see cref="Frame.Validated"/>.</summary>
    internal sealed class ValidatedDecorator : IFrameDecoder
    {
        private readonly IFrameDecoder _inner;
        private readonly FrameValidator _validate;

        internal ValidatedDecorator(IFrameDecoder inner, FrameValidator validate)
        {
            _inner = inner;
            _validate = validate;
        }

        public TimeSpan? IdleTimeout => _inner.IdleTimeout;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            // The verdict is only available after the inner decoder has written, so it writes to a
            // buffer of ours and we forward on success. Passing `payload` straight through would
            // leave a rejected payload in it, and IBufferWriter cannot take bytes back.
            var staged = new ArrayBufferWriter<byte>();
            var result = _inner.Decode(context, staged);

            if (result.Status != FrameDecodeStatus.Frame) return result;

            if (!_validate(staged.WrittenSpan, out string? reason))
            {
                return FrameDecodeResult.Invalid(result.ConsumedLength, reason ?? "Frame failed validation.");
            }

            payload.Write(staged.WrittenSpan);
            return result;
        }
    }
}
