using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace CallAndResponse.Modbus
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

        internal static Memory<byte> FLip16BitValues(this Span<byte> data)
        {
            // Check for even number of bytes
            if (data.Length % 2 != 0)
            {
                throw new ArgumentException("Data length must be even");
            }

            for (int i = 0; i < data.Length; i += 2)
            {
                byte temp = data[i];
                data[i] = data[i + 1];
                data[i + 1] = temp;
            }
            return data.ToArray();
        }
    }
}
