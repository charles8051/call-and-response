using Moq;
using CallAndResponse.Protocol.Stm32Bootloader;

namespace CallAndResponse.Test.Unit;

public class Stm32BootloaderClientTests
{
    private const byte Ack = 0x79;
    private const byte Nack = 0x1F;

    [Fact]
    public async Task Ping_ReturnsTrue_WhenAckReceived()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { Ack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        var result = await client.Ping();

        Assert.True(result);
    }

    [Fact]
    public async Task Ping_ReturnsFalse_WhenNackReceived()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { Nack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        var result = await client.Ping();

        Assert.False(result);
    }

    [Fact]
    public async Task Ping_ThrowsOperationCanceledException_WhenUnknownByteReceived()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0xAA }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Ping());
    }

    [Fact]
    public async Task Special_ReturnsByteFromTransceiver()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceiveExactly(
                It.IsAny<ReadOnlyMemory<byte>>(),
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x42 }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        var result = await client.Special();

        Assert.Equal(0x42, result);
    }

    [Fact]
    public async Task GetProtocolVersion_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.GetProtocolVersion());
    }

    [Fact]
    public async Task Open_DelegatesToTransceiver()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.Open(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var client = new Stm32BootloaderClient(mock.Object);
        await client.Open();

        mock.Verify(t => t.Open(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Close_DelegatesToTransceiver()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.Close(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var client = new Stm32BootloaderClient(mock.Object);
        await client.Close();

        mock.Verify(t => t.Close(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSupportedCommands_ParsesProtocolVersionAndCommands()
    {
        var mock = new Mock<ITransceiver>();
        // Payload: ACK, version byte, command list, ACK (header/footer stripped, only payload returned)
        // The SendReceiveHeaderFooter returns the payload between header and footer
        // Response format: ACK | version | num_commands | cmd1 | cmd2 ... | ACK
        // After header (ACK) and footer (ACK) stripping, we get: version | num_commands | cmd1 | cmd2...
        var responsePayload = new byte[]
        {
            0x0B,                                           // N (11 bytes of commands follow)
            0x11,                                           // protocol version
            (byte)Stm32BootloaderCommand.Get,
            (byte)Stm32BootloaderCommand.GetVersion,
            (byte)Stm32BootloaderCommand.GetId,
            (byte)Stm32BootloaderCommand.ReadMemory,
            (byte)Stm32BootloaderCommand.Go,
            (byte)Stm32BootloaderCommand.WriteMemory,
            (byte)Stm32BootloaderCommand.ExtendedEraseMemory,
            (byte)Stm32BootloaderCommand.Special,
            (byte)Stm32BootloaderCommand.WriteProtect,
            (byte)Stm32BootloaderCommand.WriteUnprotect,
            (byte)Stm32BootloaderCommand.ReadoutProtect,
            (byte)Stm32BootloaderCommand.ReadoutUnprotect,
        };

        mock.Setup(t => t.SendReceiveHeaderFooter(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(responsePayload.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        var info = await client.GetSupportedCommands();

        Assert.Equal(0x11, info.ProtocolVersion);
        Assert.Contains(Stm32BootloaderCommand.ReadMemory, info.SupportedCommands);
        Assert.Contains(Stm32BootloaderCommand.WriteMemory, info.SupportedCommands);
    }

    [Fact]
    public async Task GetSupportedCommands_UnknownCommand_ThrowsInvalidOperationException()
    {
        var mock = new Mock<ITransceiver>();
        var responsePayload = new byte[]
        {
            0x10,   // version
            0x01,   // N
            0xFF,   // unknown command code
        };
        mock.Setup(t => t.SendReceiveHeaderFooter(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(responsePayload.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSupportedCommands());
    }

    [Fact]
    public async Task ReadMemory_SingleChunk_ReturnsData()
    {
        var mock = new Mock<ITransceiver>();
        var callCount = 0;
        mock.Setup(t => t.SendReceiveExactly(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReadOnlyMemory<byte> writeBytes, int numBytes, CancellationToken token) =>
            {
                callCount++;
                if (callCount == 1) return new byte[] { Ack }.AsMemory();           // initiate command ack
                if (callCount == 2) return new byte[] { Ack }.AsMemory();           // address ack
                // read data + ack prefix
                var data = new byte[numBytes];
                data[0] = Ack;
                for (int i = 1; i < numBytes; i++) data[i] = (byte)i;
                return data.AsMemory();
            });

        var client = new Stm32BootloaderClient(mock.Object);
        var result = await client.ReadMemory(Stm32BootloaderClient.Stm32BaseAddress, 4);

        Assert.Equal(4, result.Length);
    }

    [Fact]
    public async Task WriteMemory_SingleChunk_SendsCorrectCommands()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceivePerfectMatch(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { Ack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await client.WriteMemory(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Stm32BootloaderClient.Stm32BaseAddress);

        // Should call SendReceivePerfectMatch 3 times: command, address, data
        mock.Verify(t => t.SendReceivePerfectMatch(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Go_SendsCorrectCommands()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceivePerfectMatch(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { Ack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await client.Go();

        // Should call SendReceivePerfectMatch twice: command init + address
        mock.Verify(t => t.SendReceivePerfectMatch(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Go_SendsGoCommandByte()
    {
        var mock = new Mock<ITransceiver>();
        ReadOnlyMemory<byte>? capturedFirst = null;
        mock.Setup(t => t.SendReceivePerfectMatch(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, CancellationToken>((w, m, ct) =>
            {
                capturedFirst ??= w;
            })
            .ReturnsAsync(new byte[] { Ack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await client.Go();

        Assert.NotNull(capturedFirst);
        Assert.Equal((byte)Stm32BootloaderCommand.Go, capturedFirst!.Value.Span[0]);
    }

    [Fact]
    public async Task ExtendedEraseMemoryPages_SendsCorrectCommands()
    {
        var mock = new Mock<ITransceiver>();
        mock.Setup(t => t.SendReceivePerfectMatch(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { Ack }.AsMemory());

        var client = new Stm32BootloaderClient(mock.Object);
        await client.ExtendedEraseMemoryPages(2);

        // Should call SendReceivePerfectMatch twice: command + page data
        mock.Verify(t => t.SendReceivePerfectMatch(
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EraseMemory_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.EraseMemory(0, 0));
    }

    [Fact]
    public async Task WriteProtect_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.WriteProtect());
    }

    [Fact]
    public async Task WriteUnprotect_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.WriteUnprotect());
    }

    [Fact]
    public async Task ReadoutProtect_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.ReadoutProtect());
    }

    [Fact]
    public async Task ReadoutUnprotect_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.ReadoutUnprotect());
    }

    [Fact]
    public async Task GetChecksum_ThrowsNotImplementedException()
    {
        var mock = new Mock<ITransceiver>();
        var client = new Stm32BootloaderClient(mock.Object);
        await Assert.ThrowsAsync<NotImplementedException>(() => client.GetChecksum());
    }
}
