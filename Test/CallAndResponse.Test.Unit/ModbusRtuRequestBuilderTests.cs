using CallAndResponse.Protocol.Modbus;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

public class ModbusRtuRequestBuilderTests
{
    // -------------------------------------------------------------------------
    // Helper: independent CRC-16/Modbus implementation for cross-validation
    // -------------------------------------------------------------------------
    private static ushort ComputeModbusCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc >>= 1;
                    crc ^= 0xA001;
                }
                else
                {
                    crc >>= 1;
                }
            }
        }
        return crc;
    }

    private static byte[] FrameWithoutCrc(byte[] frame) => frame[..^2];
    private static ushort CrcFromFrame(byte[] frame) =>
        (ushort)(frame[^2] | (frame[^1] << 8)); // little-endian

    // -------------------------------------------------------------------------
    // FC03 – Read Holding Registers
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_FC03_HasCorrectLength()
    {
        var frame = BuildFC03(unitId: 1, address: 0x0000, quantity: 1);

        // 1 (unit id) + 1 (FC) + 2 (address) + 2 (quantity) + 2 (CRC)
        frame.Should().HaveCount(8);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0xFF)]
    [InlineData(0x10)]
    public void Build_FC03_UnitIdIsFirstByte(byte unitId)
    {
        var frame = BuildFC03(unitId: unitId, address: 0x0000, quantity: 1);

        frame[0].Should().Be(unitId);
    }

    [Fact]
    public void Build_FC03_FunctionCodeIsSecondByte()
    {
        var frame = BuildFC03(unitId: 1, address: 0x0000, quantity: 1);

        frame[1].Should().Be((byte)ModbusFunctionCode.ReadHoldingRegisters);
    }

    [Theory]
    [InlineData(0x0000, 0x00, 0x00)]
    [InlineData(0x006B, 0x00, 0x6B)]
    [InlineData(0xABCD, 0xAB, 0xCD)]
    public void Build_FC03_AddressIsBigEndian(ushort address, byte expectedHigh, byte expectedLow)
    {
        var frame = BuildFC03(unitId: 1, address: address, quantity: 1);

        frame[2].Should().Be(expectedHigh, "high byte of address");
        frame[3].Should().Be(expectedLow, "low byte of address");
    }

    [Theory]
    [InlineData(0x0001, 0x00, 0x01)]
    [InlineData(0x0003, 0x00, 0x03)]
    [InlineData(0x0064, 0x00, 0x64)]
    public void Build_FC03_QuantityIsBigEndian(ushort quantity, byte expectedHigh, byte expectedLow)
    {
        var frame = BuildFC03(unitId: 1, address: 0x0000, quantity: quantity);

        frame[4].Should().Be(expectedHigh, "high byte of quantity");
        frame[5].Should().Be(expectedLow, "low byte of quantity");
    }

    [Fact]
    public void Build_FC03_CrcMatchesExpectedAlgorithm()
    {
        var frame = BuildFC03(unitId: 1, address: 0x006B, quantity: 3);

        var expectedCrc = ComputeModbusCrc(FrameWithoutCrc(frame));
        var actualCrc = CrcFromFrame(frame);

        actualCrc.Should().Be(expectedCrc);
    }

    /// <summary>
    /// Validates the CRC for [01 03 00 6B 00 03].
    /// Note: the commonly cited 0x7687 vector is for slave address 0x11, not 0x01.
    /// For unit id 0x01, the CRC is 0x1774 (stored little-endian as [0x74, 0x17]).
    /// </summary>
    [Fact]
    public void Build_FC03_CrcMatchesKnownTestVector()
    {
        var frame = BuildFC03(unitId: 0x01, address: 0x006B, quantity: 0x0003);

        // CRC 0x1774 stored little-endian
        frame[^2].Should().Be(0x74, "CRC low byte");
        frame[^1].Should().Be(0x17, "CRC high byte");
    }

    // -------------------------------------------------------------------------
    // FC16 – Write Multiple Registers
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_FC16_HasCorrectLength()
    {
        // 1 (unit) + 1 (FC) + 2 (address) + 2 (qty) + 1 (byte count) + 4 (data) + 2 (CRC) = 13
        var frame = BuildFC16(unitId: 1, address: 0x0000, data: new byte[] { 0x01, 0x02, 0x03, 0x04 });

        frame.Should().HaveCount(13);
    }

    [Fact]
    public void Build_FC16_FunctionCodeIsSecondByte()
    {
        var frame = BuildFC16(unitId: 1, address: 0x0000, data: new byte[] { 0x00, 0x01 });

        frame[1].Should().Be((byte)ModbusFunctionCode.WriteMultipleRegisters);
    }

    [Fact]
    public void Build_FC16_ByteCountFieldIsDataLength()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = BuildFC16(unitId: 1, address: 0x0000, data: data);

        // Byte count field is at index 6
        frame[6].Should().Be((byte)data.Length);
    }

    [Fact]
    public void Build_FC16_DataBytePairsAreSwapped()
    {
        // Input data: [0xAA, 0xBB, 0xCC, 0xDD]
        // Each 16-bit word is stored big-endian in Modbus, but the builder
        // takes data in little-endian (low byte first) and swaps each pair.
        var frame = BuildFC16(unitId: 1, address: 0x0000, data: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        // Bytes 7-10 are the data payload (after: unit, FC, addr hi, addr lo, qty hi, qty lo, byte count)
        frame[7].Should().Be(0xBB, "high byte of first word");
        frame[8].Should().Be(0xAA, "low byte of first word");
        frame[9].Should().Be(0xDD, "high byte of second word");
        frame[10].Should().Be(0xCC, "low byte of second word");
    }

    [Fact]
    public void Build_FC16_CrcMatchesExpectedAlgorithm()
    {
        var frame = BuildFC16(unitId: 1, address: 0x0001, data: new byte[] { 0x00, 0x0A });

        var expectedCrc = ComputeModbusCrc(FrameWithoutCrc(frame));
        var actualCrc = CrcFromFrame(frame);

        actualCrc.Should().Be(expectedCrc);
    }

    [Fact]
    public void Build_FC16_QuantityFieldIsNumberOfRegisters()
    {
        // 4 bytes of data = 2 registers
        var frame = BuildFC16(unitId: 1, address: 0x0000, data: new byte[] { 0x01, 0x02, 0x03, 0x04 });

        frame[4].Should().Be(0x00, "quantity high byte");
        frame[5].Should().Be(0x02, "quantity low byte (2 registers)");
    }

    [Fact]
    public void Build_FC16_ThrowsWhenDataIsOddLength()
    {
        var act = () => BuildFC16(unitId: 1, address: 0x0000, data: new byte[] { 0x01, 0x02, 0x03 });

        act.Should().Throw<InvalidOperationException>();
    }

    // -------------------------------------------------------------------------
    // Unsupported function code
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ModbusFunctionCode.ReadCoils)]
    [InlineData(ModbusFunctionCode.ReadDiscreteInputs)]
    public void Build_UnsupportedFunctionCode_Throws(ModbusFunctionCode unsupportedFc)
    {
        var act = () => new ModbusRtuRequestBuilder()
            .SetUnitIdentifier(1)
            .SetFunctionCode(unsupportedFc)
            .SetStartingAddress(0)
            .SetNumItems(1)
            .Build();

        act.Should().Throw<InvalidOperationException>();
    }

    // -------------------------------------------------------------------------
    // Private builder helpers
    // -------------------------------------------------------------------------

    private static byte[] BuildFC03(byte unitId, ushort address, ushort quantity) =>
        new ModbusRtuRequestBuilder()
            .SetUnitIdentifier(unitId)
            .SetFunctionCode(ModbusFunctionCode.ReadHoldingRegisters)
            .SetStartingAddress(address)
            .SetNumItems(quantity)
            .Build()
            .ToArray();

    private static byte[] BuildFC16(byte unitId, ushort address, byte[] data) =>
        new ModbusRtuRequestBuilder()
            .SetUnitIdentifier(unitId)
            .SetFunctionCode(ModbusFunctionCode.WriteMultipleRegisters)
            .SetStartingAddress(address)
            .SetNumItems((ushort)(data.Length / 2))
            .SetData(data)
            .Build()
            .ToArray();
}
