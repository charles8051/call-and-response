using System;
using CallAndResponse;

namespace CallAndResponse.Protocol.Stm32Bootloader
{
    public class Stm32BootloaderClient
    {
        private ITransceiver _transceiver;

        private static readonly byte Ack = 0x79;
        private static readonly byte Nack = 0x1F;

        // TODO: Add Transceiver configuration options. The transceiver we use here must be capable of 8 Data Bits, Even Parity, 1 Stop Bit.
        // our BLE implementation won't work out of the box, we'd need a separate BLE Service to configure
        public Stm32BootloaderClient(ITransceiver transceiver)
        {
            // 8 Data Bits, Even Parity, 1 Stop Bit
        }

        public void Open()
        {

        }

        public void Get()
        {
            throw new NotImplementedException();
        }

        public void GetProtocolVersion()
        {
            throw new NotImplementedException();
        }

        public ushort GetId()
        {
            throw new NotImplementedException();
        }

        public ReadOnlyMemory<byte> ReadMemory(uint address, ushort length)
        {
            throw new NotImplementedException();
        }
        public void Go()
        {
            throw new NotImplementedException();
        }
        public void WriteMemory(uint address, ReadOnlyMemory<byte> data)
        {
            throw new NotImplementedException();
        }

        public void EraseMemory(uint address, ushort length)
        {
            throw new NotImplementedException();
        }

        // Only available for USART Booloader 3.0+
        public void ExtendedEraseMemory(uint address, ushort length)
        {
            throw new NotImplementedException();
        }

        public void WriteProtect()
        {
            throw new NotImplementedException();
        }

        public void WriteUnprotect()
        {
            throw new NotImplementedException();
        }

        public void ReadoutProtect()
        {
            throw new NotImplementedException();
        }

        public void ReadoutUnprotect()
        {
            throw new NotImplementedException();
        }

        public void GetChecksum()
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
