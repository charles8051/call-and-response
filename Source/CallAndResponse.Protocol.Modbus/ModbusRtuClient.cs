using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse.Protocol.Modbus
{
    public class ModbusRtuClient : IModbusClient
    {
        private ITransceiver _transceiver;
        private ILogger Logger = new LoggerConfiguration().CreateLogger();

        public ModbusRtuClient(ITransceiver transceiver)
        {
            _transceiver = transceiver;
        }

        public async Task Open(CancellationToken token = default)
        {
            await _transceiver.Open(token);
        }

        public async Task Close(CancellationToken token = default)
        {
            await _transceiver.Close(token);
        }

        public Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, int numBytes, CancellationToken token = default)
        {
            if (numBytes % 2 != 0) throw new ArgumentException();
            return ReadHoldingRegisters(unitIdentifier, startingAddress, numRegisters:(ushort)(numBytes / 2), token);
        }

        public async Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, ushort numRegisters, CancellationToken token = default)
        {
            var call = new ModbusRtuRequestBuilder()
                .SetUnitIdentifier(unitIdentifier)
                .SetStartingAddress(startingAddress)
                .SetFunctionCode(ModbusFunctionCode.ReadHoldingRegisters)
                .SetNumItems(numRegisters)
                .Build();

            try
            {
                var response = await _transceiver.SendReceive(call, 5 + 2 * numRegisters, token).ConfigureAwait(false);
                ValidateResponse(unitIdentifier, response, ModbusFunctionCode.ReadHoldingRegisters);
                var payload = response.Slice(3, response.Length - 5);
                return payload.Flip16BitValues();
            }
            catch (TransceiverTransportException e)
            {
                throw new ModbusTransportException("Transceiver is cooked", e);
            }
            catch (Exception e)
            {
                throw;
            }
        }

        public async Task WriteRegisters(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var call = new ModbusRtuRequestBuilder()
                .SetUnitIdentifier(unitIdentifier)
                .SetStartingAddress(startingAddress)
                .SetFunctionCode(ModbusFunctionCode.WriteMultipleRegisters)
                .SetNumItems((ushort)data.Length)
                .SetData(data.ToArray())
                .Build();

            try
            {
                var response = await _transceiver.SendReceive(call, 3 + 2, token).ConfigureAwait(false);
                ValidateResponse(unitIdentifier, response, ModbusFunctionCode.WriteMultipleRegisters);
            }
            catch (TransceiverTransportException e)
            {
                throw new ModbusTransportException("Transceiver is cooked", e);
            }
            catch (Exception e)
            {
                throw;
            }
        }

        private void ValidateResponse(byte unitIdentifier, Memory<byte> frame, ModbusFunctionCode functionCode)
        {
            var header = frame.Slice(0, 3).ToArray();
            if (header[0] != unitIdentifier)
            {
                throw new ModbusFramingException("Unit identifier mismatch");
            }
            if ((header[1] & 0x7F) != (byte)functionCode)
            {
                throw new ModbusFramingException("Function code mismatch");
            }
            if ((header[1] & 0x80) > 1)
            {
                throw new ModbusProtocolException((ModbusProtocolExceptionCode)header[3]);
            }

            // TODO: Validate CRC

        }
    }
}
