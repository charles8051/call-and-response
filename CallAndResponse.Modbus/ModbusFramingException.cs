using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse.Modbus
{
    public class ModbusFramingException : Exception
    {
        public ModbusFramingException(string message) : base(message)
        {
        }
    }
}
