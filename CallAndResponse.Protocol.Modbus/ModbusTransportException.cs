using System;

namespace CallAndResponse.Protocol.Modbus
{
    [Serializable]
    internal class ModbusTransportException : Exception
    {
        public ModbusTransportException()
        {
        }

        public ModbusTransportException(string message) : base(message)
        {
        }

        public ModbusTransportException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}