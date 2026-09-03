using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>Adds an idle wake-up to a content decoder. See <see cref="Frame.WithIdleTimeout"/>.</summary>
    internal sealed class IdleTimeoutDecorator : IFrameDecoder
    {
        private readonly IFrameDecoder _inner;

        internal IdleTimeoutDecorator(IFrameDecoder inner, TimeSpan gap)
        {
            _inner = inner;
            IdleTimeout = gap;
        }

        public TimeSpan? IdleTimeout { get; }

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
            => _inner.Decode(context, payload);
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
