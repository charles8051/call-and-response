using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse.Modbus
{
    public class ModbusRtuClient : IModbusClient
    {
        private ITransceiver _transceiver;
        public ModbusRtuClient(ITransceiver transceiver)
        {
            _transceiver = transceiver;
        }

        public async Task Open()
        {
            await _transceiver.Open();
        }

        public async Task Close()
        {
            await _transceiver.Close();
        }

        public async Task<Memory<byte>> ReadHoldingRegistersAsync(byte unitIdentifier, ushort startingAddress, ushort numRegisters, CancellationToken token = default)
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
            catch (TransceiverException e)
            {
                throw new ModbusTransportException("Transceiver is cooked", e);
            }
            catch (OperationCanceledException e)
            {
                throw;
            }
        }

        public Task WriteMultipleRegistersAsync(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
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
