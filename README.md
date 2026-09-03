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

// Bind Modbus RTU framing: the inter-frame gap plus the CRC-16
var channel = ModbusRtu.Channel(transceiver, baudRate: 115200);

// Use it with a protocol client
var modbus = new ModbusRtuClient(channel);

var registers = await modbus.ReadHoldingRegisters(
    unitIdentifier: 1,
    startingAddress: 0x0000,
    numRegisters: 10,
    cancellationToken);
```

### Quick Example — Custom Framing

Framing is a value you pass in, so the strategies compose.

```csharp
// Receive the bytes between a header and a footer
var payload = await transceiver.SendReceive(
    new byte[] { 0x01, 0x02 },
    Frame.Between(header: new byte[] { 0xAA }, footer: new byte[] { 0x55 }),
    cancellationToken);

// Temporal framing for unsolicited data (barcode scanners, NMEA bursts)
var burst = await transceiver.Receive(
    Frame.UntilIdle(TimeSpan.FromMilliseconds(100)),
    cancellationToken);

// A length-prefixed reply, checked before it reaches you
var frame = await transceiver.Receive(
    Frame.LengthPrefixed(prefixOffset: 1, prefixSize: 2).Validated(MyChecksum),
    cancellationToken);

// Bound a stalled reply: fail after a 50ms gap rather than waiting out your token
var reply = await transceiver.Receive(
    Frame.Exactly(16).WithIdleTimeout(TimeSpan.FromMilliseconds(50)),
    cancellationToken);
```

### Quick Example — SLIP or PPP-style framing

For a self-delimiting link, bind a codec once and then send and receive payloads. Delimiters,
escapes, and the checksum stop being your problem.

```csharp
IMessageTransceiver channel = transceiver.WithFraming(new SlipCodec());

var reply = await channel.SendReceiveMessage(request, cancellationToken);
```

`HdlcCodec` is the same for RFC 1662 asynchronous HDLC framing, including the FCS. It is the framing
half of PPP and nothing above it — no LCP, no authentication, no NCPs.

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
| `CallAndResponse` | Core library — the channel contracts, `Transceiver`, the `Frame` catalogue, SLIP and HDLC codecs, exceptions |
| `CallAndResponse.Transport.Serial` | `SerialDuplexPipe` over `RJCP.SerialPortStream` |
| `CallAndResponse.Protocol.Modbus` | Modbus RTU client (FC03 read, FC16 write) |
| `CallAndResponse.Protocol.Stm32Bootloader` | STM32 system bootloader commands (read/write/erase flash) |

`CallAndResponse.Transport.BleNordicUart` ships in the repo but is not published
to NuGet. Reference the project directly, or copy `BleNordicUartPipe.cs`.

## Architecture

The library has three layers that only depend downward:

```
Protocol Layer       (Modbus, STM32 — depend only on the channel abstractions)
    ↓
Core Abstraction     (ITransceiver, IMessageTransceiver, Transceiver)
    ↓
Framing Layer        (Frame.* decoders, SlipCodec, HdlcCodec, ModbusRtu.Codec)
    ↓
Transport Layer      (SerialDuplexPipe, BleNordicUartPipe — implement IDuplexPipe)
```

- **`ITransceiver`** is a byte channel: sends go out verbatim, and each receive is directed by the
  decoder you pass in. **`IMessageTransceiver`** is a message channel whose framing is fixed by the
  link, so you send and receive payloads. `WithFraming` and `AsByteStream` move between them.
- **Framing is a value**, not a method per strategy. `Frame.Exactly(4)`, `Frame.UntilIdle(gap)`, and
  `new SlipCodec()` are all things you pass, and the combinators compose them.
- **Protocol clients** accept whichever channel their protocol actually needs. They never reference a
  transport package.
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

Take `ITransceiver` when the protocol decides its own frame boundaries:

```csharp
public class MyProtocolClient
{
    private readonly ITransceiver _transceiver;

    public MyProtocolClient(ITransceiver transceiver)
        => _transceiver = transceiver;

    public async Task<byte[]> ReadDeviceId(CancellationToken token)
    {
        var response = await _transceiver.SendReceive(
            new byte[] { 0x01 },
            Frame.Exactly(4),
            token);

        return response.ToArray();
    }
}
```

Take `IMessageTransceiver` when the link is self-delimiting. Such a client runs over SLIP, over
HDLC, or over a terminator codec without modification, because it never states a byte boundary:

```csharp
public class MyMessageClient(IMessageTransceiver channel)
{
    public async Task<Reply> Ask(Request request, CancellationToken token) =>
        Parse(await channel.SendReceiveMessage(Build(request), token));
}
```

If your protocol has framing of its own — a checksum, a delimiter, an inter-frame gap — put it in an
`IFrameCodec` and hand out a channel type built from it, the way `ModbusRtu.Channel` does. A client
that accepts any `IMessageTransceiver` while assuming its own framing will accept the wrong one
silently.

## Project Structure

```
CallAndResponse/
├── Source/
│   ├── CallAndResponse/                          Core library
│   │   ├── ITransceiver.cs                       Protocol-facing contract
│   │   ├── Transceiver.cs                        Pipe-backed implementation
│   │   ├── IMessageTransceiver.cs                Message-channel contract
│   │   ├── MessageTransceiver.cs                 Binds a codec to a link
│   │   ├── ByteStreamAdapter.cs                  Message channel as a byte channel
│   │   ├── TransceiverExtensions.cs              SendReceive, WithFraming, AsByteStream
│   │   ├── DuplexPipeExtensions.cs               AsTransceiver() extension
│   │   ├── Framing/                              Decoders, codecs, and combinators
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
