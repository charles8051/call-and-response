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

        /// <summary>
        /// Reset value of the STM32 CRC unit's polynomial register (CRC-32/MPEG-2).
        /// Used as the default polynomial for <see cref="GetChecksum"/>.
        /// </summary>
        public const uint DefaultCrcPolynomial = 0x04C11DB7;

        /// <summary>
        /// Reset value of the STM32 CRC unit's initial-value register.
        /// Used as the default seed for <see cref="GetChecksum"/>.
        /// </summary>
        public const uint DefaultCrcInitialValue = 0xFFFFFFFF;

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

        /// <summary>
        /// Get Version &amp; Read Protection Status (AN3155 command 0x01).
        /// Returns the bootloader protocol version and the two legacy option bytes.
        /// </summary>
        /// <remarks>
        /// Wire format: host sends <c>0x01 0xFE</c>; the device answers with
        /// <c>ACK, version, option byte 1, option byte 2, ACK</c>. Both option bytes read
        /// <c>0x00</c> on current parts and are kept only for protocol compatibility.
        /// <para>
        /// <see cref="GetSupportedCommands"/> also reports the protocol version, so this command
        /// is only worth issuing on its own when the supported-command list is not needed.
        /// </para>
        /// </remarks>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The bootloader protocol version and option bytes.</returns>
        /// <exception cref="InvalidOperationException">The device answered NACK or an unexpected byte.</exception>
        public async Task<Stm32VersionInfo> GetProtocolVersion(CancellationToken token = default)
        {
            var result = await _transceiver.SendReceiveExactly(new byte[] { (byte)Stm32BootloaderCommand.GetVersion, 0xFE }, 5, token);
            EnsureAck(result.Span, nameof(GetProtocolVersion));
            EnsureAck(result.Span.Slice(4), nameof(GetProtocolVersion));
            return new Stm32VersionInfo(result.Span[1], result.Span[2], result.Span[3]);
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
                throw new Stm32BootloaderException($"Malformed Get ID response {BitConverter.ToString(result.ToArray())}");
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

        /// <summary>
        /// Erase Memory by address range. Never implemented — see the remarks.
        /// </summary>
        /// <remarks>
        /// Command 0x43 addresses flash by single-byte page code, not by address and length, and
        /// mapping a range onto page codes needs a per-device flash layout this library does not
        /// have. The signature is kept, and made non-callable, only so that binaries compiled
        /// against an earlier package still resolve the method rather than failing to JIT their
        /// caller with a <see cref="MissingMethodException"/>. Use
        /// <see cref="EraseMemory(IEnumerable{byte}, CancellationToken)"/> or
        /// <see cref="EraseAllMemory"/> instead.
        /// </remarks>
        [Obsolete("EraseMemory(address, length) was never implemented; command 0x43 addresses flash by page code, not by address and length. Use EraseMemory(pageNumbers) or EraseAllMemory().", true)]
        public Task EraseMemory(uint address, ushort length, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Erase Memory (AN3155 command 0x43) — erases the listed flash memory pages.
        /// Available on USART bootloaders below 3.0; from 3.0 onwards the device exposes
        /// Extended Erase (0x44) instead. Check <see cref="GetSupportedCommands"/> before calling.
        /// </summary>
        /// <remarks>
        /// Wire format: host sends <c>0x43 0xBC</c> and waits for ACK, then sends
        /// <c>N, page codes…, checksum</c> where <c>N</c> is the page count minus one and the
        /// checksum is the XOR of <c>N</c> and every page code, and waits for a second ACK.
        /// <para>
        /// Page codes are single bytes, so at most 255 pages can be erased per command
        /// (<c>N = 255</c> is reserved for the global erase request — use
        /// <see cref="EraseAllMemory"/> for that). AN3155 notes that erasing a write-protected
        /// sector returns no error, so a successful ACK does not by itself prove the pages
        /// were erased.
        /// </para>
        /// </remarks>
        /// <param name="pageNumbers">The flash page codes to erase. Page numbering is device specific.</param>
        /// <param name="token">
        /// Cancellation token. Must allow for the device's page erase time. Cancelling does not
        /// undo an erase: once the page frame has been sent the device proceeds regardless, so an
        /// <see cref="OperationCanceledException"/> means the outcome is unknown, not that nothing
        /// happened. Treat the listed pages as possibly erased and re-establish bootloader state
        /// before writing or retrying.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="pageNumbers"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="pageNumbers"/> is empty or holds more than 255 pages.</exception>
        /// <exception cref="InvalidOperationException">The device answered NACK or an unexpected byte.</exception>
        public async Task EraseMemory(IEnumerable<byte> pageNumbers, CancellationToken token = default)
        {
            if (pageNumbers == null) throw new ArgumentNullException(nameof(pageNumbers));

            var pages = pageNumbers.ToArray();
            if (pages.Length == 0)
            {
                throw new ArgumentException("At least one page number is required.", nameof(pageNumbers));
            }
            if (pages.Length > 255)
            {
                throw new ArgumentException("Erase Memory addresses pages with a single byte, so at most 255 pages can be erased per command.", nameof(pageNumbers));
            }

            await SendAndExpectAck(new byte[] { (byte)Stm32BootloaderCommand.EraseMemory, 0xBC }, nameof(EraseMemory), token);

            var payload = new byte[pages.Length + 2];
            payload[0] = (byte)(pages.Length - 1);
            Array.Copy(pages, 0, payload, 1, pages.Length);
            byte checksum = 0;
            for (int i = 0; i < payload.Length - 1; i++)
            {
                checksum ^= payload[i];
            }
            payload[payload.Length - 1] = checksum;

            await SendAndExpectAck(payload, nameof(EraseMemory), token);
        }

        /// <summary>
        /// Erase Memory (AN3155 command 0x43) — global erase. Erases the whole flash memory.
        /// Available on USART bootloaders below 3.0; from 3.0 onwards the device exposes
        /// Extended Erase (0x44) instead. Check <see cref="GetSupportedCommands"/> before calling.
        /// </summary>
        /// <remarks>
        /// Wire format: host sends <c>0x43 0xBC</c> and waits for ACK, then sends the reserved
        /// global erase request <c>0xFF 0x00</c> and waits for a second ACK.
        /// <para>
        /// <b>This erases the entire flash memory.</b> The second ACK only arrives once the erase
        /// has completed, which takes considerably longer than a page erase — size the
        /// cancellation token against the mass erase time in the device datasheet.
        /// </para>
        /// </remarks>
        /// <param name="token">
        /// Cancellation token. Must allow for the device's mass erase time. Cancelling does not
        /// undo the erase: once <c>0xFF 0x00</c> has been sent the device erases regardless, so an
        /// <see cref="OperationCanceledException"/> means the outcome is unknown, not that nothing
        /// happened. Treat the whole flash as possibly erased and re-establish bootloader state
        /// before writing or retrying.
        /// </param>
        /// <exception cref="InvalidOperationException">The device answered NACK or an unexpected byte.</exception>
        public async Task EraseAllMemory(CancellationToken token = default)
        {
            await SendAndExpectAck(new byte[] { (byte)Stm32BootloaderCommand.EraseMemory, 0xBC }, nameof(EraseAllMemory), token);
            await SendAndExpectAck(new byte[] { 0xFF, 0x00 }, nameof(EraseAllMemory), token);
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

        /// <summary>
        /// Write Protect (AN3155 command 0x63). Not implemented — see the remarks.
        /// </summary>
        /// <remarks>
        /// Enabling write protection on the wrong sector list can leave a part that this library
        /// cannot recover, so the command is not shipped speculatively without hardware to verify
        /// it against. Declared, and deliberately non-callable, so it is not mistaken for a
        /// working command in IntelliSense.
        /// </remarks>
        [Obsolete("Write Protect (0x63) is not implemented. It can leave flash sectors locked, so it is not shipped unverified.", true)]
        public Task WriteProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write Unprotect (AN3155 command 0x73). Not implemented — see the remarks.
        /// </summary>
        /// <remarks>
        /// Clearing write protection rewrites the option bytes and triggers a system reset;
        /// like the other protection commands it is not shipped without hardware to verify it
        /// against. Declared, and deliberately non-callable, so it is not mistaken for a working
        /// command in IntelliSense.
        /// </remarks>
        [Obsolete("Write Unprotect (0x73) is not implemented. It rewrites option bytes and resets the device, so it is not shipped unverified.", true)]
        public Task WriteUnprotect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Readout Protect (AN3155 command 0x82). Not implemented — see the remarks.
        /// </summary>
        /// <remarks>
        /// Enabling read protection puts the part at RDP level 1, where Read Memory and Write
        /// Memory are refused and the only way back is <see cref="ReadoutUnprotect"/>, which mass
        /// erases the flash. Declared, and deliberately non-callable, so it is not mistaken for a
        /// working command in IntelliSense.
        /// </remarks>
        [Obsolete("Readout Protect (0x82) is not implemented. It locks the part at RDP level 1, so it is not shipped unverified.", true)]
        public Task ReadoutProtect(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Readout Unprotect (AN3155 command 0x92). Disables flash read protection and drops the
        /// part back to RDP level 0.
        /// </summary>
        /// <remarks>
        /// <b>This mass erases the flash memory.</b> On the STM32 the transition out of RDP level 1
        /// is defined to erase the whole of flash, so every byte of application code and every
        /// value stored in flash is lost. That is the mechanism, not a side effect that can be
        /// avoided: a part at RDP level 1 refuses Read Memory and Write Memory, and this command
        /// is the only route back through the bootloader.
        /// <para>
        /// Wire format: host sends <c>0x92 0x6D</c> and waits for ACK, then waits for a second ACK
        /// which the device sends only once the erase has completed. Size the cancellation token
        /// against the mass erase time in the device datasheet.
        /// </para>
        /// <para>
        /// Most parts generate a system reset after the second ACK, so the bootloader must be
        /// re-entered before any further command is issued.
        /// </para>
        /// </remarks>
        /// <param name="token">
        /// Cancellation token. Must allow for the device's mass erase time. Cancelling does not
        /// undo the erase: once <c>0x92 0x6D</c> has been accepted the device erases and drops RDP
        /// regardless, so an <see cref="OperationCanceledException"/> means the outcome is unknown,
        /// not that nothing happened. Treat the whole flash as possibly erased and re-enter the
        /// bootloader — the part has probably reset — before issuing anything else.
        /// </param>
        /// <exception cref="InvalidOperationException">The device answered NACK or an unexpected byte.</exception>
        public async Task ReadoutUnprotect(CancellationToken token = default)
        {
            await SendAndExpectAck(new byte[] { (byte)Stm32BootloaderCommand.ReadoutUnprotect, 0x6D }, nameof(ReadoutUnprotect), token);
            await ExpectAck(nameof(ReadoutUnprotect), token);
        }

        /// <summary>
        /// Get Checksum with no arguments. Never implemented — see the remarks.
        /// </summary>
        /// <remarks>
        /// Command 0xA1 needs a start address, a region size, a CRC polynomial and a CRC seed;
        /// there is nothing sensible for a no-argument form to send. The signature is kept, and
        /// made non-callable, only so that binaries compiled against an earlier package still
        /// resolve the method rather than failing to JIT their caller with a
        /// <see cref="MissingMethodException"/>. Use
        /// <see cref="GetChecksum(uint, uint, uint, uint, CancellationToken)"/> instead.
        /// </remarks>
        [Obsolete("GetChecksum() was never implemented; command 0xA1 needs an address, a size, a CRC polynomial and a CRC seed. Use GetChecksum(address, numWords, ...).", true)]
        public Task GetChecksum(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get Checksum (AN3155 command 0xA1). Asks the device to compute a CRC over a region of
        /// its own memory, which verifies a written image without reading it back over the wire.
        /// </summary>
        /// <remarks>
        /// Wire format: host sends <c>0xA1 0x5E</c>, then four big-endian parameter frames — start
        /// address, size as a count of 32-bit words, CRC polynomial, CRC initial value — each of four bytes
        /// followed by their XOR, each acknowledged with an ACK. The device then answers
        /// <c>ACK, CRC (4 bytes, MSB first), checksum</c>, where the trailing byte is the XOR of
        /// the four CRC bytes.
        /// <para>
        /// The command is only present on bootloader protocol 3.3 and later, and parts that do not
        /// support changing the polynomial or the initial value silently ignore those values and
        /// still ACK them. Check <see cref="GetSupportedCommands"/> before calling.
        /// </para>
        /// </remarks>
        /// <param name="address">Start address of the region to checksum.</param>
        /// <param name="numWords">
        /// Region size as a count of 32-bit words, which is the unit AN3155 Rev 16 §3.13 specifies
        /// for bytes 8 to 11 — "memory area size (number of 32-bit words)". It is not a byte count:
        /// <c>numWords = 0x40</c> checksums 0x100 bytes. This is also why the protocol's "must be a
        /// multiple of 4 bytes" constraint needs no validation here — a whole number of words is
        /// always a multiple of four bytes.
        /// </param>
        /// <param name="crcPolynomial">CRC polynomial. Defaults to <see cref="DefaultCrcPolynomial"/>.</param>
        /// <param name="crcInitialValue">CRC seed. Defaults to <see cref="DefaultCrcInitialValue"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The CRC the device computed over the requested region.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="numWords"/> is zero.</exception>
        /// <exception cref="InvalidOperationException">The device answered NACK, an unexpected byte, or a CRC whose checksum did not match.</exception>
        public async Task<uint> GetChecksum(uint address, uint numWords, uint crcPolynomial = DefaultCrcPolynomial, uint crcInitialValue = DefaultCrcInitialValue, CancellationToken token = default)
        {
            if (numWords == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numWords), "The memory area size must be at least one 32-bit word.");
            }

            await SendAndExpectAck(new byte[] { (byte)Stm32BootloaderCommand.GetChecksum, 0x5E }, nameof(GetChecksum), token);
            await SendAndExpectAck(BigEndianWithChecksum(address), nameof(GetChecksum), token);
            await SendAndExpectAck(BigEndianWithChecksum(numWords), nameof(GetChecksum), token);
            await SendAndExpectAck(BigEndianWithChecksum(crcPolynomial), nameof(GetChecksum), token);
            await SendAndExpectAck(BigEndianWithChecksum(crcInitialValue), nameof(GetChecksum), token);

            // ACK, then the CRC value MSB first, then the XOR of those four bytes.
            var result = await _transceiver.ReceiveExactly(6, token);
            EnsureAck(result.Span, nameof(GetChecksum));

            var crc = result.Slice(1, 4).ToArray();
            var receivedChecksum = result.Span[5];
            var expectedChecksum = (byte)(crc[0] ^ crc[1] ^ crc[2] ^ crc[3]);
            if (receivedChecksum != expectedChecksum)
            {
                throw new InvalidOperationException($"GetChecksum: the device returned a CRC whose checksum did not match. Expected 0x{expectedChecksum:X2}, got 0x{receivedChecksum:X2}");
            }

            return ((uint)crc[0] << 24) | ((uint)crc[1] << 16) | ((uint)crc[2] << 8) | crc[3];
        }

        /// <summary>
        /// Sends a frame and reads the single status byte the device answers with, failing if it
        /// is not an ACK.
        /// </summary>
        private async Task SendAndExpectAck(byte[] frame, string commandName, CancellationToken token)
        {
            var result = await _transceiver.SendReceiveExactly(frame, 1, token);
            EnsureAck(result.Span, commandName);
        }

        /// <summary>
        /// Reads a single status byte the device sends unprompted — the second ACK of a command
        /// that reports completion of a long-running operation — failing if it is not an ACK.
        /// </summary>
        private async Task ExpectAck(string commandName, CancellationToken token)
        {
            var result = await _transceiver.ReceiveExactly(1, token);
            EnsureAck(result.Span, commandName);
        }

        private static void EnsureAck(ReadOnlySpan<byte> response, string commandName)
        {
            if (response.Length == 0)
            {
                throw new InvalidOperationException($"{commandName}: the device sent no status byte");
            }
            if (response[0] == Ack)
            {
                return;
            }
            if (response[0] == Nack)
            {
                throw new InvalidOperationException($"{commandName}: the device answered NACK (0x{Nack:X2})");
            }
            throw new InvalidOperationException($"{commandName}: expected ACK (0x{Ack:X2}) or NACK (0x{Nack:X2}), got 0x{response[0]:X2}");
        }

        /// <summary>
        /// Encodes a 32-bit value big-endian and appends the XOR of the four bytes, which is the
        /// frame shape AN3155 uses for every 32-bit parameter.
        /// </summary>
        private static byte[] BigEndianWithChecksum(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return new byte[] { bytes[0], bytes[1], bytes[2], bytes[3], (byte)(bytes[0] ^ bytes[1] ^ bytes[2] ^ bytes[3]) };
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

    /// <summary>
    /// The response to Get Version &amp; Read Protection Status (AN3155 command 0x01).
    /// </summary>
    public class Stm32VersionInfo
    {
        /// <summary>
        /// Bootloader protocol version, packed as one BCD-style byte: 0x10 is version 1.0.
        /// </summary>
        public byte Version { get; }

        /// <summary>Legacy option byte 1. Reads 0x00 on current parts.</summary>
        public byte OptionByte1 { get; }

        /// <summary>Legacy option byte 2. Reads 0x00 on current parts.</summary>
        public byte OptionByte2 { get; }

        /// <summary>High nibble of <see cref="Version"/>.</summary>
        public byte MajorVersion => (byte)(Version >> 4);

        /// <summary>Low nibble of <see cref="Version"/>.</summary>
        public byte MinorVersion => (byte)(Version & 0x0F);

        public Stm32VersionInfo(byte version, byte optionByte1, byte optionByte2)
        {
            Version = version;
            OptionByte1 = optionByte1;
            OptionByte2 = optionByte2;
        }
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
