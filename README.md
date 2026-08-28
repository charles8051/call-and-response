# CallAndResponse

A .NET library for structured **call-and-response** communication over
byte-oriented transports. Swap between serial, BLE, and anything else you can
express as a pipe without touching your protocol code.

The library is pure framing and protocol logic. It never opens, closes, or
manages transport connections — you provide an active `IDuplexPipe` from
`System.IO.Pipelines`, and CallAndResponse handles message framing on top of it.

## Getting Started

### Prerequisites

- .NET SDK 9.0.200 or later (required by the `.slnx` solution format; all projects target net8.0)

### Install

```
dotnet add package CallAndResponse
dotnet add package CallAndResponse.Transport.Serial
dotnet add package CallAndResponse.Protocol.Modbus
```

### Quick Example — Modbus over Serial

```csharp
using CallAndResponse;
using CallAndResponse.Protocol.Modbus;
using CallAndResponse.Transport.Serial;
using RJCP.IO.Ports;

// You own the serial port lifecycle
using var port = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One);
port.Open();

// Wrap the open port in a duplex pipe
await using var pipe = new SerialDuplexPipe(port);

var transceiver = new Transceiver(pipe);

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
| `CallAndResponse.Transport.Serial` | `SerialDuplexPipe` over `RJCP.SerialPortStream` |
| `CallAndResponse.Protocol.Modbus` | Modbus RTU client (FC03 read, FC16 write) |
| `CallAndResponse.Protocol.Stm32Bootloader` | STM32 system bootloader commands (read/write/erase flash) |

`CallAndResponse.Transport.BleNordicUart` ships in the repo but is not published
to NuGet. Reference the project directly, or copy `BleNordicUartPipe.cs`.

## Architecture

The library has three layers that only depend downward:

```
Protocol Layer       (Modbus, STM32 — depend only on ITransceiver)
    ↓
Core Abstraction     (ITransceiver, Transceiver, framing extensions)
    ↓
Transport Layer      (SerialDuplexPipe, BleNordicUartPipe — implement IDuplexPipe)
```

- **`Transceiver`** takes an `IDuplexPipe`, or a `PipeReader` + `PipeWriter` pair,
  and provides framed message exchange. All convenience methods
  (`SendReceiveExactly`, `ReceiveUntilTerminator`, etc.) are extension methods on
  `ITransceiver`.
- **Protocol clients** accept `ITransceiver` and use the convenience methods to
  implement protocol-specific operations. They never reference a transport package.
- **Transport packages** each provide a single `IDuplexPipe`. The caller owns the
  underlying connection and its lifecycle.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full architecture
document.

## Adding a Transport

A transport package is only worth writing when the adaptation is non-trivial — a
background pump, a framing quirk, a vendor SDK that is not stream-shaped. Most
transports need no package at all.

```csharp
// Any Stream — serial, TCP, named pipe. No package required.
var transceiver = new Transceiver(
    PipeReader.Create(stream),
    PipeWriter.Create(stream));

// Any IDuplexPipe, via the AsTransceiver() extension
ITransceiver transceiver = myDuplexPipe.AsTransceiver();

// Event-based transports (BLE notifications, etc.) need a pipe you drive
var pipe = new BleNordicUartPipe();
device.DataReceived += async (s, e) => await pipe.RxWriter.WriteAsync(e.Data);
// ...and a loop draining pipe.TxReader out to the device
var transceiver = new Transceiver(pipe);
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
│   ├── CallAndResponse.Transport.Serial/         SerialDuplexPipe (RJCP)
│   ├── CallAndResponse.Transport.BleNordicUart/  BleNordicUartPipe (unpublished)
│   ├── CallAndResponse.Protocol.Modbus/          Modbus RTU protocol
│   └── CallAndResponse.Protocol.Stm32Bootloader/ STM32 bootloader protocol
│
├── Examples/
│   ├── Example.Transport.Serial/                 Serial + Modbus
│   └── Example.Transport.Ble/                    BLE Nordic UART
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
dotnet test CallAndResponse.slnx
```

## License

[MIT](LICENSE) © Charles Lee
