using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse.Modbus
{
    public interface IModbusClient
    {
        Task Open();
        Task Close();
        Task<Memory<byte>> ReadHoldingRegistersAsync(byte unitIdentifier, ushort startingAddress, ushort quantity, CancellationToken cancellationToken = default);
        Task WriteMultipleRegistersAsync(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    }
}
