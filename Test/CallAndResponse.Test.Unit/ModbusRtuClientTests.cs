using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

/// <summary>
/// Tier 3 — tests <see cref="ModbusRtuClient"/> end-to-end using <see cref="FakeDuplexPipe"/>.
/// No I/O; no mocking. The fake delivers pre-enqueued response bytes through the
/// real <see cref="Transceiver"/> pipe-based accumulation loop.
/// </summary>
public class ModbusRtuClientTests
{
    private static CancellationToken Token(int ms = 2000) =>
        new CancellationTokenSource(ms).Token;

    // RTU frames on the inter-frame gap, so the fake has to be allowed to go quiet. The gap is
    // short here because nothing follows it; a real link derives it from the baud rate.
    private static IMessageTransceiver Channel(FakeDuplexPipe pipe) =>
        pipe.AsTransceiver().WithFraming(ModbusRtu.Codec(TimeSpan.FromMilliseconds(20)));

    // A test oracle for the CRC the codec now checks. Deliberately a second implementation
    // rather than a call into the codec, so a wrong polynomial fails instead of agreeing.
    private static byte[] WithCrc(List<byte> frame)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in frame)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        frame.Add((byte)(crc & 0xFF));
        frame.Add((byte)(crc >> 8));
        return frame.ToArray();
    }

    // FC03 response: unit id (1) + FC (1) + byte count (1) + data (n) + CRC (2)
    private static byte[] BuildFC03Response(byte unitId, byte[] data)
    {
        var frame = new List<byte> { unitId, 0x03, (byte)data.Length };
        frame.AddRange(data);
        return WithCrc(frame);
    }

    // Exception response: unit id (1) + FC with the error bit set (1) + code (1) + CRC (2).
    // Five bytes, shorter than any success response — which is the whole point: under the old
    // length-based framing this frame never completed and the call hung until cancellation.
    private static byte[] BuildExceptionResponse(byte unitId, byte functionCode, ModbusProtocolExceptionCode code) =>
        WithCrc(new List<byte> { unitId, (byte)(functionCode | 0x80), (byte)code });

    // FC16 response: unit id (1) + FC (1) + address (2) + quantity (2) + CRC (2) = 8 bytes
    private static byte[] BuildFC16Response(byte unitId, ushort address, ushort quantity) =>
        WithCrc(new List<byte>
        {
            unitId, 0x10,
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(quantity >> 8), (byte)(quantity & 0xFF),
        });

    // =========================================================================
    // ReadHoldingRegisters — frame construction
    // =========================================================================

    [Fact]
    public async Task ReadHoldingRegisters_SendsFC03FrameWithCorrectFields()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildFC03Response(0x01, new byte[] { 0x00, 0x0A, 0x00, 0x0B }));

        var client = new ModbusRtuClient(Channel(pipe));
        await client.ReadHoldingRegisters(unitIdentifier: 0x01, startingAddress: 0x006B, numRegisters: 2, Token());

        var frame = pipe.SentBytes.ToArray();
        frame[0].Should().Be(0x01, "unit identifier");
        frame[1].Should().Be((byte)ModbusFunctionCode.ReadHoldingRegisters, "function code");
        frame[2].Should().Be(0x00, "address high byte");
        frame[3].Should().Be(0x6B, "address low byte");
        frame[4].Should().Be(0x00, "quantity high byte");
        frame[5].Should().Be(0x02, "quantity low byte");
    }

    [Theory]
    [InlineData(0x0001, 0x00, 0x01)]
    [InlineData(0x0064, 0x00, 0x64)]
    [InlineData(0xABCD, 0xAB, 0xCD)]
    public async Task ReadHoldingRegisters_AddressEncodedBigEndian(ushort address, byte expectedHi, byte expectedLo)
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildFC03Response(0x01, new byte[] { 0x00, 0x00 }));

        var client = new ModbusRtuClient(Channel(pipe));
        await client.ReadHoldingRegisters(0x01, address, numRegisters: 1, Token());

        pipe.SentBytes.ToArray()[2].Should().Be(expectedHi, "address high byte");
        pipe.SentBytes.ToArray()[3].Should().Be(expectedLo, "address low byte");
    }

    // =========================================================================
    // ReadHoldingRegisters — response parsing
    // =========================================================================

    [Fact]
    public async Task ReadHoldingRegisters_ReturnsFlippedPayload()
    {
        var pipe = new FakeDuplexPipe();
        // Device responds with two big-endian registers: [0x00, 0x0A] and [0x00, 0x0B]
        pipe.EnqueueRx(BuildFC03Response(0x01, new byte[] { 0x00, 0x0A, 0x00, 0x0B }));

        var client = new ModbusRtuClient(Channel(pipe));
        var result = await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 2, Token());

        // After Flip16BitValues the pairs are swapped to little-endian
        result.ToArray().Should().Equal(0x0A, 0x00, 0x0B, 0x00);
    }

    [Fact]
    public async Task ReadHoldingRegisters_RequestsExactByteCount()
    {
        var pipe = new FakeDuplexPipe();
        // 5 overhead + 2 * 2 registers = 9 bytes
        pipe.EnqueueRx(BuildFC03Response(0x01, new byte[] { 0x00, 0x0A, 0x00, 0x0B }));

        var client = new ModbusRtuClient(Channel(pipe));
        var result = await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 2, Token());

        // Result has 4 data bytes (2 registers * 2 bytes each); overhead bytes consumed by SendReceiveExactly
        result.Length.Should().Be(4, "two registers = 4 data bytes after stripping overhead");
    }

    // =========================================================================
    // ReadHoldingRegisters — error handling
    // =========================================================================

    [Fact]
    public async Task ReadHoldingRegisters_ErrorBitSet_ThrowsModbusProtocolException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildExceptionResponse(0x01, 0x03, ModbusProtocolExceptionCode.IllegalDataAddress));

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 1, Token());

        await act.Should()
            .ThrowAsync<ModbusProtocolException>()
            .Where(ex => ex.ExceptionCode == ModbusProtocolExceptionCode.IllegalDataAddress);
    }

    [Theory]
    [InlineData(ModbusProtocolExceptionCode.IllegalFunction)]
    [InlineData(ModbusProtocolExceptionCode.IllegalDataAddress)]
    [InlineData(ModbusProtocolExceptionCode.IllegalDataValue)]
    [InlineData(ModbusProtocolExceptionCode.ServerDeviceFailure)]
    public async Task ReadHoldingRegisters_ErrorCode_PropagatedInException(ModbusProtocolExceptionCode code)
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildExceptionResponse(0x01, 0x03, code));

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 1, Token());

        await act.Should()
            .ThrowAsync<ModbusProtocolException>()
            .Where(ex => ex.ExceptionCode == code);
    }

    [Fact]
    public async Task ReadHoldingRegisters_UnitIdMismatch_ThrowsModbusFramingException()
    {
        var pipe = new FakeDuplexPipe();
        // Response carries unit id 0x02, but the request was for unit 0x01
        pipe.EnqueueRx(BuildFC03Response(unitId: 0x02, new byte[] { 0x00, 0x0A }));

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 1, Token());

        await act.Should().ThrowAsync<ModbusFramingException>();
    }

    [Fact]
    public async Task ReadHoldingRegisters_FunctionCodeMismatch_ThrowsModbusFramingException()
    {
        var pipe = new FakeDuplexPipe();
        // Response echoes FC16 (0x10) instead of FC03 (0x03)
        pipe.EnqueueRx(0x01, 0x10, 0x02, 0x00, 0x0A, 0x00, 0x00);

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.ReadHoldingRegisters(0x01, 0x0000, numRegisters: 1, Token());

        await act.Should().ThrowAsync<ModbusFramingException>();
    }

    // =========================================================================
    // WriteRegisters — frame construction
    // =========================================================================

    [Fact]
    public async Task WriteRegisters_SendsFC16FrameWithCorrectFields()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildFC16Response(0x01, 0x0001, 2));

        var client = new ModbusRtuClient(Channel(pipe));
        await client.WriteRegisters(0x01, 0x0001, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, Token());

        var frame = pipe.SentBytes.ToArray();
        frame[0].Should().Be(0x01, "unit identifier");
        frame[1].Should().Be((byte)ModbusFunctionCode.WriteMultipleRegisters, "function code");
        frame[2].Should().Be(0x00, "address high byte");
        frame[3].Should().Be(0x01, "address low byte");
        frame[4].Should().Be(0x00, "quantity high byte");
        frame[5].Should().Be(0x02, "quantity low byte (4 bytes = 2 registers)");
    }

    [Fact]
    public async Task WriteRegisters_ByteCountFieldMatchesDataLength()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildFC16Response(0x01, 0x0000, 2));

        var client = new ModbusRtuClient(Channel(pipe));
        await client.WriteRegisters(0x01, 0x0000, new byte[] { 0x01, 0x02, 0x03, 0x04 }, Token());

        // byte count field is at index 6
        pipe.SentBytes.ToArray()[6].Should().Be(4, "byte count equals data length");
    }

    // =========================================================================
    // WriteRegisters — error handling
    // =========================================================================

    [Fact]
    public async Task WriteRegisters_ErrorBitSet_ThrowsModbusProtocolException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildExceptionResponse(0x01, 0x10, ModbusProtocolExceptionCode.IllegalFunction));

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.WriteRegisters(0x01, 0x0000, new byte[] { 0x00, 0x01 }, Token());

        await act.Should()
            .ThrowAsync<ModbusProtocolException>()
            .Where(ex => ex.ExceptionCode == ModbusProtocolExceptionCode.IllegalFunction);
    }

    [Fact]
    public async Task WriteRegisters_UnitIdMismatch_ThrowsModbusFramingException()
    {
        var pipe = new FakeDuplexPipe();
        pipe.EnqueueRx(BuildFC16Response(unitId: 0x02, 0x0000, 1));

        var client = new ModbusRtuClient(Channel(pipe));
        var act = async () => await client.WriteRegisters(0x01, 0x0000, new byte[] { 0x00, 0x01 }, Token());

        await act.Should().ThrowAsync<ModbusFramingException>();
    }
}
