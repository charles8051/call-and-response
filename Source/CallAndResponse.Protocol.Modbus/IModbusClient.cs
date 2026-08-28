using System;
using System.Threading;
using System.Threading.Tasks;

namespace CallAndResponse.Protocol.Modbus
{
    public interface IModbusClient
    {
        Task<Memory<byte>> ReadHoldingRegisters(byte unitIdentifier, ushort startingAddress, ushort quantity, CancellationToken cancellationToken = default);
        Task WriteRegisters(byte unitIdentifier, ushort startingAddress, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    }
}
