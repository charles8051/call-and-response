using Moq;
using CallAndResponse.Protocol.Modbus;

namespace CallAndResponse.Test.Unit;

public class ModbusRtuClientTests
{
    private static Memory<byte> BuildReadHoldingRegistersResponse(byte unitId, ushort numRegisters, byte[] registerData)
    {
        // Response: unit id (1) + FC (1) + byte count (1) + data (2*n) + CRC (2)
        var response = new List<byte>
        {
            unitId,
            (byte)ModbusFunctionCode.ReadHoldingRegisters,
            (byte)(numRegisters * 2)
        };
        response.AddRange(registerData);
        // Append dummy CRC
        response.Add(0x00);
        response.Add(0x00);
        return response.ToArray().AsMemory();
    }

    private static Memory<byte> BuildWriteRegistersResponse(byte unitId, ushort startingAddress, ushort numRegisters)
    {
        // Response: unit id (1) + FC (1) + starting address (2) + quantity (2) + CRC (2) = 8 bytes
        var response = new List<byte>
        {
            unitId,
            (byte)ModbusFunctionCode.WriteMultipleRegisters,
            (byte)(startingAddress >> 8),
            (byte)(startingAddress & 0xFF),
            (byte)(numRegisters >> 8),
            (byte)(numRegisters & 0xFF),
            0x00,
            0x00
        };
        return response.ToArray().AsMemory();
    }

    private static Memory<byte> BuildErrorResponse(byte unitId, ModbusFunctionCode fc, ModbusProtocolExceptionCode exceptionCode)
    {
        // Error response: unit id (1) + FC|0x80 (1) + exception code (1) + CRC (2) = 5 bytes
        return new byte[]
        {
            unitId,
            (byte)((byte)fc | 0x80),
            (byte)exceptionCode,
            0x00,
            0x00
        }.AsMemory();
    }

    [Fact]
    public async Task ReadHoldingRegisters_ValidResponse_ReturnsFlippedData()
    {
        var mock = new Mock<ITransceiver>();
        // Registers are sent big-endian by device, response has 0x01,0x02 which flips to 0x02,0x01
        var registerData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildReadHoldingRegistersResponse(0x01, 2, registerData));

        var client = new ModbusRtuClient(mock.Object);
        var result = await client.ReadHoldingRegisters(0x01, 0x0000, (ushort)2);

        Assert.Equal(new byte[] { 0x02, 0x01, 0x04, 0x03 }, result.ToArray());
    }

    [Fact]
    public async Task ReadHoldingRegisters_UnitIdMismatch_ThrowsModbusFramingException()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildReadHoldingRegistersResponse(0x02, 1, new byte[] { 0x00, 0x00 });
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ModbusFramingException>(() =>
            client.ReadHoldingRegisters(0x01, 0x0000, (ushort)1));
    }

    [Fact]
    public async Task ReadHoldingRegisters_FunctionCodeMismatch_ThrowsModbusFramingException()
    {
        var mock = new Mock<ITransceiver>();
        // Return response with wrong function code
        var response = new byte[]
        {
            0x01,
            (byte)ModbusFunctionCode.WriteMultipleRegisters,
            0x02,
            0x00, 0x00,
            0x00, 0x00
        }.AsMemory();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ModbusFramingException>(() =>
            client.ReadHoldingRegisters(0x01, 0x0000, (ushort)1));
    }

    [Fact]
    public async Task ReadHoldingRegisters_ErrorBitSet_ThrowsModbusProtocolException()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildErrorResponse(0x01, ModbusFunctionCode.ReadHoldingRegisters, ModbusProtocolExceptionCode.IllegalDataAddress);
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        var ex = await Assert.ThrowsAsync<ModbusProtocolException>(() =>
            client.ReadHoldingRegisters(0x01, 0x0000, (ushort)1));

        Assert.Equal(ModbusProtocolExceptionCode.IllegalDataAddress, ex.ExceptionCode);
    }

    [Fact]
    public async Task ReadHoldingRegisters_TransceiverThrows_ThrowsModbusTransportException()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransceiverTransportException("port error"));

        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ModbusTransportException>(() =>
            client.ReadHoldingRegisters(0x01, 0x0000, (ushort)1));
    }

    [Fact]
    public async Task ReadHoldingRegisters_OddByteCount_ThrowsArgumentException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ReadHoldingRegisters(0x01, 0x0000, numBytes: 3));
    }

    [Fact]
    public async Task ReadHoldingRegisters_EvenByteCount_ForwardsToRegisterOverload()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildReadHoldingRegistersResponse(0x01, 1, new byte[] { 0x00, 0x01 });
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        var result = await client.ReadHoldingRegisters(0x01, 0x0000, numBytes: 2);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task WriteRegisters_ValidResponse_Succeeds()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildWriteRegistersResponse(0x01, 0x0000, 1);
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        await client.WriteRegisters(0x01, 0x0000, new byte[] { 0x01, 0x02 });

        mock.Verify(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 8, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteRegisters_UnitIdMismatch_ThrowsModbusFramingException()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildWriteRegistersResponse(0x02, 0x0000, 1);
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ModbusFramingException>(() =>
            client.WriteRegisters(0x01, 0x0000, new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public async Task WriteRegisters_ErrorBitSet_ThrowsModbusProtocolException()
    {
        var mock = new Mock<ITransceiver>();
        var response = BuildErrorResponse(0x01, ModbusFunctionCode.WriteMultipleRegisters, ModbusProtocolExceptionCode.IllegalFunction);
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var client = new ModbusRtuClient(mock.Object);
        var ex = await Assert.ThrowsAsync<ModbusProtocolException>(() =>
            client.WriteRegisters(0x01, 0x0000, new byte[] { 0x01, 0x02 }));

        Assert.Equal(ModbusProtocolExceptionCode.IllegalFunction, ex.ExceptionCode);
    }

    [Fact]
    public async Task WriteRegisters_TransceiverThrows_ThrowsModbusTransportException()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransceiverTransportException("port error"));

        var client = new ModbusRtuClient(mock.Object);
        await Assert.ThrowsAsync<ModbusTransportException>(() =>
            client.WriteRegisters(0x01, 0x0000, new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public async Task Open_DelegatesToTransceiver()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.Open(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var client = new ModbusRtuClient(mock.Object);
        await client.Open();

        mock.Verify(t => t.Open(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Close_DelegatesToTransceiver()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.Close(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var client = new ModbusRtuClient(mock.Object);
        await client.Close();

        mock.Verify(t => t.Close(It.IsAny<CancellationToken>()), Times.Once);
    }
}
