# CallAndResponse — Architecture

## Overview

CallAndResponse is a .NET library for structured **call-and-response** communication
over byte-oriented transports. It decouples *how* bytes move (serial, BLE, anything
you can express as a pipe) from *what* those bytes mean (Modbus, STM32 bootloader,
custom protocols), letting you swap transports without touching protocol logic.

The library is **pure framing and protocol logic**. It never opens, closes, connects,
or disconnects anything. It receives an already-active `IDuplexPipe` and provides
structured message exchange on top of it.

The design has two axes:

| Axis | Concern | Examples |
|---|---|---|
| **Transport** | Adapt an active connection to `IDuplexPipe` | Serial port, BLE Nordic UART |
| **Protocol** | Build request frames, parse response frames, enforce protocol rules | Modbus RTU, STM32 bootloader |

A protocol implementation takes an `ITransceiver` and uses its `SendReceive*`
convenience methods without knowing or caring which transport is underneath.

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
│   var transceiver = new Transceiver(pipe);           │
│   var modbus = new ModbusRtuClient(transceiver);     │
│   var regs   = await modbus.ReadHoldingRegisters(…); │
└──────────────────┬───────────────────────────────────┘
                   │ uses ITransceiver / IModbusClient
┌──────────────────▼───────────────────────────────────┐
│              Protocol Layer (optional)               │
│                                                      │
│   CallAndResponse.Protocol.Modbus                    │
│   CallAndResponse.Protocol.Stm32Bootloader           │
│                                                      │
│   Builds frames, validates responses, computes CRC.  │
│   Delegates all I/O to ITransceiver.                 │
└──────────────────┬───────────────────────────────────┘
                   │ calls Send, ReceiveMessage, ReceiveUntilIdle
┌──────────────────▼───────────────────────────────────┐
│                 Core Abstraction                     │
│                                                      │
│   ITransceiver ──── pure communication contract      │
│   Transceiver (sealed) ── IDuplexPipe composition    │
│       └─ framed message accumulation                 │
│   TransceiverExtensions ── SendReceive* / Receive*   │
│   AsTransceiver() ── composition entry point         │
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

`System.IO.Pipelines` is the seam. Any transport you can express as a
`PipeReader` / `PipeWriter` pair works without a dedicated package: a
`NetworkStream`, a `NamedPipeClientStream`, or a plain `Stream` via
`PipeReader.Create` / `PipeWriter.Create`.

---

## Package Map

| Package | TFM | Dependencies | Purpose |
|---|---|---|---|
| `CallAndResponse` | net8.0 | Microsoft.Extensions.Logging.Abstractions, System.IO.Pipelines, System.Diagnostics.DiagnosticSource | Core abstraction: `ITransceiver`, `Transceiver`, `FrameDetectionResult`, exceptions |
| `CallAndResponse.Transport.Serial` | net8.0 | Core, RJCP.SerialPortStream | Serial port duplex pipe |
| `CallAndResponse.Transport.BleNordicUart` | net8.0 | Core, Plugin.BLE | BLE Nordic UART Service duplex pipe |
| `CallAndResponse.Protocol.Modbus` | net8.0 | Core | Modbus RTU client (FC03, FC16) |
| `CallAndResponse.Protocol.Stm32Bootloader` | net8.0 | Core | STM32 system bootloader command set |

