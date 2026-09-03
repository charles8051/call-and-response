using System;
using System.Collections.Generic;
using System.Linq;

namespace CallAndResponse.Protocol.Modbus
{
    internal class ModbusRtuRequestBuilder
    {
        private byte _unitIdentifier;
        private ModbusFunctionCode _functionCode;
        private ushort? _startingAddress;
        private ushort? _numItems;
        private byte[]? _data;

        public ModbusRtuRequestBuilder SetUnitIdentifier(byte unitIdentifier)
        {
            _unitIdentifier = unitIdentifier;
            return this;
        }

        public ModbusRtuRequestBuilder SetFunctionCode(ModbusFunctionCode functionCode)
        {
            _functionCode = functionCode;
            return this;
        }

        public ModbusRtuRequestBuilder SetStartingAddress(ushort startingAddress)
        {
            _startingAddress = startingAddress;
            return this;
        }

        public ModbusRtuRequestBuilder SetNumItems(ushort numItems)
        {
            _numItems = numItems;
            return this;
        }

        public ModbusRtuRequestBuilder SetData(byte[] data)
        {
            _data = data;
            return this;
        }

        public Memory<byte> Build()
        {
            var frame = new List<byte>()
            {
                _unitIdentifier,
                (byte)_functionCode,
            };
            if (_startingAddress.HasValue)
            {
                var startingAddressBytes = BitConverter.GetBytes(_startingAddress.Value);
                Array.Reverse(startingAddressBytes);
                frame.AddRange(startingAddressBytes);
            }
            if (_numItems.HasValue)
            {
                var numItemsBytes = BitConverter.GetBytes(_numItems.Value);
                Array.Reverse(numItemsBytes);
                frame.AddRange(numItemsBytes);
            }

            if (_functionCode == ModbusFunctionCode.ReadHoldingRegisters)
            {

            }
            else if (_functionCode == ModbusFunctionCode.WriteMultipleRegisters)
            {
                if (_data is null) throw new InvalidOperationException("Must set data for WriteMultipleRegisters");
                if (_data.Length % 2 != 0) throw new InvalidOperationException("Data must be an even number of bytes");

                // Byte count field: number of data bytes to follow
                frame.Add((byte)_data.Length);

                // Flip every pair of bytes in _data and then add to frame
                for (int i = 0; i < _data.Length; i += 2)
                {
                    frame.Add(_data[i + 1]);
                    frame.Add(_data[i]);
                }
            }
            else
            {
                throw new InvalidOperationException("Function code not supported");
            }

            // No CRC here. The codec appends it on send and verifies it on receive, so the two can
            // no longer disagree and neither can be forgotten.
            return frame.ToArray();
        }
    }
}
