using System;

namespace CallAndResponse.Modbus
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
