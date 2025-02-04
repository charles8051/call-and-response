using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            _transceiver = transceiver;
        }

        public async Task Open(CancellationToken token = default)
        {
            await _transceiver.Open(token);
        }

        public async Task<bool> Ping(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { 0x7F }, 1, token);
            if (result.Span[0] == Ack)
            {
                return true;
            }
            else if (result.Span[0] == Nack)
            {
                return false;
            }
            else
            {
                throw new OperationCanceledException();
            }
        }

        public async Task<Stm32ProtocolInfo> GetSupportedCommands(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.Get, 0xFF }, new byte[] { Ack }, new byte[] { Ack }, token);

            var supportedCommands = new List<Stm32BootloaderCommand>(); 
            foreach (var command in result.Span.Slice(2).ToArray())
            {
                if (!Enum.IsDefined(typeof(Stm32BootloaderCommand), command))
                {
                    throw new InvalidOperationException($"Unknown command {command}");
                }
                supportedCommands.Add((Stm32BootloaderCommand)command);
            }
            return new Stm32ProtocolInfo(result.Span[1], supportedCommands);
        }

        public async Task<byte> Special(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.Special, 0xAF }, 1, token);
            return result.Span[0];
        }

        public async Task GetProtocolVersion(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task<byte> GetId(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, new byte[] { Ack }, new byte[] { Ack }, token);

            return result.Span[2];
        }

        public async Task<ReadOnlyMemory<byte>> ReadMemory(uint address, byte length, CancellationToken token = default)
        {
            //            The host sends bytes to the STM32 as follows:
            //Send ACK byte
            //Start GID(1)
            //Received
            //byte = 0x02 + 0xFD ?
            //Send N = number of bytes – 1
            //End of GID(1)
            //No
            //Yes
            //ai14636
            //Send NACK byte
            //Send ACK byte
            //Send product ID
            //Bytes 1 - 2: 0x11 + 0xEE
            //Wait for ACK
            //Bytes 3 to 6 Start address byte 3: MSB, byte 6: LSB
            //Byte 7: Checksum: XOR(byte 3, byte 4, byte 5, byte 6)
            //Wait for ACK
            //Byte 8: The number of bytes to be read – 1(0 < N ≤ 255);
            //            Byte 9: Checksum: XOR byte 8(complement of byte 8)


            // Initiate command
            await _transceiver.SendReceive(new byte[] { (byte)Stm32BootloaderCommand.ReadMemory, 0xEE }, new byte[] { Ack }, token);

            var addressBytes = BitConverter.GetBytes(address);
            var checksum = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]);

            var sendBytes = addressBytes.ToList();
            sendBytes.Add(checksum);

            await _transceiver.SendReceive(sendBytes.ToArray(), new byte[] { Ack }, token);

            var byteLengthChecksum = (byte)(length ^ (byte)(~length));
            await _transceiver.SendReceive( new byte[] { length, byteLengthChecksum }, new byte[] { Ack }, token);

            
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

    public class Stm32ProtocolInfo
    {
        public IEnumerable<Stm32BootloaderCommand> SupportedCommands { get; set; }
        public byte ProtocolVersion { get; set; }

        public Stm32ProtocolInfo(byte protocolVersion, IEnumerable<Stm32BootloaderCommand> supportedCommands)
        {
            SupportedCommands = supportedCommands;
            ProtocolVersion = protocolVersion;
        }

        public Stm32ProtocolInfo(byte protocolVersion, params Stm32BootloaderCommand[] supportedCommands)
        {
            SupportedCommands = supportedCommands;
            ProtocolVersion = protocolVersion;
        }
    }
}
