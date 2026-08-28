# CallAndResponse

A .NET library for structured **call-and-response** communication over
byte-oriented transports. Swap between serial, BLE, and USB without touching
your protocol code.

The library is pure framing and protocol logic. It never opens, closes, or
manages transport connections — you provide an active `IDuplexPipe` from
`System.IO.Pipelines`, and CallAndResponse handles message framing on top of it.

## Getting Started

### Prerequisites

- .NET SDK 9.0.200 or later (required by the `.slnx` solution format; all projects target net8.0)

### Install

```
dotnet add package CallAndResponse
dotnet add package CallAndResponse.Protocol.Modbus
```

### Quick Example — Modbus over Serial

```csharp
using System.IO.Ports;
using System.IO.Pipelines;
using CallAndResponse;
using CallAndResponse.Protocol.Modbus;

// You own the serial port lifecycle
using var port = new SerialPort("COM3", 9600, Parity.Even);
port.Open();

// Bridge to pipes — two lines
var transceiver = new Transceiver(
    PipeReader.Create(port.BaseStream),
    PipeWriter.Create(port.BaseStream));

// Use it with a protocol client
var modbus = new ModbusRtuClient(transceiver);

var registers = await modbus.ReadHoldingRegisters(
    unitIdentifier: 1,
    startingAddress: 0x0000,
    numRegisters: 10,
    cancellationToken);
```

### Quick Example — Custom Framing

```csharp
// Receive until a specific header + footer pattern is found
var payload = await transceiver.SendReceiveHeaderFooter(
    writeBytes: new byte[] { 0x01, 0x02 },
    header: new byte[] { 0xAA },
    footer: new byte[] { 0x55 },
    token: cancellationToken);

// Or use temporal framing for unsolicited data (e.g., barcode scanners)
var burst = await transceiver.ReceiveUntilIdle(
    idleTimeout: TimeSpan.FromMilliseconds(100),
    token: cancellationToken);
```

### Quick Example — STM32 Firmware Update

```csharp
using CallAndResponse.Protocol.Stm32Bootloader;

var bootloader = new Stm32BootloaderClient(transceiver);

if (await bootloader.Ping(cancellationToken))
{
    var info = await bootloader.GetSupportedCommands(cancellationToken);
    var chipId = await bootloader.GetId(cancellationToken);

    // Read 1024 bytes of flash
    var flash = await bootloader.ReadMemory(
        Stm32BootloaderClient.Stm32BaseAddress, 1024, cancellationToken);
}
```

## Packages

| Package | Description |
|---|---|
| `CallAndResponse` | Core library — `ITransceiver`, `Transceiver`, framing extensions, exceptions |
| `CallAndResponse.Protocol.Modbus` | Modbus RTU client (read/write holding registers) |
| `CallAndResponse.Protocol.Stm32Bootloader` | STM32 system bootloader commands (read/write/erase flash) |

## Architecture

The library has two layers that only depend downward:

```
Protocol Layer       (Modbus, STM32 — depends only on ITransceiver)
    ↓
Core Abstraction     (ITransceiver, Transceiver, PipeReader + PipeWriter)
```

- **`Transceiver`** takes `IDuplexPipe` or `PipeReader` + `PipeWriter` and provides
  framed message exchange. All convenience methods (`SendReceiveExactly`,
  `ReceiveUntilTerminator`, etc.) are extension methods on `ITransceiver`.
- **Protocol clients** accept `ITransceiver` and use the convenience methods to
  implement protocol-specific operations.
- **Transport bridging** is the caller's responsibility. See `Examples/` for
  complete `IDuplexPipe` implementations for serial and BLE Nordic UART.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture
document.

## Adding a Transport

Bridge your transport to `System.IO.Pipelines` and create a `Transceiver`:

```csharp
// For stream-based transports (serial, TCP, etc.)
var transceiver = new Transceiver(
    PipeReader.Create(stream),
    PipeWriter.Create(stream));

// For event-based transports (BLE notifications, etc.)
var rxPipe = new Pipe();
device.DataReceived += async (s, e) =>
    await rxPipe.Writer.WriteAsync(e.Data);
var transceiver = new Transceiver(rxPipe.Reader, txPipeWriter);
```

See `Examples/Example.Transport.Serial/` and `Examples/Example.Transport.Ble/`
for complete working examples.

## Adding a Protocol

Accept `ITransceiver` via constructor and use the convenience methods:

```csharp
public class MyProtocolClient
{
    private readonly ITransceiver _transceiver;

    public MyProtocolClient(ITransceiver transceiver)
        => _transceiver = transceiver;

    public async Task<byte[]> ReadDeviceId(CancellationToken token)
    {
        var response = await _transceiver.SendReceiveExactly(
            new byte[] { 0x01 },
            numBytesExpected: 4,
            token);

        return response.ToArray();
    }
}
```

## Project Structure

```
CallAndResponse/
├── Source/
│   ├── CallAndResponse/                          Core library
│   │   ├── ITransceiver.cs                       Protocol-facing contract
│   │   ├── Transceiver.cs                        Pipe-backed implementation
│   │   ├── TransceiverExtensions.cs              Convenience framing methods
│   │   ├── DuplexPipeExtensions.cs               AsTransceiver() extension
│   │   ├── FrameDetectionResult.cs               Frame detection return type
│   │   └── TransceiverTransportException.cs      I/O-level exception
│   │
│   ├── CallAndResponse.Protocol.Modbus/          Modbus RTU protocol
│   └── CallAndResponse.Protocol.Stm32Bootloader/ STM32 bootloader protocol
│
├── Examples/
│   ├── Example.Transport.Serial/                 Serial IDuplexPipe + Modbus
│   └── Example.Transport.Ble/                    BLE Nordic UART IDuplexPipe
│
├── Test/
│   └── CallAndResponse.Test.Unit/                Unit tests (xUnit)
│
├── docs/
│   ├── ARCHITECTURE.md
│   └── adr/                                      Architecture decision records
│
└── CallAndResponse.slnx
```

## Building

```
dotnet build CallAndResponse.slnx
dotnet test CallAndResponse.slnx --filter Category!=Integration
```

## License

[MIT](LICENSE) © Charles Lee
