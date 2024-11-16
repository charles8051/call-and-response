using System;

namespace CallAndResponse.Protocol.Modbus
{
    public class ModbusProtocolException : Exception
    {
        public ModbusProtocolExceptionCode ExceptionCode { get; }

        internal ModbusProtocolException(ModbusProtocolExceptionCode exceptionCode, string message = "") : base(message)
        {
            ExceptionCode = exceptionCode;
        }
    }
}
