using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse.Modbus
{
    public interface IModbusClient
    {
        Task Open(CancellationToken token = default);
        Task Close(CancellationToken token = default);
        Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, ushort quantity, CancellationToken cancellationToken = default);
        Task WriteRegisters(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    }
}
