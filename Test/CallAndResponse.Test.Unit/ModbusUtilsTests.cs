using CallAndResponse.Protocol.Modbus;
using FluentAssertions;

namespace CallAndResponse.Test.Unit;

public class ModbusUtilsTests
{
    [Fact]
    public void Flip16BitValues_SwapsEachBytePair()
    {
        Memory<byte> data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var result = data.Flip16BitValues();

        result.ToArray().Should().Equal(0x02, 0x01, 0x04, 0x03);
    }

    [Fact]
    public void Flip16BitValues_SinglePair_SwapsBytes()
    {
        Memory<byte> data = new byte[] { 0xAA, 0xBB };

        var result = data.Flip16BitValues();

        result.ToArray().Should().Equal(0xBB, 0xAA);
    }

    [Fact]
    public void Flip16BitValues_AllZeros_RemainsUnchanged()
    {
        Memory<byte> data = new byte[] { 0x00, 0x00, 0x00, 0x00 };

        var result = data.Flip16BitValues();

        result.ToArray().Should().Equal(0x00, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void Flip16BitValues_IdenticalPairBytes_RemainsUnchanged()
    {
        Memory<byte> data = new byte[] { 0xFF, 0xFF };

        var result = data.Flip16BitValues();

        result.ToArray().Should().Equal(0xFF, 0xFF);
    }

    [Fact]
    public void Flip16BitValues_IsItsOwnInverse()
    {
        var original = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Memory<byte> data = original.ToArray();

        data.Flip16BitValues().Flip16BitValues();

        data.ToArray().Should().Equal(original);
    }

    [Fact]
    public void Flip16BitValues_EmptySpan_ReturnsEmpty()
    {
        Memory<byte> data = Array.Empty<byte>();

        var result = data.Flip16BitValues();

        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Flip16BitValues_OddLength_ThrowsArgumentException()
    {
        Memory<byte> data = new byte[] { 0x01, 0x02, 0x03 };

        var act = () => data.Flip16BitValues();

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01 }, new byte[] { 0x01, 0x00 })]
    [InlineData(new byte[] { 0x12, 0x34 }, new byte[] { 0x34, 0x12 })]
    [InlineData(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, new byte[] { 0xAD, 0xDE, 0xEF, 0xBE })]
    public void Flip16BitValues_Theory_ProducesExpectedResult(byte[] input, byte[] expected)
    {
        Memory<byte> data = input;

        var result = data.Flip16BitValues();

        result.ToArray().Should().Equal(expected);
    }
}
