using System;

namespace CallAndResponse.Protocol.Modbus
{
    public class ModbusFramingException : Exception
    {
        public ModbusFramingException(string message) : base(message)
        {
        }
    }
}
