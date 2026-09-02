using System;
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

        private const byte Ack = 0x79;
        private const byte Nack = 0x1F;
        public const uint Stm32BaseAddress = 0x08000000;

        // AN3155 3.7 special erase codes, sent as the first (and only) half-word of the erase payload
        private const ushort MassEraseCode = 0xFFFF;
        private const ushort Bank1EraseCode = 0xFFFE;
        private const ushort Bank2EraseCode = 0xFFFD;

        // Half-words 0xFFFD..0xFFFF are the special codes above, so the largest page-list half-word
        // N is 0xFFFC, which describes N + 1 = 0xFFFD pages.
        private const int MaxErasePageCount = 0xFFFD;

        // TODO: provide MCU model specific support

        // TODO: Add Transceiver configuration options. The transceiver we use here must be capable of 8 Data Bits, Even Parity, 1 Stop Bit.
        // our BLE implementation won't work out of the box, we'd need a separate BLE Service to configure
        public Stm32BootloaderClient(ITransceiver transceiver)
        {
            // 8 Data Bits, Even Parity, 1 Stop Bit
            _transceiver = transceiver;
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
                throw new Stm32BootloaderException($"Unexpected reply 0x{result.Span[0]:X2} to the sync byte; expected ACK (0x{Ack:X2}) or NACK (0x{Nack:X2}).");
            }
        }

        public async Task<byte> Special(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.Special, 0xAF }, 1, token);
            return result.Span[0];
        }

        public Task GetProtocolVersion(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        // AN3155 section 3.3: the reply is ACK, N = 0x01, PID high, PID low, ACK.
        // The product id is the two bytes at [2..3]; index 4 is the closing ACK.
        public async Task<ushort> GetId(CancellationToken token = default)
        {
            //var result = await _transceiver.SendReceiveHeaderFooter(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, new byte[] { Ack }, new byte[] { Ack }, token);
            var result = await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.GetId, 0xFD }, 5, token);

            // SendReceiveExactly only guarantees the byte count, so check the framing before trusting
            // the payload. A stream left out of sync by an earlier command can deliver five bytes whose
            // [2..3] would otherwise parse as a plausible id.
            if (result.Span[0] != Ack || result.Span[1] != 0x01 || result.Span[4] != Ack)
            {
                throw new InvalidOperationException($"Malformed Get ID response {BitConverter.ToString(result.ToArray())}");
            }

            return (ushort)((result.Span[2] << 8) | result.Span[3]);
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

            var addressBytes = BitConverter.GetBytes(address);
            Array.Reverse(addressBytes);
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

            var addressBytes = BitConverter.GetBytes(address);
            Array.Reverse(addressBytes);
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

            var addressBytes = BitConverter.GetBytes(jumpAddress);
            Array.Reverse(addressBytes);
            byte addressChecksumByte = (byte)(addressBytes[0] ^ addressBytes[1] ^ addressBytes[2] ^ addressBytes[3]);
            var payload = addressBytes.Append(addressChecksumByte);
            await _transceiver.SendReceivePerfectMatch(payload.ToArray(), new byte[] { Ack }, token);
        }

        public Task EraseMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        // Only available for USART Booloader 3.0+
        [Obsolete("Sends AN3155 half-word N, so it erases pages 0 through numPages inclusive - one more page than the name suggests - and it cannot start above page 0. Use ExtendedErasePages, ExtendedEraseMass, or ExtendedEraseBank instead.")]
        public Task ExtendedEraseMemoryPages(ushort numPages, CancellationToken token = default)
        {
            // Deliberately bypasses the page-count guard in ExtendedErasePages so that every ushort
            // input still produces byte-for-byte the frame this method has always sent - including
            // the malformed ones at 0xFFFD..0xFFFF, where the half-word collides with a special code.
            var shorts = new ushort[numPages + 2];
            shorts[0] = numPages;

            for (int i = 0; i < numPages + 1; i++)
            {
                shorts[i + 1] = (ushort)i;
            }

            return SendExtendedErase(shorts, token);
        }

        // Only available for USART Booloader 3.0+
        // AN3155 3.7: special code 0xFFFF, sent as a bare half-word plus checksum with no page list.
        public Task ExtendedEraseMass(CancellationToken token = default)
        {
            return ExtendedEraseSpecial(MassEraseCode, token);
        }

        // Only available for USART Booloader 3.0+
        // AN3155 3.7: special codes 0xFFFE (bank 1) and 0xFFFD (bank 2), sent as a bare half-word
        // plus checksum with no page list.
        public Task ExtendedEraseBank(int bank, CancellationToken token = default)
        {
            if (bank != 1 && bank != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), bank, "Bank must be 1 or 2");
            }
            return ExtendedEraseSpecial(bank == 1 ? Bank1EraseCode : Bank2EraseCode, token);
        }

        // Only available for USART Booloader 3.0+
        // AN3155 3.7: half-word N = pages.Count - 1, followed by the page numbers, then the checksum.
        public Task ExtendedErasePages(IReadOnlyList<ushort> pages, CancellationToken token = default)
        {
            if (pages is null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (pages.Count == 0)
            {
                throw new ArgumentException("At least one page must be specified", nameof(pages));
            }
            if (pages.Count > MaxErasePageCount)
            {
                throw new ArgumentException($"At most {MaxErasePageCount} pages can be erased in one command; half-words above 0xFFFC are reserved for mass and bank erase", nameof(pages));
            }

            var shorts = new ushort[pages.Count + 1];
            shorts[0] = (ushort)(pages.Count - 1);

            for (int i = 0; i < pages.Count; i++)
            {
                shorts[i + 1] = pages[i];
            }

            return SendExtendedErase(shorts, token);
        }

        private Task ExtendedEraseSpecial(ushort code, CancellationToken token)
        {
            return SendExtendedErase(new ushort[] { code }, token);
        }

        private async Task SendExtendedErase(ushort[] shorts, CancellationToken token)
        {
            await _transceiver.SendReceivePerfectMatch(new byte[] { (byte)Stm32BootloaderCommand.ExtendedEraseMemory, 0xBB }, new byte[] { Ack }, token);

            var payload = shorts.SelectMany((x) => { var b = BitConverter.GetBytes(x); Array.Reverse(b); return b; });
            var checksum = (byte)~(ComputeChecksum(payload.ToArray()));
            payload = payload.Append(checksum);

            await _transceiver.SendReceivePerfectMatch(payload.ToArray(), new byte[] { Ack }, token);
        }

        public Task WriteProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public Task WriteUnprotect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public Task ReadoutProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public Task ReadoutUnprotect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        public Task GetChecksum(CancellationToken token = default)
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