`CallAndResponse`, `CallAndResponse.Transport.Serial`, `CallAndResponse.Protocol.Modbus`, and
`CallAndResponse.Protocol.Stm32Bootloader` are the packable projects; `MinVer` derives their version from
the nearest `v*` tag. `.github/workflows/publish.yml` publishes them to nuget.org on a `v*` tag — see
[Releasing](../CONTRIBUTING.md#releasing).

---

## Core Abstraction

### `ITransceiver`

The central protocol-facing contract. Every protocol client consumes it; it carries
only send/receive operations. It has no lifecycle members — no `Open`, no `Close`,
no `IsOpen`. See [ADR-0011](adr/adr-0011-remove-lifecycle-ownership-from-transceiver.md).

```
ITransceiver
├── Primitives
│   ├── Send(ReadOnlyMemory<byte>, CancellationToken)
│   ├── ReceiveMessage(Func<ReadOnlyMemory<byte>, FrameDetectionResult>, CancellationToken)
│   └── ReceiveUntilIdle(TimeSpan idleTimeout, CancellationToken)
│
└── Convenience (extension methods in TransceiverExtensions)
    ├── SendReceiveExactly(…, int numBytesExpected, …)
    ├── SendReceiveFooter(…, ReadOnlyMemory<byte> footer, …)
    ├── SendReceiveHeaderFooter(…, header, footer, …)
    ├── SendReceivePerfectMatch(…, matchBytes, …)
    ├── SendReceive(…, Func detectMessage, …)
    ├── SendReceiveString(…, char terminator, …)
    ├── SendReceiveString(…, string terminator, …)
    ├── ReceiveExactly(int numBytesExpected, …)
    ├── ReceiveUntilTerminator(char, …)
    ├── ReceiveUntilTerminatorPattern(ReadOnlyMemory<byte>, …)
    ├── ReceiveUntilPerfectMatch(ReadOnlyMemory<byte>, …)
    └── ReceiveUntilHeaderFooterMatch(header, footer, …)
```

Protocol clients (`ModbusRtuClient`, `Stm32BootloaderClient`) accept `ITransceiver`.

### `Transceiver`

The single implementation, `sealed`. Two constructors:

```csharp
new Transceiver(IDuplexPipe pipe, ILogger<Transceiver>? logger = null)
new Transceiver(PipeReader input, PipeWriter output, ILogger<Transceiver>? logger = null)
```

The caller owns the pipe and everything under it. `Transceiver` never completes
the reader or the writer.

### `AsTransceiver()`

Extension method on `IDuplexPipe` (`DuplexPipeExtensions`) for the common case:

```csharp
ITransceiver transceiver = myDuplexPipe.AsTransceiver();
var client = new ModbusRtuClient(transceiver);
```

### `FrameDetectionResult`

The value a detect function returns. `FrameDetectionResult.Incomplete` means keep
reading; `FrameDetectionResult.Complete(payloadOffset, payloadLength)` marks the
frame found and names the payload slice within the accumulated buffer.

### Message Detection Pattern

The detect function is the key abstraction that makes the library flexible.
Rather than baking framing logic into the transport, the *caller* decides when
a complete message has arrived:

```
detectMessage(accumulatedBytes) → FrameDetectionResult
    Complete(offset, length)  →  return bytes[offset..offset+length]
    Incomplete                →  keep reading
```

Built-in convenience methods provide detect functions for common patterns:

| Method | Detection strategy |
|---|---|
| `ReceiveExactly(n)` | `buffer.Length >= n` |
| `ReceiveUntilTerminator(char)` | `IndexOf((byte)terminator)` |
| `ReceiveUntilTerminatorPattern(bytes)` | `Span.IndexOf(pattern)` |
| `ReceiveUntilPerfectMatch(bytes)` | `Span.IndexOf(matchBytes)` |
| `ReceiveUntilHeaderFooterMatch(h, f)` | `IndexOf(header)` then `IndexOf(footer)` after header |
| `SendReceive(…, detectMessage)` | Caller-supplied function |

`ReceiveUntilIdle` is the exception: it frames on silence rather than on content,
for unsolicited or streaming data (barcode scanners, GPS NMEA sentences) where the
gap between bytes is the frame boundary.

---

## Transport Implementations

Each transport package provides a single `IDuplexPipe`. The caller owns the
underlying transport resource and its lifecycle.

### `SerialDuplexPipe`

Wraps an already-open `RJCP.IO.Ports.SerialPortStream`. Implements
`IAsyncDisposable`; disposing it stops the background read pump, not the port.

```csharp
using var port = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One);
port.Open();
await using var pipe = new SerialDuplexPipe(port);
var transceiver = new Transceiver(pipe);
```

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

Request frames are built with the internal `ModbusRtuRequestBuilder` (fluent
builder pattern with CRC-16 computation via `ModbusUtils`). Response validation
checks unit identifier, function code, and exception flags. Modbus exceptions
are surfaced as `ModbusProtocolException` with a typed `ModbusProtocolExceptionCode`.

### STM32 Bootloader (`CallAndResponse.Protocol.Stm32Bootloader`)

`Stm32BootloaderClient` implements the STM32 system bootloader command set
(AN3155) including:

- `Ping` — Initial synchronization (0x7F → ACK/NACK)
- `GetSupportedCommands` — Protocol version and command list
- `GetId` — Chip ID
- `ReadMemory` — Read flash/RAM in 256-byte pages
- `WriteMemory` — Write flash/RAM in 256-byte pages
- `ExtendedEraseMass` — Extended erase, AN3155 mass-erase code `0xFFFF`
- `ExtendedEraseBank` — Extended erase, AN3155 bank codes `0xFFFE` / `0xFFFD`
- `ExtendedErasePages` — Extended erase of an explicit page list
  (`ExtendedEraseMemoryPages` is the deprecated pages-`0..N` form of this; see
  [ADR-0016](adr/adr-0016-stm32-extended-erase-api-shape.md))

---

## Exception Hierarchy

```
Exception
├── TransceiverTransportException      I/O-level failures during send/receive
│                                      (write failed, disconnected mid-transfer)
│
├── ModbusProtocolException            Modbus exception response from the device
│   └── .ExceptionCode                 (typed ModbusProtocolExceptionCode enum)
│
├── ModbusFramingException             Modbus response framing error (unit ID or
│                                      function code mismatch)
│
└── ModbusTransportException           Wraps TransceiverTransportException for
                                       Modbus-specific context
```

---

## Design Patterns

| Pattern | Where | Purpose |
|---|---|---|
| **Strategy (via detect function)** | `ReceiveMessage` parameter | Caller injects framing logic as a `Func<>` delegate rather than subclassing |
| **Composition** | `IDuplexPipe.AsTransceiver()` | Wrap any duplex pipe in a transceiver without inheritance |
| **Builder** | `ModbusRtuRequestBuilder` | Fluent frame construction |
| **Dependency Inversion** | Protocol → `ITransceiver` | Protocols depend on the abstraction, never on a concrete transport |

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
2. Accept `ITransceiver` via constructor injection.
3. Use the `SendReceive*` convenience methods to implement protocol operations.
4. Define protocol-specific exceptions.

### Custom framing

Pass a custom `Func<ReadOnlyMemory<byte>, FrameDetectionResult>` to `SendReceive`
or `ReceiveMessage` for protocol framing that doesn't match any built-in pattern.

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
