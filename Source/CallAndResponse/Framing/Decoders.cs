using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>Exactly N bytes. See <see cref="Frame.Exactly"/>.</summary>
    internal sealed class ExactlyDecoder : IFrameDecoder
    {
        private readonly int _count;

        internal ExactlyDecoder(int count) => _count = count;

        public TimeSpan? IdleTimeout => null;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            if (context.Received.Length < _count) return FrameDecodeResult.NeedMoreData;

            Frame.CopyTo(context.Received, 0, _count, payload);
            return FrameDecodeResult.Frame(_count);
        }
    }

    /// <summary>Up to a terminator byte or pattern. See <see cref="Frame.UntilTerminator"/>.</summary>
    internal sealed class PatternDecoder : IFrameDecoder
    {
        private readonly byte[] _pattern;
        private readonly bool _keepInPayload;

        internal PatternDecoder(byte[] pattern, bool keepInPayload)
        {
            _pattern = pattern;
            _keepInPayload = keepInPayload;
        }

        public TimeSpan? IdleTimeout => null;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            long index = Frame.IndexOf(context.Received, _pattern);
            if (index < 0) return FrameDecodeResult.NeedMoreData;

            long payloadLength = _keepInPayload ? index + _pattern.Length : index;
            Frame.CopyTo(context.Received, 0, payloadLength, payload);

            // The pattern is consumed either way, so it cannot satisfy the next receive.
            return FrameDecodeResult.Frame((int)(index + _pattern.Length));
        }
    }

    /// <summary>Between a header and the footer that follows it. See <see cref="Frame.Between"/>.</summary>
    internal sealed class BetweenDecoder : IFrameDecoder
    {
        private readonly byte[] _header;
        private readonly byte[] _footer;

        internal BetweenDecoder(byte[] header, byte[] footer)
        {
            _header = header;
            _footer = footer;
        }

        public TimeSpan? IdleTimeout => null;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            long headerIndex = Frame.IndexOf(context.Received, _header);
            if (headerIndex < 0) return FrameDecodeResult.NeedMoreData;

            long payloadStart = headerIndex + _header.Length;
            long footerIndex = Frame.IndexOf(context.Received, _footer, payloadStart);
            if (footerIndex < 0) return FrameDecodeResult.NeedMoreData;

            Frame.CopyTo(context.Received, payloadStart, footerIndex - payloadStart, payload);
            return FrameDecodeResult.Frame((int)(footerIndex + _footer.Length));
        }
    }

    /// <summary>Everything buffered when the line goes quiet. See <see cref="Frame.UntilIdle"/>.</summary>
    internal sealed class UntilIdleDecoder : IFrameDecoder
    {
        private readonly TimeSpan _gap;

        internal UntilIdleDecoder(TimeSpan gap) => _gap = gap;

        public TimeSpan? IdleTimeout => _gap;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            // An idle window that elapsed before the first byte arrived is not a frame boundary —
            // it is the gap before the device answers. Keep waiting.
            if (context.Received.IsEmpty) return FrameDecodeResult.NeedMoreData;

            if (!context.IsIdle && !context.IsTransportComplete) return FrameDecodeResult.NeedMoreData;

            long length = context.Received.Length;
            Frame.CopyTo(context.Received, 0, length, payload);
            return FrameDecodeResult.Frame((int)length);
        }
    }

    /// <summary>Everything received when the transport closes. See <see cref="Frame.UntilTransportComplete"/>.</summary>
    internal sealed class UntilTransportCompleteDecoder : IFrameDecoder
    {
        public TimeSpan? IdleTimeout => null;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            if (!context.IsTransportComplete) return FrameDecodeResult.NeedMoreData;

            long length = context.Received.Length;
            Frame.CopyTo(context.Received, 0, length, payload);
            return FrameDecodeResult.Frame((int)length);
        }
    }

    /// <summary>A frame sized by a length field. See <see cref="Frame.LengthPrefixed"/>.</summary>
    internal sealed class LengthPrefixedDecoder : IFrameDecoder
    {
        private readonly int _prefixOffset;
        private readonly int _prefixSize;
        private readonly Endianness _endianness;
        private readonly int _lengthAdjustment;
        private readonly int _payloadOffset;
        private readonly int _trailerLength;

        internal LengthPrefixedDecoder(
            int prefixOffset, int prefixSize, Endianness endianness,
            int lengthAdjustment, int payloadOffset, int trailerLength)
        {
            _prefixOffset = prefixOffset;
            _prefixSize = prefixSize;
            _endianness = endianness;
            _lengthAdjustment = lengthAdjustment;
            _payloadOffset = payloadOffset;
            _trailerLength = trailerLength;
        }

        public TimeSpan? IdleTimeout => null;

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            long prefixEnd = (long)_prefixOffset + _prefixSize;
            if (context.Received.Length < prefixEnd) return FrameDecodeResult.NeedMoreData;

            Span<byte> prefix = stackalloc byte[_prefixSize];
            context.Received.Slice(_prefixOffset, _prefixSize).CopyTo(prefix);

            long declared = ReadPrefix(prefix);
            long bytesAfterPrefix = declared + _lengthAdjustment;

            if (bytesAfterPrefix < 0)
            {
                return FrameDecodeResult.Invalid(
                    (int)prefixEnd,
                    $"Length prefix {declared} with adjustment {_lengthAdjustment} describes a negative frame body.");
            }

            long frameLength = prefixEnd + bytesAfterPrefix;
            if (context.Received.Length < frameLength) return FrameDecodeResult.NeedMoreData;

            long payloadLength = frameLength - _payloadOffset - _trailerLength;
            if (payloadLength < 0)
            {
                return FrameDecodeResult.Invalid(
                    (int)frameLength,
                    $"A {frameLength}-byte frame cannot hold a payload at offset {_payloadOffset} with a {_trailerLength}-byte trailer.");
            }

            Frame.CopyTo(context.Received, _payloadOffset, payloadLength, payload);
            return FrameDecodeResult.Frame((int)frameLength);
        }

        private long ReadPrefix(ReadOnlySpan<byte> prefix)
        {
            long value = 0;

            if (_endianness == Endianness.BigEndian)
            {
                foreach (byte b in prefix) value = (value << 8) | b;
            }
            else
            {
                for (int i = prefix.Length - 1; i >= 0; i--) value = (value << 8) | prefix[i];
            }

            return value;
        }
    }

    /// <summary>A decoder built from a delegate. See <see cref="Frame.Custom"/>.</summary>
    internal sealed class CustomDecoder : IFrameDecoder
    {
        private readonly FrameDecodeCallback _decode;

        internal CustomDecoder(FrameDecodeCallback decode, TimeSpan? idleTimeout)
        {
            _decode = decode;
            IdleTimeout = idleTimeout;
        }

        public TimeSpan? IdleTimeout { get; }

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
            => _decode(context, payload);
    }

    /// <summary>A decoder over a flattened span. See <see cref="Frame.OverSpan"/>.</summary>
    internal sealed class SpanDecoder : IFrameDecoder
    {
        private readonly SpanFrameDecodeCallback _decode;

        internal SpanDecoder(SpanFrameDecodeCallback decode, TimeSpan? idleTimeout)
        {
            _decode = decode;
            IdleTimeout = idleTimeout;
        }

        public TimeSpan? IdleTimeout { get; }

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            if (context.Received.IsSingleSegment)
            {
                return _decode(context.Received.FirstSpan, context.IsIdle, context.IsTransportComplete, payload);
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)context.Received.Length);
            try
            {
                var span = rented.AsSpan(0, (int)context.Received.Length);
                context.Received.CopyTo(span);
                return _decode(span, context.IsIdle, context.IsTransportComplete, payload);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
