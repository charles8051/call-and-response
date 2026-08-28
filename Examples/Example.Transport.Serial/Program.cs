using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;
using RJCP.IO.Ports;

// -- Configure the serial port --
using var port = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One);
port.Open();

// -- Create a transceiver from the open port --
await using var pipe = new SerialDuplexPipe(port);

var transceiver = new Transceiver(pipe);

// -- Use it with a protocol client --
var modbus = new ModbusRtuClient(transceiver);

using var cts = new CancellationTokenSource(5000);

try
{
    var registers = await modbus.ReadHoldingRegisters(
        unitIdentifier: 1,
        startingAddress: 0,
        numRegisters: 10,
        cts.Token
    );

    Console.WriteLine($"Read {registers.Length} bytes:");
    Console.WriteLine(BitConverter.ToString(registers.ToArray()));
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timed out waiting for response.");
}
catch (ModbusProtocolException ex)
{
    Console.WriteLine($"Modbus error: {ex.ExceptionCode}");
}
