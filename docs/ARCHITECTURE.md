# CallAndResponse — Architecture

## Overview

CallAndResponse is a .NET library for structured **call-and-response** communication
over byte-oriented transports. It decouples *how* bytes move (serial, BLE, anything
you can express as a pipe) from *what* those bytes mean (Modbus, STM32 bootloader,
custom protocols), letting you swap transports without touching protocol logic.

The library is **pure framing and protocol logic**. It never opens, closes, connects,
or disconnects anything. It receives an already-active `IDuplexPipe` and provides
structured message exchange on top of it.

The design has three axes:

| Axis | Concern | Examples |
|---|---|---|
| **Transport** | Adapt an active connection to `IDuplexPipe` | Serial port, BLE Nordic UART |
| **Framing** | Decide where a frame ends, and turn wire bytes into a payload and back | Fixed length, terminator, idle gap, SLIP, RFC 1662 async HDLC |
| **Protocol** | Build request frames, parse response frames, enforce protocol rules | Modbus RTU, STM32 bootloader |

Framing is the axis [ADR-0020](adr/adr-0020-framing-codec-abstraction.md) added. Before it, framing was
a receive-only concern expressed as offsets into the received bytes, which could not describe a payload
that is not a contiguous slice of the wire and had nowhere to put an escape or a checksum on the way out.

A protocol implementation takes an `ITransceiver` or an `IMessageTransceiver` and never knows which
transport is underneath.

---

## Layer Diagram

```
┌──────────────────────────────────────────────────────┐
│                  Application Code                    │
│                                                      │
│   // Caller owns transport lifecycle                 │
│   using var port = new SerialPortStream("COM5", …);  │
│   port.Open();                                       │
│                                                      │
│   await using var pipe = new SerialDuplexPipe(port); │
│   var link    = pipe.AsTransceiver();                │
│   var channel = ModbusRtu.Channel(link, 115200);     │
│   var modbus  = new ModbusRtuClient(channel);        │
│   var regs    = await modbus.ReadHoldingRegisters(…);│
└──────────────────┬───────────────────────────────────┘
                   │ uses ITransceiver / IMessageTransceiver
┌──────────────────▼───────────────────────────────────┐
│              Protocol Layer (optional)               │
│                                                      │
│   CallAndResponse.Protocol.Modbus                    │
│   CallAndResponse.Protocol.Stm32Bootloader           │
│                                                      │
│   Builds request payloads, validates responses.      │
│   Delegates all I/O to the channel it was given.     │
└──────────────────┬───────────────────────────────────┘
                   │ Send / Receive(decoder), or SendMessage / ReceiveMessage
┌──────────────────▼───────────────────────────────────┐
│                 Core Abstraction                     │
│                                                      │
│   ITransceiver ──────── byte channel, caller-framed  │
│   IMessageTransceiver ─ message channel, link-framed │
│                                                      │
│   Transceiver (sealed) ── IDuplexPipe composition    │
│   MessageTransceiver ──── binds a codec to a link    │
│   ByteStreamAdapter ───── the other direction        │
└──────────────────┬───────────────────────────────────┘
                   │ IFrameDecoder / IFrameCodec
┌──────────────────▼───────────────────────────────────┐
│                   Framing Layer                      │
│                                                      │
│   Frame.Exactly / UntilTerminator / UntilPattern /   │
│   Between / UntilIdle / LengthPrefixed / Custom      │
│     └─ plus WithIdleTimeout, WithMaxLength, Validated│
│                                                      │
│   SlipCodec (RFC 1055), HdlcCodec (RFC 1662)         │
│   ModbusRtu.Codec — CRC-16 and the inter-frame gap   │
└──────────────────┬───────────────────────────────────┘
                   │ implements IDuplexPipe
┌──────────────────▼───────────────────────────────────┐
│               Transport Layer                        │
│                                                      │
│   CallAndResponse.Transport.Serial                   │
│       └─ SerialDuplexPipe(SerialPortStream)          │
│                                                      │
│   CallAndResponse.Transport.BleNordicUart            │
│       └─ BleNordicUartPipe()                         │
└──────────────────────────────────────────────────────┘
```

