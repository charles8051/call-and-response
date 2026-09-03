using System;
using System.Buffers;

namespace CallAndResponse.Framing
{
    /// <summary>Options for <see cref="HdlcCodec"/>.</summary>
    public sealed record HdlcOptions
    {
        /// <summary>What to do with a frame whose FCS does not match. Defaults to throwing.</summary>
        /// <remarks>
        /// RFC 1662 says to discard silently. That is right for a datagram link and wrong for a
        /// request/response one, where a silently dropped reply is a call that hangs until its token
        /// fires and tells the caller nothing.
        /// </remarks>
        public InvalidFrameAction OnFcsMismatch { get; init; } = InvalidFrameAction.Throw;

        /// <summary>
        /// Which of <c>0x00</c>–<c>0x1F</c> to escape when sending. RFC 1662 §7.1 gives
        /// <c>0xFFFFFFFF</c> as the pre-negotiation default: escape all of them.
        /// </summary>
        public uint SendAccm { get; init; } = 0xFFFFFFFF;

        /// <summary>
        /// Which of <c>0x00</c>–<c>0x1F</c> to discard when they arrive unescaped. These are
        /// presumed to have been inserted by the link rather than the peer.
        /// </summary>
        public uint ReceiveAccm { get; init; } = 0xFFFFFFFF;

        /// <summary>Longest encoded frame body accepted. Defaults to the standard PPP MRU.</summary>
        public int MaxFrameLength { get; init; } = 1500;

        /// <summary>
        /// Address and control octets to prepend on send and strip on receive, usually
        /// <c>FF 03</c>. Null means the payload is framed as given: this type does RFC 1662 framing,
        /// not PPP, so it has no opinion about what the frame carries.
        /// </summary>
        public byte[]? AddressAndControl { get; init; }
    }

    /// <summary>
    /// RFC 1662 asynchronous HDLC framing — the framing half of PPP, and none of the rest of it.
    /// Frames are delimited by <c>0x7E</c>, escaped with <c>0x7D</c> plus a <c>0x20</c> XOR, and
    /// carry a 16-bit FCS.
    /// </summary>
    /// <remarks>
    /// LCP, authentication, and the NCPs are out of scope: those are a link state machine, and this
    /// library does not own link lifecycle. A consequence is that the ACCM is configuration here
    /// rather than something negotiated.
    /// </remarks>
    public sealed class HdlcCodec : IFrameCodec
    {
        /// <summary>Frame delimiter.</summary>
        public const byte Flag = 0x7E;

        /// <summary>Escape prefix.</summary>
        public const byte ControlEscape = 0x7D;

        /// <summary>An escaped octet is transmitted XORed with this.</summary>
        public const byte EscapeXor = 0x20;

        /// <summary>What the FCS calculation yields across a frame that includes its own good FCS.</summary>
        private const ushort GoodFcs = 0xF0B8;

        private const ushort InitialFcs = 0xFFFF;

        private static readonly ushort[] FcsTable = BuildFcsTable();

        private readonly HdlcOptions _options;

        /// <summary>Create a codec, optionally with non-default options.</summary>
        public HdlcCodec(HdlcOptions? options = null) => _options = options ?? new HdlcOptions();

        /// <inheritdoc />
        public TimeSpan? IdleTimeout => null;

        /// <inheritdoc />
        public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            var prefix = _options.AddressAndControl ?? Array.Empty<byte>();

            // The FCS covers the unescaped frame contents and is then escaped along with them, so
            // it has to be computed before any escaping happens.
            ushort fcs = InitialFcs;
            foreach (byte b in prefix) fcs = Update(fcs, b);
            foreach (byte b in payload) fcs = Update(fcs, b);
            fcs = (ushort)~fcs;

            byte fcsLow = (byte)(fcs & 0xFF);
            byte fcsHigh = (byte)(fcs >> 8);

            // Worst case is every octet escaping, plus both flags.
            var span = destination.GetSpan((prefix.Length + payload.Length + 2) * 2 + 2);
            int written = 0;

            span[written++] = Flag;
            foreach (byte b in prefix) WriteEscaped(span, ref written, b, _options.SendAccm);
            foreach (byte b in payload) WriteEscaped(span, ref written, b, _options.SendAccm);
            WriteEscaped(span, ref written, fcsLow, _options.SendAccm);
            WriteEscaped(span, ref written, fcsHigh, _options.SendAccm);
            span[written++] = Flag;

            destination.Advance(written);
        }

        /// <inheritdoc />
        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            var received = context.Received;

            long bodyStart = SlipCodec.CountLeading(received, Flag);
            if (bodyStart >= received.Length)
            {
                return bodyStart > 0
                    ? FrameDecodeResult.Discard((int)bodyStart)
                    : FrameDecodeResult.NeedMoreData;
            }

