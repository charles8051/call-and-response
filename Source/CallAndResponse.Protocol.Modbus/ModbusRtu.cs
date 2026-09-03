using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;

namespace CallAndResponse.Protocol.Modbus
{
    /// <summary>
    /// Modbus RTU framing: the inter-frame gap decides where a frame ends, and a CRC-16 decides
    /// whether it arrived intact.
    /// </summary>
    public static class ModbusRtu
    {
        /// <summary>
        /// The 3.5-character silence that separates RTU frames, for a given baud rate. Above 19200
        /// the standard fixes it at 1.75ms rather than deriving it.
        /// </summary>
        /// <remarks>
        /// The baud rate lives with the application, which opened the port; a transceiver deliberately
        /// does not know it.
        /// </remarks>
        public static TimeSpan GapFor(int baudRate)
        {
            if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "Baud rate must be positive.");

            if (baudRate > 19200) return TimeSpan.FromMilliseconds(1.75);

            // 11 bits per character (start, 8 data, parity, stop), 3.5 characters.
            double seconds = 11.0 * 3.5 / baudRate;
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>Create an RTU codec framing on <paramref name="interFrameGap"/>.</summary>
        public static IFrameCodec Codec(TimeSpan interFrameGap) => new ModbusRtuCodec(interFrameGap);

        /// <summary>
        /// Bind RTU framing to <paramref name="transceiver"/>, giving the channel
        /// <see cref="ModbusRtuClient"/> requires.
        /// </summary>
        public static ModbusRtuChannel Channel(ITransceiver transceiver, TimeSpan interFrameGap)
            => new ModbusRtuChannel(transceiver, interFrameGap);

        /// <summary>Bind RTU framing with the gap derived from <paramref name="baudRate"/>.</summary>
        public static ModbusRtuChannel Channel(ITransceiver transceiver, int baudRate)
            => new ModbusRtuChannel(transceiver, GapFor(baudRate));
    }

    /// <summary>
    /// A message channel that is RTU-framed by construction.
    /// </summary>
    /// <remarks>
    /// <see cref="ModbusRtuClient"/> takes this rather than a bare <see cref="IMessageTransceiver"/>
    /// because it reads returned bytes as CRC-checked, gap-delimited RTU frames. Any other channel
    /// would satisfy the interface and silently produce requests with no CRC and responses that were
    /// never validated, so the type is what guarantees the framing rather than a convention.
    /// </remarks>
    public sealed class ModbusRtuChannel : IMessageTransceiver
    {
        private readonly IMessageTransceiver _inner;

        internal ModbusRtuChannel(ITransceiver transceiver, TimeSpan interFrameGap)
        {
            if (transceiver is null) throw new ArgumentNullException(nameof(transceiver));
            _inner = transceiver.WithFraming(new ModbusRtuCodec(interFrameGap));
        }

        /// <inheritdoc />
        public Task SendMessage(ReadOnlyMemory<byte> payload, CancellationToken token)
            => _inner.SendMessage(payload, token);

        /// <inheritdoc />
        public Task<Memory<byte>> ReceiveMessage(CancellationToken token)
            => _inner.ReceiveMessage(token);
    }

    /// <summary>
    /// Appends the CRC on send, and on receive frames on the idle gap, verifies the CRC, and strips
    /// it. Framing on the gap rather than on an expected length is what lets a short exception
    /// response parse: it is simply a shorter frame.
    /// </summary>
    internal sealed class ModbusRtuCodec : IFrameCodec
    {
        internal ModbusRtuCodec(TimeSpan interFrameGap)
        {
            if (interFrameGap <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interFrameGap), interFrameGap, "Inter-frame gap must be positive.");

            IdleTimeout = interFrameGap;
        }

        public TimeSpan? IdleTimeout { get; }

        public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            ushort crc = Crc16(payload);

            var span = destination.GetSpan(payload.Length + 2);
            payload.CopyTo(span);
            span[payload.Length] = (byte)(crc & 0xFF);
            span[payload.Length + 1] = (byte)(crc >> 8);
            destination.Advance(payload.Length + 2);
        }

        public FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload)
        {
            var received = context.Received;

            if (received.IsEmpty) return FrameDecodeResult.NeedMoreData;
            if (!context.IsIdle && !context.IsTransportComplete) return FrameDecodeResult.NeedMoreData;

            int length = (int)received.Length;

            // Unit id, function code, and CRC is the shortest legal frame.
            if (length < 4)
            {
                return FrameDecodeResult.Invalid(length, $"Modbus RTU frame of {length} byte(s) is too short to be valid.");
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var frame = rented.AsSpan(0, length);
                received.CopyTo(frame);

                ushort expected = Crc16(frame.Slice(0, length - 2));
                ushort actual = (ushort)(frame[length - 2] | (frame[length - 1] << 8));

                if (expected != actual)
                {
                    return FrameDecodeResult.Invalid(
                        length, $"Modbus RTU CRC mismatch: computed 0x{expected:X4}, frame carried 0x{actual:X4}.");
                }

                payload.Write(frame.Slice(0, length - 2));
                return FrameDecodeResult.Frame(length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        internal static ushort Crc16(ReadOnlySpan<byte> frame)
        {
            ushort crc = 0xFFFF;

            foreach (byte value in frame)
            {
                crc ^= value;

                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
                }
            }

            return crc;
        }
    }
}