Dependencies flow **downward only**. Protocol packages reference the core
`CallAndResponse` package and nothing else — they never reference a transport
package. Transport packages also reference only the core package. The
application wires them together.

The framing layer lives inside the core package rather than beside it: a decoder is a value, not a
dependency, and the codecs add nothing to the dependency graph.

`System.IO.Pipelines` is the seam. Any transport you can express as a
`PipeReader` / `PipeWriter` pair works without a dedicated package: a
`NetworkStream`, a `NamedPipeClientStream`, or a plain `Stream` via
`PipeReader.Create` / `PipeWriter.Create`.

---

## Package Map

| Package | TFM | Dependencies | Purpose |
|---|---|---|---|
| `CallAndResponse` | net8.0 | Microsoft.Extensions.Logging.Abstractions, System.IO.Pipelines, System.Diagnostics.DiagnosticSource | Core abstraction: `ITransceiver`, `IMessageTransceiver`, `Transceiver`, the `Frame` decoder catalogue, `SlipCodec`, `HdlcCodec`, exceptions |
| `CallAndResponse.Transport.Serial` | net8.0 | Core, RJCP.SerialPortStream | Serial port duplex pipe |
| `CallAndResponse.Transport.BleNordicUart` | net8.0 | Core, Plugin.BLE | BLE Nordic UART Service duplex pipe |
| `CallAndResponse.Protocol.Modbus` | net8.0 | Core | Modbus RTU codec and client (FC03, FC16) |
| `CallAndResponse.Protocol.Stm32Bootloader` | net8.0 | Core | STM32 system bootloader command set |