            long flagIndex = Frame.IndexOf(received, stackalloc byte[] { Flag }, bodyStart);
            if (flagIndex < 0)
            {
                return received.Length - bodyStart > _options.MaxFrameLength
                    ? FrameDecodeResult.Invalid(
                        (int)received.Length,
                        $"No HDLC flag within {_options.MaxFrameLength} bytes ({received.Length - bodyStart} accumulated).")
                    : FrameDecodeResult.NeedMoreData;
            }

            long bodyLength = flagIndex - bodyStart;
            int consumed = (int)(flagIndex + 1);

            if (bodyLength > _options.MaxFrameLength)
            {
                return Reject(consumed, $"HDLC frame of {bodyLength} bytes exceeds the {_options.MaxFrameLength}-byte maximum.");
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent((int)bodyLength);
            byte[] unescaped = ArrayPool<byte>.Shared.Rent((int)bodyLength);
            try
            {
                var body = rented.AsSpan(0, (int)bodyLength);
                received.Slice(bodyStart, bodyLength).CopyTo(body);

                if (!TryUnescape(body, unescaped, _options.ReceiveAccm, out int length, out string? reason))
                {
                    return Reject(consumed, reason!);
                }

                var frame = unescaped.AsSpan(0, length);

                int prefixLength = _options.AddressAndControl?.Length ?? 0;
                if (frame.Length < prefixLength + 2)
                {
                    // Too short to hold an FCS. RFC 1662 treats these as inter-frame fill rather
                    // than as corruption, so they are dropped without raising an error.
                    return FrameDecodeResult.Discard(consumed);
                }

                ushort fcs = InitialFcs;
                foreach (byte b in frame) fcs = Update(fcs, b);
                if (fcs != GoodFcs)
                {
                    return _options.OnFcsMismatch == InvalidFrameAction.Discard
                        ? FrameDecodeResult.Discard(consumed)
                        : FrameDecodeResult.Invalid(consumed, "HDLC frame check sequence mismatch.");
                }

                // Strip the FCS and, when configured, the address and control octets.
                payload.Write(frame.Slice(prefixLength, frame.Length - prefixLength - 2));
                return FrameDecodeResult.Frame(consumed);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
                ArrayPool<byte>.Shared.Return(unescaped);
            }
        }

        private FrameDecodeResult Reject(int consumed, string reason)
            => _options.OnFcsMismatch == InvalidFrameAction.Discard
                ? FrameDecodeResult.Discard(consumed)
                : FrameDecodeResult.Invalid(consumed, reason);

        private static void WriteEscaped(Span<byte> span, ref int written, byte value, uint accm)
        {
            if (NeedsEscape(value, accm))
            {
                span[written++] = ControlEscape;
                span[written++] = (byte)(value ^ EscapeXor);
            }
            else
            {
                span[written++] = value;
            }
        }

        private static bool NeedsEscape(byte value, uint accm)
        {
            if (value == Flag || value == ControlEscape) return true;
            return value < 0x20 && (accm & (1u << value)) != 0;
        }

        private static bool TryUnescape(
            ReadOnlySpan<byte> body, Span<byte> destination, uint receiveAccm, out int length, out string? reason)
        {
            length = 0;

            for (int i = 0; i < body.Length; i++)
            {
                byte b = body[i];

                if (b == ControlEscape)
                {
                    if (++i == body.Length)
                    {
                        reason = "HDLC frame ends with a control escape and no escaped octet.";
                        return false;
                    }

                    // An escaped control octet was meant, so the receive ACCM does not apply to it.
                    destination[length++] = (byte)(body[i] ^ EscapeXor);
                    continue;
                }

                // RFC 1662: discard unescaped control octets the receive ACCM flags. They were
                // inserted by the link, not sent by the peer.
                if (b < 0x20 && (receiveAccm & (1u << b)) != 0) continue;

                destination[length++] = b;
            }

            reason = null;
            return true;
        }

        private static ushort Update(ushort fcs, byte value) => (ushort)((fcs >> 8) ^ FcsTable[(fcs ^ value) & 0xFF]);

        /// <summary>
        /// The RFC 1662 FCS-16 table: CRC-16/X-25, reflected polynomial <c>0x8408</c>. Generated
        /// rather than transcribed, because a one-bit error in 256 literals is invisible until it
        /// meets a real peer.
        /// </summary>
        private static ushort[] BuildFcsTable()
        {
            const ushort polynomial = 0x8408;
            var table = new ushort[256];

            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ polynomial) : (ushort)(crc >> 1);
                }

                table[i] = crc;
            }

            return table;
        }
    }
}
