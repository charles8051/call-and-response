using System;
using System.Collections.Generic;
using System.Text;

namespace CallAndResponse.Protocol.Modbus
{
    internal static class ModbusUtils
    {
        internal static Memory<byte> Flip16BitValues(this Memory<byte> data)
        {
            // Check for even number of bytes
            if (data.Length % 2 != 0)
            {
                throw new ArgumentException("Data length must be even");
            }

            var span = data.Span;
            for (int i = 0; i < span.Length; i += 2)
            {
                byte temp = span[i];
                span[i] = span[i + 1];
                span[i + 1] = temp;
            }
            return data;
        }
    }
}
