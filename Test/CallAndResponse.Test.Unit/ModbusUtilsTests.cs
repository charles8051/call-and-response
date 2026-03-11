using CallAndResponse.Protocol.Modbus;

namespace CallAndResponse.Test.Unit;

public class ModbusUtilsTests
{
    [Fact]
    public void Flip16BitValues_SwapsEachPairOfBytes()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 }.AsMemory();
        var result = data.Flip16BitValues();
        Assert.Equal(new byte[] { 0x02, 0x01, 0x04, 0x03 }, result.ToArray());
    }

    [Fact]
    public void Flip16BitValues_SinglePair_SwapsBytes()
    {
        var data = new byte[] { 0xAB, 0xCD }.AsMemory();
        var result = data.Flip16BitValues();
        Assert.Equal(new byte[] { 0xCD, 0xAB }, result.ToArray());
    }

    [Fact]
    public void Flip16BitValues_EmptyArray_ReturnsEmpty()
    {
        var data = Array.Empty<byte>().AsMemory();
        var result = data.Flip16BitValues();
        Assert.Empty(result.ToArray());
    }

    [Fact]
    public void Flip16BitValues_OddLength_ThrowsArgumentException()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 }.AsMemory();
        Assert.Throws<ArgumentException>(() => data.Flip16BitValues());
    }

    [Fact]
    public void Flip16BitValues_IdenticalBytes_ReturnsSameValues()
    {
        var data = new byte[] { 0xFF, 0xFF, 0x00, 0x00 }.AsMemory();
        var result = data.Flip16BitValues();
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, result.ToArray());
    }
}
