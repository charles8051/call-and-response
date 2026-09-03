using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Test.Unit.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallAndResponse.Test.Unit;

public class TypedLoggerSupportTests
{
    [Fact]
    public void ModbusRtuClient_TypedLoggerConstructor_CreatesInstance()
    {
        var pipe = new FakeDuplexPipe();
        var channel = ModbusRtu.Channel(pipe.AsTransceiver(), TimeSpan.FromMilliseconds(20));

        var sut = new ModbusRtuClient(channel, NullLogger<ModbusRtuClient>.Instance);

        sut.Should().NotBeNull();
    }
}
