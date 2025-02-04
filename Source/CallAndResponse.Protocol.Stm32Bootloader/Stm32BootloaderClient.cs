using System;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;

namespace CallAndResponse.Protocol.Stm32Bootloader
{

    internal enum AckNack{
        Ack = 0x79,
        Nack = 0x1F
    }
    public class Stm32BootloaderClient
    {
        private ITransceiver _transceiver;

        //private static readonly byte Ack = 0x79;
        //private static readonly byte Nack = 0x1F;

        // TODO: Add Transceiver configuration options. The transceiver we use here must be capable of 8 Data Bits, Even Parity, 1 Stop Bit.
        // our BLE implementation won't work out of the box, we'd need a separate BLE Service to configure
        public Stm32BootloaderClient(ITransceiver transceiver)
        {
            // 8 Data Bits, Even Parity, 1 Stop Bit
            _transceiver = transceiver;
        }

        public async Task Open(CancellationToken token = default)
        {
            await _transceiver.Open(token);
        }

        public async Task<AckNack> Ping()
        {

        }

        public async Task<ushort> Get(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.Get, 0xFF }, 15, token);
            if (result.Span[0] != (byte)AckNack.Ack)
            {
                throw new Exception("Get command failed");
            }

            return result.Span[3];
        }

        public async Task GetProtocolVersion(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task<byte> GetId(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, 5, token);
            if (result.Span[0] != (byte)AckNack.Ack)
            {
                throw new Exception("Get command failed");
            }

            return result.Span[3];
        }

        public async Task<ReadOnlyMemory<byte>> ReadMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
        public async Task Go(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
        public async Task WriteMemory(uint address, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task EraseMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        // Only available for USART Booloader 3.0+
        public async Task ExtendedEraseMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task WriteProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task WriteUnprotect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task ReadoutProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task ReadoutUnprotect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task GetChecksum(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
    }

    public enum Stm32BootloaderCommand : byte
    {
        Get = 0x00,
        GetVersion = 0x01,
        GetId = 0x02,
        ReadMemory = 0x11,
        Go = 0x21,
        WriteMemory = 0x31,
        EraseMemory = 0x43,
        ExtendedEraseMemory = 0x44,
        Special = 0x50,
        ExtendedSpecial = 0x51,
        WriteProtect = 0x63,
        WriteUnprotect = 0x73,
        ReadoutProtect = 0x82,
        ReadoutUnprotect = 0x92,
        GetChecksum = 0xA1
    }
}
