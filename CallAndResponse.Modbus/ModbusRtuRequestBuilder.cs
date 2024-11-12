using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CallAndResponse.Modbus
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

        public byte[] Build()
        {
            var frame = new List<byte>()
            {
                _unitIdentifier,
                (byte)_functionCode,
            };
            if (_startingAddress.HasValue)
            {
                frame.AddRange(BitConverter.GetBytes(_startingAddress.Value).Reverse());
            }
            if (_numItems.HasValue)
            {
                frame.AddRange(BitConverter.GetBytes(_numItems.Value).Reverse());
            }

            if (_functionCode == ModbusFunctionCode.ReadHoldingRegisters)
            {

            }
            else if (_functionCode == ModbusFunctionCode.WriteMultipleRegisters)
            {
                if (_data is null) throw new InvalidOperationException("Must set data for WriteMultipleRegisters");
                if (_data.Length % 2 != 0) throw new InvalidOperationException("Data must be an even number of bytes");

                // Flip every pair of bytes in _data and then add to frame
                for (int i = 0; i < _data!.Length; i += 2)
                {
                    frame.Add(_data[i + 1]);
                    frame.Add(_data[i]);
                }
            }
            else
            {
                throw new InvalidOperationException("Function code not supported");
            }

            // Add CRC
            return AddCrc(frame).ToArray();
        }

        private List<byte> AddCrc(List<byte> frame)
        {
            // apply crc16 to frame, then return the frame with the crc16 appended
            //var span = frame.Span;
            ushort crc = 0xFFFF;

            foreach (var value in frame)
            {
                crc ^= value;

                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }

            return frame.Concat(BitConverter.GetBytes(crc)).ToList();
        }
    }
}