`CallAndResponse`, `CallAndResponse.Transport.Serial`, `CallAndResponse.Protocol.Modbus`, and
`CallAndResponse.Protocol.Stm32Bootloader` are the packable projects; `MinVer` derives their version from
the nearest `v*` tag. `.github/workflows/publish.yml` publishes them to nuget.org on a `v*` tag — see
[Releasing](../CONTRIBUTING.md#releasing).

---

## Core Abstraction

Two contracts, split on where the frame boundary is decided. A protocol that knows its own reply
shapes takes the byte channel; one running over a self-delimiting link takes the message channel.

### `ITransceiver`

A byte channel. Sends go out verbatim, and each receive is directed by the caller, who supplies the
decoder that says where the frame ends. No lifecycle members — no `Open`, no `Close`, no `IsOpen`.
See [ADR-0011](adr/adr-0011-remove-lifecycle-ownership-from-transceiver.md).

```
ITransceiver
├── Send(ReadOnlyMemory<byte>, CancellationToken)
├── Receive(IFrameDecoder, CancellationToken)                      → Memory<byte>
└── Receive(IFrameDecoder, IBufferWriter<byte>, CancellationToken) → writes in place
```

### `IMessageTransceiver`

A message channel. Framing is a property of the link and fixed for its lifetime, so the caller sends
and receives payloads and never sees a delimiter, an escape, or a checksum.

```
IMessageTransceiver
├── SendMessage(ReadOnlyMemory<byte>, CancellationToken)
└── ReceiveMessage(CancellationToken) → Memory<byte>
```

A client written against this runs over SLIP, over RFC 1662, or over a plain terminator codec without
modification, because it never expressed an opinion about byte boundaries.

### Moving between them

```csharp
ITransceiver        link    = pipe.AsTransceiver();
IMessageTransceiver channel = link.WithFraming(new SlipCodec());
ITransceiver        again   = channel.AsByteStream();
```

`AsByteStream` is lossy in both directions and says so. Reads concatenate across message boundaries;
each `Send` becomes exactly one message whether or not the caller meant one. It exists for a client
written against `ITransceiver` that has to run over a framed link, and a client whose sends do not
already align with message boundaries should move to `IMessageTransceiver` instead.

### `Transceiver`

The `ITransceiver` implementation, `sealed`. Two constructors:

```csharp
new Transceiver(IDuplexPipe pipe, ILogger<Transceiver>? logger = null)
new Transceiver(PipeReader input, PipeWriter output, ILogger<Transceiver>? logger = null)
```

The caller owns the pipe and everything under it. `Transceiver` never completes the reader or the
writer.

### `AsTransceiver()`

Extension method on `IDuplexPipe` (`DuplexPipeExtensions`) for the common case:

```csharp
ITransceiver transceiver = myDuplexPipe.AsTransceiver();
```

### `IFrameDecoder`

The framing strategy, injected as a value. A decoder answers where the frame ends and **produces**
the payload, rather than describing where it sits in the received bytes. That is what makes SLIP and
RFC 1662 expressible: their payloads are not contiguous slices of the wire.

```csharp
public interface IFrameDecoder
{
    TimeSpan? IdleTimeout { get; }
    FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload);
}
```

`FrameContext` carries `Received` (a `ReadOnlySequence<byte>` starting at the first unconsumed byte),
`IsIdle`, and `IsTransportComplete`. `FrameDecodeResult` carries a status and a consumed extent:

| Status | Meaning | Loop does |
|---|---|---|
| `NeedMoreData` | No complete frame yet | Wait for more; consumes nothing |
| `Frame` | Payload written to the writer | Deliver it, consume `ConsumedLength` |
| `Discard` | Leading bytes belong to no frame | Drop them and decode again |
| `Invalid` | A frame was found and is malformed | Consume it, then throw `FramingException` |

Two rules the receive loop enforces rather than trusts:

- **Output is transactional.** The decoder writes to a staging buffer the loop owns, and the caller
  sees it only on `Frame`. `IBufferWriter<byte>` has no rewind, so a decoder that writes and then asks
  for more data would otherwise duplicate, and one that writes and then rejects would leak.
- **A decoder that throws does not wedge the link.** The loop advances the reader on the way out.
  Before [ADR-0020](adr/adr-0020-framing-codec-abstraction.md) an exception from caller-supplied code
  skipped every `AdvanceTo`, and `PipeReader` refused every later read for the rest of the session.

`Decode` must be a pure function of its context. The loop re-invokes it on a buffer that grows but
always starts at the same byte, so a decoder that carries a parse cursor between calls will mis-frame.

### `IFrameCodec`

`IFrameEncoder` adds the send half — delimiters, escapes, checksums — and `IFrameCodec` is both.
A codec is what binds to a link to make a message channel, because a framing that transforms the
payload has to transform it in both directions.

### The `Frame` catalogue

| Decoder | Frames on | Consumes |
|---|---|---|
| `Frame.Exactly(n)` | A byte count | The `n` bytes returned |
| `Frame.UntilTerminator(b)` | A single delimiter byte | Payload **and** the delimiter |
| `Frame.UntilPattern(bytes)` | A delimiter sequence | Payload **and** the pattern |
| `Frame.Between(header, footer)` | A header, then the next footer | Everything through the footer |
| `Frame.UntilIdle(gap)` | Silence on the line | Everything buffered |
| `Frame.UntilTransportComplete()` | The transport closing | Everything buffered |
| `Frame.LengthPrefixed(…)` | A length field | The whole frame it describes |
| `Frame.Custom(…)` / `Frame.OverSpan(…)` | Whatever the caller writes | Whatever it reports |

Combinators, which is the point of decoders being values:

| Combinator | Effect |
|---|---|
| `.WithIdleTimeout(gap)` | Stop waiting at the gap: ask the inner decoder once more as if the transport had closed, and fail if it still cannot finish |
| `.WithMaxLength(n)` | Fail rather than accumulate forever when no frame arrives |
| `.Validated(check)` | Reject a decoded payload — a CRC, a magic byte — before it reaches the caller |

`Frame.UntilIdle(gap).Validated(crc)` is Modbus RTU's real framing rule, and was not expressible
before [ADR-0020](adr/adr-0020-framing-codec-abstraction.md).

`WithIdleTimeout` is a deadline rather than a framing rule, and the distinction matters. It never
returns the buffered wire bytes in the inner decoder's place, because doing so would skip that
decoder's unescaping, its checksum, and anything `Validated` wrapped around it — handing the caller
undecoded bytes shaped like a payload. To frame on silence itself, use `Frame.UntilIdle`.

### Framing codecs

`SlipCodec` implements RFC 1055: `0xC0` delimits, `0xDB` escapes. It has no checksum and no error
detection of any kind, so after a desynchronisation noise between two delimiters decodes into a
payload that looks valid. An empty payload also does not survive the round trip — encoded it is two
delimiters, which is what the RFC tells receivers to discard as inter-frame fill.

`HdlcCodec` implements RFC 1662 asynchronous HDLC framing, and only the framing: LCP,
authentication, and the NCPs are a link state machine and out of scope. `0x7E` delimits, `0x7D`
escapes with a `0x20` XOR, the ACCM says which control octets to escape, and a CRC-16/X-25 FCS
precedes the closing flag. The FCS lives inside the codec rather than in a stackable CRC layer
because RFC 1662 computes it over the **unescaped** frame and then escapes it along with everything
else — escaping and integrity interleave, so they cannot be layers.

---

## Transport Implementations

Each transport package provides a single `IDuplexPipe`. The caller owns the
underlying transport resource and its lifecycle.

### `SerialDuplexPipe`

Wraps an already-open `RJCP.IO.Ports.SerialPortStream`. Implements
`IAsyncDisposable`; disposing it stops the background read pump, not the port.

The pump distinguishes a clean stop from a dead port. Disposal cancels it and the
reader sees an ordinary end of stream. Anything else — the adapter unplugged, a
driver error, another process taking the handle — is captured and passed to
`writer.Complete(failure)`, so the consumer's next read throws the real cause
rather than reporting a truncated frame.

```csharp
using var port = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One);
port.Open();
await using var pipe = new SerialDuplexPipe(port);
var transceiver = new Transceiver(pipe);
```

RJCP is a third-party dependency and needs a native `libnserial` build on Linux.
[ADR-0019](adr/adr-0019-dual-serial-transport-backends.md) accepts a second serial backend over
`System.IO.Ports` so consumers can choose. It is not implemented; this section describes what ships
today.

### `BleNordicUartPipe`

A pipe pair for the BLE Nordic UART Service. The caller owns BLE connection,
service discovery, and notification subscription, and drives the pipe from both
ends: write received notification bytes into `RxWriter`, and pump `TxReader` out
to the TX characteristic.

```csharp
var pipe = new BleNordicUartPipe();
// feed pipe.RxWriter from BLE notifications
// drain pipe.TxReader to the TX characteristic
var transceiver = new Transceiver(pipe);
```

### Any `Stream`

No package required:

```csharp
var transceiver = new Transceiver(
    PipeReader.Create(stream),
    PipeWriter.Create(stream));
```

---

## Protocol Implementations

### Modbus RTU (`CallAndResponse.Protocol.Modbus`)

`ModbusRtuClient` implements `IModbusClient` and supports:

- **FC03** — Read Holding Registers
- **FC16** — Write Multiple Registers

```csharp
var channel = ModbusRtu.Channel(pipe.AsTransceiver(), baudRate: 115200);
var modbus  = new ModbusRtuClient(channel);
```

The client takes `ModbusRtuChannel`, not a bare `IMessageTransceiver`. It reads every reply as a
CRC-checked, gap-delimited RTU frame, and any other channel would satisfy the interface while
silently producing requests with no CRC and responses that were never validated. The type is what
guarantees the framing.

`ModbusRtu.Codec(gap)` owns both halves of RTU framing: it appends the CRC-16 on send, and on receive
it frames on the 3.5-character inter-frame gap, validates the CRC, and strips it. `ModbusRtu.GapFor`
derives that gap from the baud rate, which the application knows and the transceiver deliberately
does not.

Framing on the gap rather than on an expected response length is what lets a Modbus exception
response be parsed at all — it is five bytes, shorter than any success response, so a length-based
framing waits forever for bytes the device already finished sending.

Request payloads are built with the internal `ModbusRtuRequestBuilder`, which no longer touches the
CRC. Response validation checks unit identifier, function code, declared byte count, and the
exception flag; exceptions are surfaced as `ModbusProtocolException` with a typed
`ModbusProtocolExceptionCode`.

### STM32 Bootloader (`CallAndResponse.Protocol.Stm32Bootloader`)

`Stm32BootloaderClient` implements the STM32 system bootloader command set
(AN3155) including:

- `Ping` — Initial synchronization (0x7F → ACK/NACK)
- `GetSupportedCommands` — Protocol version and command list, framed by
  `Frame.LengthPrefixed` after its opening ACK is read on its own so a NACK is
  reported rather than left waiting for a byte count that never arrives
- `GetId` — Chip ID
- `ReadMemory` — Read flash/RAM in 256-byte pages
- `WriteMemory` — Write flash/RAM in 256-byte pages
- `ExtendedEraseMass` — Extended erase, AN3155 mass-erase code `0xFFFF`
- `ExtendedEraseBank` — Extended erase, AN3155 bank codes `0xFFFE` / `0xFFFD`
- `ExtendedErasePages` — Extended erase of an explicit page list
  (`ExtendedEraseMemoryPages` is the deprecated pages-`0..N` form of this; see
  [ADR-0016](adr/adr-0016-stm32-extended-erase-api-shape.md))
- `GetProtocolVersion` — Bootloader version and legacy option bytes (0x01)
- `EraseMemory` / `EraseAllMemory` — Page and global erase for bootloaders below 3.0 (0x43)
- `ReadoutUnprotect` — Leave RDP level 1; mass erases the flash (0x92)
- `GetChecksum` — Device-computed CRC over a memory region (0xA1)

`WriteProtect` (0x63), `WriteUnprotect` (0x73), and `ReadoutProtect` (0x82) are
declared but marked `[Obsolete(…, true)]`, so calling them is a compile error.
They rewrite option bytes and are not shipped without hardware to verify them
against. See [ADR-0018](adr/adr-0018-stm32-bootloader-command-surface.md).

---

## Exception Hierarchy

```
Exception
├── TransceiverTransportException      I/O-level failures during send/receive
│                                      (write failed, disconnected mid-transfer,
│                                      closed with bytes left unframed)
│
├── FramingException                   A healthy transport delivered a malformed
│   │                                  frame — an illegal escape, an over-length
│   │                                  frame, a decoder that rejected one
│   └── FrameIntegrityException        A checksum did not match its contents
│                                      (an RFC 1662 FCS mismatch)
│
├── ModbusProtocolException            Modbus exception response from the device
│   └── .ExceptionCode                 (typed ModbusProtocolExceptionCode enum)
│
├── ModbusFramingException             Modbus response framing error (unit ID or
│                                      function code mismatch)
│
├── ModbusTransportException           Wraps TransceiverTransportException for
│                                      Modbus-specific context
│
└── Stm32BootloaderException           STM32 bootloader protocol violation
                                       (a sync-byte reply that is neither ACK
                                       nor NACK, a NACK or unexpected byte
                                       where a command expects an ACK, a
                                       malformed reply)
```

---

## Design Patterns

| Pattern | Where | Purpose |
|---|---|---|
| **Strategy** | `IFrameDecoder` passed to `Receive` | Framing is a value the caller injects, not a subclass or a fixed member |
| **Decorator** | `Frame.WithMaxLength` / `.Validated` / `.WithIdleTimeout`; `MessageTransceiver` over `ITransceiver` | Compose framing rules, and bind a codec to a link, without either knowing about the other |
| **Adapter** | `AsByteStream()` | Present a message channel as a byte channel, with the losses stated rather than hidden |
| **Composition** | `IDuplexPipe.AsTransceiver()` | Wrap any duplex pipe in a transceiver without inheritance |
| **Builder** | `ModbusRtuRequestBuilder` | Fluent frame construction |
| **Dependency Inversion** | Protocol → `ITransceiver` / `IMessageTransceiver` | Protocols depend on the abstraction, never on a concrete transport |

---

## Extension Points

### Adding a new transport

1. Express the connection as an `IDuplexPipe`, or as a `PipeReader` / `PipeWriter` pair.
2. Pass it to `new Transceiver(…)`, or call `.AsTransceiver()` on the pipe.

A dedicated package is only worth it when the adaptation is non-trivial — a
background pump, a framing quirk, a vendor SDK that is not stream-shaped.
`SerialDuplexPipe` exists because `SerialPortStream` needs a pump.
`PipeReader.Create(stream)` needs no package at all.

### Adding a new protocol

1. Create a new package referencing `CallAndResponse`.
2. Pick the channel the protocol actually needs. Take `ITransceiver` when the protocol decides its own
   boundaries — a fixed reply length, a terminator, a length field. Take a message channel when the
   link is self-delimiting and the framing chooses for you.
3. Implement operations with `SendReceive(bytes, decoder)` or `SendReceiveMessage(payload)`.
4. Define protocol-specific exceptions.

If the protocol has framing of its own — a checksum, a delimiter, an inter-frame gap — put it in an
`IFrameCodec` and expose a channel type built from it, the way `ModbusRtu.Channel` does. A client that
accepts any `IMessageTransceiver` while assuming its own framing will accept the wrong one silently.

### Custom framing

Compose the catalogue first: `Frame.LengthPrefixed(...).Validated(crc)` and
`Frame.UntilTerminator(0x0A).WithMaxLength(512)` cover most of what devices actually do. When
nothing fits, write the decode function:

```csharp
var decoder = Frame.OverSpan((received, isIdle, isTransportComplete, payload) =>
{
    if (received.Length < 4) return FrameDecodeResult.NeedMoreData;
    payload.Write(received.Slice(2, 2));
    return FrameDecodeResult.Frame(4);
});
```

Write to `payload` only when returning `Frame`, keep `Decode` a pure function of its arguments, and
report a malformed frame as `FrameDecodeResult.Invalid` rather than throwing. `Frame.Custom` is the
same thing over a `ReadOnlySequence<byte>`, which avoids a copy when the transport hands back
segmented buffers.

For a framing that also transforms outgoing bytes, implement `IFrameCodec` and bind it with
`WithFraming`. `SlipCodec` is the smallest complete example.

---

## Logging

All library packages use `Microsoft.Extensions.Logging.Abstractions` as their
logging dependency. No concrete logging sink (Serilog, NLog, etc.) appears in
any library project.

- `NullLogger.Instance` is the default at every construction site — the library
  never throws when no logger is provided.
- Consumers are free to wire any MEL-compatible backend (Serilog via
  `Serilog.Extensions.Logging`, Microsoft.Extensions.Logging.Console, etc.)
  by resolving an `ILogger` or `ILogger<T>` from their host and passing it in.

See [ADR-0006](adr/adr-0006-logging-abstraction-strategy.md) for the full
rationale, log-level mapping from the prior Serilog implementation, and
package version constraints.

---

## Architectural Decision Records

See [`docs/adr/README.md`](adr/README.md) for the index, including which records
describe abstractions the library no longer has.

---

## Target Framework Strategy

All projects target **net8.0**.

All projects target net8.0, but building requires the **9.0.200 SDK or later** — the `.slnx` solution
format is not parsed by older SDKs. CI builds in a `dotnet/sdk:10.0` container.
