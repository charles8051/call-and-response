using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse.Framing;

namespace CallAndResponse.Protocol.Modbus
{
    /// <summary>
    /// A Modbus RTU client over a message channel framed by <see cref="ModbusRtu.Codec"/>.
    /// </summary>
    /// <remarks>
    /// The client sees whole RTU frames with the CRC already checked and stripped, so it never
    /// predicts a response length. That is what lets a short exception response be parsed as one:
    /// under a length-based framing it would never complete.
    /// </remarks>
    public class ModbusRtuClient : IModbusClient
    {
        private readonly IMessageTransceiver _channel;
        private readonly ILogger _logger;

        /// <summary>
        /// Create a client over an RTU-framed channel — normally
        /// <c>transceiver.WithFraming(ModbusRtu.Codec(ModbusRtu.GapFor(baudRate)))</c>.
        /// </summary>
        public ModbusRtuClient(IMessageTransceiver channel)
            : this(channel, NullLogger<ModbusRtuClient>.Instance)
        {
        }

        /// <inheritdoc cref="ModbusRtuClient(IMessageTransceiver)" />
        public ModbusRtuClient(IMessageTransceiver channel, ILogger<ModbusRtuClient> logger)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _logger = logger ?? NullLogger<ModbusRtuClient>.Instance;
        }

        public Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, int numBytes, CancellationToken token = default)
        {
            if (numBytes % 2 != 0) throw new ArgumentException("Byte count must be even.", nameof(numBytes));
            return ReadHoldingRegisters(unitIdentifier, startingAddress, numRegisters: (ushort)(numBytes / 2), token);
        }

        public async Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, ushort numRegisters, CancellationToken token = default)
        {
            var call = new ModbusRtuRequestBuilder()
                .SetUnitIdentifier(unitIdentifier)
                .SetStartingAddress(startingAddress)
                .SetFunctionCode(ModbusFunctionCode.ReadHoldingRegisters)
                .SetNumItems(numRegisters)
                .Build();

            var response = await Exchange(call, token).ConfigureAwait(false);
            ValidateResponse(unitIdentifier, response, ModbusFunctionCode.ReadHoldingRegisters);

            // unit id, function code, byte count, then the register data.
            if (response.Length < 3)
            {
                throw new ModbusFramingException($"Read response of {response.Length} byte(s) has no byte-count field.");
            }

            int declared = response.Span[2];
            var payload = response.Slice(3);

            if (declared != payload.Length)
            {
                throw new ModbusFramingException(
                    $"Read response declares {declared} data byte(s) and carries {payload.Length}.");
            }

            if (declared != 2 * numRegisters)
            {
                throw new ModbusFramingException(
                    $"Read of {numRegisters} register(s) answered with {declared} data byte(s).");
            }

            return payload.Flip16BitValues();
        }

        public async Task WriteRegisters(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var call = new ModbusRtuRequestBuilder()
                .SetUnitIdentifier(unitIdentifier)
                .SetStartingAddress(startingAddress)
                .SetFunctionCode(ModbusFunctionCode.WriteMultipleRegisters)
                .SetNumItems((ushort)(data.Length / 2))
                .SetData(data.ToArray())
                .Build();

            var response = await Exchange(call, token).ConfigureAwait(false);
            ValidateResponse(unitIdentifier, response, ModbusFunctionCode.WriteMultipleRegisters);

            // unit id, function code, starting address, quantity.
            if (response.Length != 6)
            {
                throw new ModbusFramingException($"Write response of {response.Length} byte(s) is not the expected 6.");
            }
        }

        private async Task<Memory<byte>> Exchange(ReadOnlyMemory<byte> call, CancellationToken token)
        {
            try
            {
                return await _channel.SendReceiveMessage(call, token).ConfigureAwait(false);
            }
            catch (FramingException e)
            {
                _logger.LogError(e, "Modbus RTU framing failure");
                throw new ModbusFramingException(e.Message);
            }
            catch (TransceiverTransportException e)
            {
                _logger.LogError(e, "Modbus RTU transport failure");
                throw new ModbusTransportException("Transceiver is cooked", e);
            }
        }

        private static void ValidateResponse(byte unitIdentifier, Memory<byte> frame, ModbusFunctionCode functionCode)
        {
            if (frame.Length < 2)
            {
                throw new ModbusFramingException($"Response of {frame.Length} byte(s) is too short to carry a header.");
            }

            var header = frame.Span;

            if (header[0] != unitIdentifier)
            {
                throw new ModbusFramingException("Unit identifier mismatch");
            }

            if ((header[1] & 0x7F) != (byte)functionCode)
            {
                throw new ModbusFramingException("Function code mismatch");
            }

            if ((header[1] & 0x80) != 0)
            {
                if (frame.Length < 3)
                {
                    throw new ModbusFramingException("Exception response carries no exception code.");
                }

                throw new ModbusProtocolException((ModbusProtocolExceptionCode)header[2]);
            }
        }
    }
}
