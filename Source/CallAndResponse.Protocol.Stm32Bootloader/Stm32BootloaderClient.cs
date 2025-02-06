using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;

namespace CallAndResponse.Protocol.Stm32Bootloader
{

    public class Stm32BootloaderClient
    {
        private ITransceiver _transceiver;

        private const byte Ack = 0x79;
        private const byte Nack = 0x1F;
        public const uint Stm32BaseAddress = 0x08000000;

        // TODO: provide MCU model specific support

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
        public async Task Close(CancellationToken token = default)
        {
            await _transceiver.Close(token);
        }


        public async Task<Stm32ProtocolInfo> GetSupportedCommands(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceiveHeaderFooter(new byte[] { (byte)Stm32BootloaderCommand.Get, 0xFF }, new byte[] { Ack }, new byte[] { Ack }, token);

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

        public async Task<bool> Ping(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceiveExactly(new byte[] { 0x7F }, 1, token);
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

        public async Task<byte> Special(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.Special, 0xAF }, 1, token);
            return result.Span[0];
        }

        public async Task GetProtocolVersion(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public async Task<byte> GetId(CancellationToken token = default)
        {
            //var result = await _transceiver.SendReceiveHeaderFooter(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, new byte[] { Ack }, new byte[] { Ack }, token);
            var result = await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, 5, token);

            return result.Span[4];
        }

        public async Task<ReadOnlyMemory<byte>> ReadMemory(uint address, uint length, CancellationToken token = default)
        {
            var result = new List<byte>();
            while (length > 0)
            {
                var readLength = Math.Min(length, 256);
                var data = await Read256(address, readLength, token);
                result.AddRange(data.ToArray());
                address += readLength;
                length -= readLength;
            }
            return result.ToArray();
        }

        private async Task<ReadOnlyMemory<byte>> Read256(uint address, uint length, CancellationToken token = default)
        {
            if (length > 256) throw new ArgumentException();

            // Initiate command
            await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.ReadMemory, 0xEE }, 1, token);

            var addressBytes = BitConverter.GetBytes(address).Reverse().ToArray();
            var checksum = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]);

            var sendBytes = addressBytes.ToList();
            sendBytes.Add(checksum);

            await _transceiver.SendReceiveExactly(sendBytes.ToArray(), 1, token);

            var byteLengthChecksum = (byte)((length-1) ^ 0xFF);
            var result = await _transceiver.SendReceiveExactly( new byte[] { (byte)(length-1), byteLengthChecksum }, (int)length + 1, token);
            return result.Slice(1);
        }

        public async Task WriteMemory(ReadOnlyMemory<byte> data, uint address = Stm32BaseAddress, CancellationToken token = default)
        {
            var numBytesWritten = 0;
            var numBytes = data.Length;
            while(numBytes > 0)
            {
                var writeLength = Math.Min(numBytes, 256);
                await Write256(address, data.Slice(numBytesWritten, writeLength), token);
                numBytesWritten += (int)writeLength;
                numBytes -= writeLength;
                address += (uint)writeLength;
            }
        }
        private async Task Write256(uint address, ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            if(data.Length > 256)
            {
                throw new ArgumentException("Data length must be less than or equal to 256 bytes");
            }

            await _transceiver.SendReceivePerfectMatch(new byte[] { (byte)Stm32BootloaderCommand.WriteMemory, 0xCE }, new byte[] { Ack }, token);

            var addressBytes = BitConverter.GetBytes(address).Reverse().ToArray();
            var checksum = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]);
            var sendBytes = addressBytes.ToList();
            sendBytes.Add(checksum);
            await _transceiver.SendReceivePerfectMatch(sendBytes.ToArray(), new byte[] { Ack }, token);

            var length = (byte)(data.Length-1);
            byte dataChecksum = (byte)(~(ComputeChecksum(data.ToArray()) ^ (byte)length));

            sendBytes = new List<byte> { length };
            sendBytes.AddRange(data.ToArray());
            sendBytes.Add(dataChecksum);

            await _transceiver.SendReceivePerfectMatch(sendBytes.ToArray(), new byte[] { Ack }, token);
        }

        private byte ComputeChecksum(byte[] data)
        {
            byte xor = 0xff;
            for (int i = 0; i < data.Length; i++)
                xor ^= data[i];
            return xor;
        }

        public async Task Go(uint jumpAddress = Stm32BaseAddress, CancellationToken token = default)
        {
            await _transceiver.SendReceivePerfectMatch(new byte[] { (byte)Stm32BootloaderCommand.Go, 0xDE }, new byte[] { Ack }, token);

            var addressBytes = BitConverter.GetBytes(jumpAddress).Reverse().ToArray();
            byte addressChecksumByte = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]);
            var payload = addressBytes.Append(addressChecksumByte);
            await _transceiver.SendReceivePerfectMatch(payload.ToArray(), new byte[] { Ack }, token);
        }

        public async Task EraseMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        // Only available for USART Booloader 3.0+
        public async Task ExtendedEraseMemoryPages(ushort numPages, CancellationToken token = default)
        {
            await _transceiver.SendReceivePerfectMatch(new byte[] { (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB }, new byte[] { Ack }, token);

            var shorts = new List<ushort>();
            shorts.Add(numPages);

            for(int i = 0; i < numPages + 1; i++)
            {
                shorts.Add((ushort)i);
            }

            var payload = shorts.SelectMany((x) => BitConverter.GetBytes(x).Reverse());
            var checksum = (byte)~(ComputeChecksum(payload.ToArray()));
            payload = payload.Append(checksum);

            await _transceiver.SendReceivePerfectMatch(payload.ToArray(), new byte[] { Ack }, token);
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
