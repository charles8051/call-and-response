# Breaking changes

Each release that changes what a caller sees gets a section here, newest first. An entry
says what changed, who it affects, and what to write instead. Entries within a release
are ordered by how likely you are to hit them.

A removed or changed signature fails your build, so it announces itself. A behaviour
change does not, so behaviour changes are listed too and are called out where they
appear.

CallAndResponse is pre-1.0 in spirit while the 2.x line is in alpha: the public surface
moves between alpha releases, and this file is what that owes you in return. Releases
before `v2.0.0-alpha.7` have no section of their own. Coming from `v1.1.1`, the last stable
1.x release, start at [Upgrading from `v1.1.1`](#upgrading-from-v111) at the end of this
file, which maps that surface directly onto 2.x.

**While a change is in flight**, add its entry under a `## Unreleased` heading. Retitle
that heading to the version in the release commit — `.github/workflows/publish.yml`
refuses a tag whose docs still say `Unreleased`, because a release whose breaking-changes
doc calls its own entries pending reads as a promise that nothing shipped.

## `v2.0.0-alpha.7` — since `v2.0.0-alpha.6`

The framing rewrite in [ADR-0020](adr/adr-0020-framing-codec-abstraction.md) is the bulk
of this release. Entries 1 through 3 are direct consequences of it, and so are 8 and 9. It supersedes
[ADR-0017](adr/adr-0017-frame-consumed-length.md), whose `ConsumedLength` addition shipped
and was deleted inside the same release.

### 1. `ITransceiver` receives through an `IFrameDecoder`, and `FrameDetectionResult` is gone

A detector *described* where the payload sat inside the received bytes, as three offsets.
That cannot express a framing whose payload is not a contiguous slice of the wire — a SLIP
frame `C0 41 DB DC 42 C0` carries the payload `41 C0 42`, which appears nowhere in the
buffer. A decoder *produces* the payload instead.

| Member | Before | After |
|---|---|---|
| `ITransceiver.ReceiveMessage(Func<ReadOnlyMemory<byte>, FrameDetectionResult>, CancellationToken)` | present | removed — use `Receive(IFrameDecoder, CancellationToken)` |
| `ITransceiver.ReceiveUntilIdle(TimeSpan, CancellationToken)` | present | removed — use `Receive(Frame.UntilIdle(gap), token)` |
| `ITransceiver.Receive(IFrameDecoder, IBufferWriter<byte>, CancellationToken)` | — | added, writes into a caller-owned buffer |
| `FrameDetectionResult` | public struct | removed |

```csharp
// Before
var reply = await transceiver.ReceiveMessage(
    buffer =>
    {
        var end = buffer.Span.IndexOf((byte)'\n');
        return end < 0
            ? FrameDetectionResult.Incomplete
            : FrameDetectionResult.Complete(0, end);
    },
    token);

// After
var reply = await transceiver.Receive(Frame.UntilTerminator((byte)'\n'), token);
```

A decoder you write yourself is `Frame.Custom(decode)`, or `Frame.OverSpan(decode)` if you
would rather have the bytes flattened than write against `SequenceReader<byte>`. `Decode`
is required to be total — return `FrameDecodeResult.Invalid(consumed, reason)` rather than
throwing — and to be a pure function of its `FrameContext`.

### 2. The `Receive*` and `SendReceive*` catalogue collapsed into `SendReceive` plus `Frame`

Twelve extension methods were one `Send` cross-produced with one of five detectors. They
are replaced by `SendReceive(writeBytes, decoder)` and a decoder catalogue that composes.

| Before | After |
|---|---|
| `ReceiveExactly(n, token)` | `Receive(Frame.Exactly(n), token)` |
| `ReceiveUntilTerminator(ch, token)` | `Receive(Frame.UntilTerminator((byte)ch), token)` |
| `ReceiveUntilTerminatorPattern(pattern, token)` | `Receive(Frame.UntilPattern(pattern), token)` |
| `ReceiveUntilPerfectMatch(match, token)` | `Receive(Frame.UntilPattern(match, keepInPayload: true), token)` — see below |
| `ReceiveUntilHeaderFooterMatch(header, footer, token)` | `Receive(Frame.Between(header, footer), token)` |
| `SendReceiveExactly(w, n, token)` | `SendReceive(w, Frame.Exactly(n), token)` |
| `SendReceivePerfectMatch(w, match, token)` | `SendReceive(w, Frame.UntilPattern(match, keepInPayload: true), token)` — see below |
| `SendReceiveFooter(w, pattern, token)` | `SendReceive(w, Frame.UntilPattern(pattern), token)` |
| `SendReceiveHeaderFooter(w, header, footer, token)` | `SendReceive(w, Frame.Between(header, footer), token)` |
| `SendReceive(w, detectMessage, token)` | `SendReceive(w, Frame.Custom(decode), token)` |
| `SendReceiveString(w, char, token)` | unchanged |
| `SendReceiveString(w, string, token)` | unchanged |

**The two perfect-match rows are the one pair that is not an exact translation.**
`ReceiveUntilPerfectMatch` returned *only* the matched bytes and silently discarded
everything that preceded them, so its result was the constant you passed in.
`Frame.UntilPattern(match, keepInPayload: true)` returns the preceding bytes as well. If
you were using it as "wait for this exact acknowledgement", the new payload's last
`match.Length` bytes are the old return value, and the bytes before them are what the old
call was throwing away.

`Frame` also carries what the old set could not express: `Frame.LengthPrefixed(...)`, and
the combinators `WithIdleTimeout`, `WithMaxLength` and `Validated`. Modbus RTU's real
framing rule is `Frame.UntilIdle(gap).Validated(crc)`, which had no expression before.

> **`WithIdleTimeout` is a deadline, not a fallback framing.** On the gap it asks the
> inner decoder once more with the transport presented as complete. It never hands back
> the raw buffered bytes in the decoder's place, because that would skip unescaping,
> checksums and anything `Validated` wrapped around it. To frame *on* silence, use
> `Frame.UntilIdle(gap)`, which is a different question.

**Also behavioural.** `ReceiveUntilTerminator`, `ReceiveUntilTerminatorPattern` and
`ReceiveUntilHeaderFooterMatch` left the delimiter they matched in the pipe, where it
satisfied the next command's detector and shifted every subsequent reply by a frame
(issue #7). Their `Frame` replacements consume it. Code written around the old
behaviour — a caller that expected a stray leading delimiter on the next read, or that
skipped one — now sees it gone.

### 3. `ModbusRtuClient` takes a `ModbusRtuChannel`, not an `ITransceiver`

Modbus RTU's framing is a property of the link rather than something a caller chooses per
read, so the client now sits on `IMessageTransceiver` through a channel that owns the
codec and the inter-frame gap.

```csharp
// Before
var client = new ModbusRtuClient(transceiver);

// After
var client = new ModbusRtuClient(ModbusRtu.Channel(transceiver, baudRate: 115200));
```

`ModbusRtu.Channel(transceiver, interFrameGap)` takes the gap directly if you do not want
it derived from the baud rate. `ModbusRtu.GapFor(baudRate)` is the derivation on its own,
and `ModbusRtu.Codec(gap)` is the `IFrameCodec` if you want to bind it yourself with
`transceiver.WithFraming(codec)`.

### 4. `Stm32BootloaderClient.GetId` returns `ushort`, and returns the product id

It read byte `[4]` of the five-byte AN3155 reply, which is the closing ACK, so it returned
`0x79` for every part (issue #6). The id is bytes `[2..3]`, and an STM32 product id is 12
bits, so the return type widens from `byte` to `ushort` to hold it.

```csharp
ushort productId = await client.GetId(token);   // 0x413 on an F4, 0x410 on F1 medium-density
```

The reply's framing — leading ACK, `N = 0x01`, trailing ACK — is now checked before bytes
2 and 3 are combined, so a stream left out of sync by an earlier command throws
`Stm32BootloaderException` rather than handing back a plausible-looking wrong id.

### 5. `Stm32BootloaderClient.GetProtocolVersion` returns `Task<Stm32VersionInfo>`

It was `Task`, and discarded the reply it had already read. `Stm32VersionInfo` carries
`Version`, `OptionByte1`, `OptionByte2`, and `MajorVersion` / `MinorVersion` split out of
the version nibbles. `await client.GetProtocolVersion(token);` still compiles; anything
that stored the returned `Task` in a `Task`-typed variable does not.

### 6. Five commands are declared and non-callable

`WriteProtect`, `WriteUnprotect`, `ReadoutProtect`, `EraseMemory(address, length)` and the
no-argument `GetChecksum()` carry `[Obsolete(..., error: true)]`, so a call is a compile
error. None of them were ever implemented — they threw `NotImplementedException` at
runtime. The three protection commands are not shipped without hardware to verify them
against, because each can leave a part this library cannot recover. The other two had no
workable signature: `GetChecksum()` needs an address, a size, a CRC polynomial and a seed,
and command `0x43` addresses flash by single-byte page code rather than by address and
length.

| Non-callable | Replacement |
|---|---|
| `Task GetChecksum(CancellationToken)` | `Task<uint> GetChecksum(uint address, uint numWords, uint crcPolynomial = 0x04C11DB7, uint crcInitialValue = 0xFFFFFFFF, CancellationToken)` |
| `Task EraseMemory(uint address, ushort length, CancellationToken)` | `Task EraseMemory(IEnumerable<byte> pageNumbers, CancellationToken)`, or `Task EraseAllMemory(CancellationToken)` |
| `Task WriteProtect(CancellationToken)` | none |
| `Task WriteUnprotect(CancellationToken)` | none |
| `Task ReadoutProtect(CancellationToken)` | none |

> **The two `EraseMemory` overloads are not two spellings of one operation.** The obsolete
> one took a flash address and a byte length. The replacement takes **AN3155 page codes** —
> the device's own single-byte page numbers, as its reference manual defines them — and
> erases exactly the pages listed. It does not interpret an address, and it does not derive
> pages from one. Passing an address, or a length, erases whichever pages happen to carry
> those numbers. The mapping from an address range to page codes needs a per-device flash
> layout this library does not have, which is why the address form was never implemented
> and is not being replaced by an equivalent.

```csharp
// After
uint crc = await client.GetChecksum(address, numWords, token: token);
await client.EraseMemory(new byte[] { 0, 1, 2 }, token);   // erases pages 0, 1 and 2
await client.EraseAllMemory(token);                        // AN3155 global erase
```

Check `GetSupportedCommands` first: `0x43` is a pre-3.0 USART bootloader command, and from
3.0 onwards the device exposes Extended Erase (`0x44`) instead — the `ExtendedErase*` family
below.

**What "declared and non-callable" means, precisely.** The signature is still in the
assembly, and its body still throws `NotImplementedException` — exactly what it did before.
Nothing gained an implementation, and nothing touches the device.

- **Compiling against this package**: the call is a **compile error**. There is no suppression
  intended; use the replacement.
- **A binary already compiled against an earlier package**: the method still resolves, so the
  caller loads and runs, and the call throws `NotImplementedException` at the call site. That
  is the reason the signatures are kept rather than deleted — deleting them turns a reachable
  exception into a `MissingMethodException` that fails to JIT the whole calling method.

So no route through `EraseMemory(address, length)` erases anything, on any part, in either
case. There is no address-based erase in this library to reach.

`ReadoutUnprotect` **is** implemented, and mass erases the flash. That is the mechanism of
leaving RDP level 1, not a side effect that can be avoided.

### 7. An STM32 NACK throws instead of blocking until cancellation

> **This one does not announce itself.** Every command waited for the ACK byte to *appear*
> in the stream. A NACK is not an ACK, so it never satisfied the wait and the call blocked
> until the token fired (issue #22). A rejected command was indistinguishable from a dead
> link, and both surfaced as `OperationCanceledException` — or as a hang, if the caller
> passed `CancellationToken.None`.

All seven ACK checks now read exactly one status byte and inspect it.
`Stm32BootloaderException` is thrown for a NACK, for an unexpected byte, and for no byte at
all, with the command name and the byte in the message.

```csharp
// Before — a NACK from the device reached you as this, if at all
catch (OperationCanceledException) { /* cancelled? rejected? no way to tell */ }

// After
catch (Stm32BootloaderException ex) { /* "Go: the device answered NACK (0x1F)" */ }
```

`Ping` is the same change at the handshake (issue #9). It is the call most likely to be
talking to something that is not an STM32 bootloader at all, and it reported that by
throwing a bare `OperationCanceledException`. A `catch (OperationCanceledException)` that
treated a rejection as "the user cancelled" now sees `Stm32BootloaderException` escape.

### 8. A Modbus exception response parses instead of hanging

> **This one does not announce itself.** A five-byte exception response is shorter than the
> normal reply the old client was waiting for, so it read past the end of the frame and
> waited for bytes that were never coming (issue #21).

`ModbusRtuClient` now frames on the inter-frame gap through `ModbusRtuChannel`, where an
exception response is simply a shorter complete frame. A device reporting
`IllegalDataAddress` raises `ModbusProtocolException` at the call, where before the call
blocked until cancellation. Code that treated a timeout on a Modbus read as "device
offline" now sees the protocol exception instead, which is the more specific answer and a
different `catch`.

### 9. `WithIdleTimeout` ends a frame on silence rather than waking and continuing

> **This one does not announce itself.** It armed the idle timer and nothing else. The
> content decoders ignore `IsIdle`, so `Frame.UntilTerminator(x).WithIdleTimeout(gap)` woke
> at the gap and went on asking for a terminator that was never coming.

It is now a real deadline: silence with bytes in hand asks the inner decoder once more with
the transport presented as complete. A decoder that can finish there does; one that cannot
throws. A caller relying on the old shape to keep waiting past the gap now gets a result or
an exception at the gap.

The timer itself also no longer cancels the read after the one it was armed for (issue
#23). Arming is CAS-guarded, so a gap that elapses while the next read is already in flight
cannot cut that read short.

### 10. A failed serial read surfaces its exception instead of a clean EOF

> **This one does not announce itself.** `SerialDuplexPipe`'s read pump caught every
> exception and completed the writer with no argument, so a port that failed mid-session
> was indistinguishable from one that closed cleanly (issue #11).

Both cases reported `TransceiverTransportException` with "transport closed before frame was
complete", and the real cause was discarded at the `catch`. The pump now hands the exception
to `PipeWriter.Complete(failure)`, so the original — an `IOException` from the driver, an
`UnauthorizedAccessException` on a yanked USB adapter — reaches the caller instead. Code
that only caught `TransceiverTransportException` around a receive now sees those escape.

Cancellation is still a clean completion, and the filter is keyed on the exception's own
token rather than on "is a shutdown under way", so a driver aborting a read for its own
reasons at the instant `DisposeAsync` runs is preserved as a failure.

### Deprecated, still working

`ExtendedEraseMemoryPages(numPages)` always emitted an explicit page list, so AN3155 §3.7's
special codes — `0xFFFF` mass, `0xFFFE` bank 1, `0xFFFD` bank 2 — were unreachable, and
passing `0xFFFF` built 65537 half-words instead of one
([ADR-0016](adr/adr-0016-stm32-extended-erase-api-shape.md)). The erase forms are now
separate methods:

| Instead of | Write |
|---|---|
| `ExtendedEraseMemoryPages(n)` | `ExtendedErasePages(pages)` — the pages you want erased, not a count |
| — | `ExtendedEraseMass()` |
| — | `ExtendedEraseBank(bank)` |

`ExtendedEraseMemoryPages` is `[Obsolete]` as a warning, not an error, and its bytes on the
wire are unchanged for every input it previously accepted — including `0xFFFD`–`0xFFFF`,
which built a malformed page-list frame and still do. A deprecation is not entitled to
change behaviour.

`ExtendedErasePages` takes the page numbers rather than a count, which removes the
off-by-one in `numPages` and lifts the old requirement that erasure start at page 0.

## Upgrading from `v1.1.1`

`v1.1.1` is the last stable 1.x release. Nothing between it and `v2.0.0-alpha.7` has a
section of its own, so this one maps the 1.1.1 surface directly onto the current 2.x
surface, and is kept current with it. It covers the four packages 1.1.1 published:
`CallAndResponse`, `CallAndResponse.Transport.Serial`, `CallAndResponse.Protocol.Modbus` and
`CallAndResponse.Transport.Ble`. `CallAndResponse.Protocol.Stm32Bootloader` had no stable
1.x release; its changes are in the per-release sections above.

In 1.1.1 a transceiver owned the port. It opened and closed it, polled it for bytes, and
dropped whatever arrived after the message it was looking for. In 2.x you own the port, the
library reads from an `IDuplexPipe` over it, and each receive takes a decoder that says where
the frame ends. Most of the entries below follow from that.

### 1. Every package targets `net8.0` only

1.1.1 shipped `netstandard2.0` and `netstandard2.1`. 2.x ships `net8.0` alone, so a project
on .NET Framework, .NET 6 or 7, Xamarin or Unity fails restore with `NU1202`. Those projects
stay on 1.1.1 or move to .NET 8.

### 2. Upgrade the packages together

> **This one does not announce itself.** Each 1.1.1 package depends on `CallAndResponse`
> with the open range `>= 1.1.1`, so NuGet restores a 1.1.1 transport or protocol package
> next to a 2.x core with no warning. A 1.1.1 `CallAndResponse.Protocol.Modbus` then builds
> too, and fails at runtime when `ModbusRtuClient` calls
> `ITransceiver.SendReceive(ReadOnlyMemory<byte>, int, CancellationToken)`, which 2.x does
> not declare.

A 1.1.1 `CallAndResponse.Transport.Serial` does fail the build, with `CS0012` asking for a
reference to `Serilog`. That error names the wrong fix. Adding Serilog lets it build, and
`SerialPortTransceiver` then fails at runtime, because it derives from `Transceiver`, which
2.x seals.

Move `CallAndResponse`, `CallAndResponse.Transport.Serial` and
`CallAndResponse.Protocol.Modbus` to the same 2.x version in one change. Remove
`CallAndResponse.Transport.Ble`, which has no 2.x release (entry 11).

### 3. You open and close the port

1.1.1's `ITransceiver` had `Open`, `Close` and `IsOpen`, and `SerialPortTransceiver` built
its own `System.IO.Ports.SerialPort`. 2.x has no lifecycle members. You open an
`RJCP.IO.Ports.SerialPortStream`, wrap it in a `SerialDuplexPipe`, and hand that to a
`Transceiver`.

```csharp
// Before
var transceiver = new SerialPortTransceiver("COM5", 115200);
await transceiver.Open(token);
// ...
await transceiver.Close(token);

// After
using RJCP.IO.Ports;

using var port = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One);
port.Open();
await using var pipe = new SerialDuplexPipe(port);
var transceiver = new Transceiver(pipe);
```

`Parity` and `StopBits` now come from `RJCP.IO.Ports`, not `System.IO.Ports`, and the
constructor takes data bits before parity. Disposing the `SerialDuplexPipe` stops its read
pump and leaves the port open. 1.1.1's `Send` and `ReceiveMessage` reopened a closed port on
their own; 2.x never does.

| 1.1.1 | 2.x |
|---|---|
| `ITransceiver.Open`, `Close`, `IsOpen` | none. Open and close the port yourself |
| `SerialPortTransceiver(portName, baudRate, parity, dataBits, stopBits, logger)` | `new Transceiver(new SerialDuplexPipe(openPort))` |
| `TransceiverFactory`, `CreateSerialTransceiver(portName)` | as above |
| `WindowsSerialPortTransceiver(vid, pid, ...)`, `CreateCP210xTransceiver(...)`, `CreateWindowsSerialTransceiver(vid, pid)` | none. Resolve the port name yourself |
| `SerialPortUtils.FindPortNameById(vid, pid)`, `SerialPortUtils.GetCp210xComPort()` | none |
| `abstract class Transceiver`, subclassed for a custom transport | `sealed class Transceiver` over an `IDuplexPipe`. Provide the pipe instead |

The VID/PID lookup was a Windows-only WMI query, and device discovery is now out of scope
([ADR-0009](adr/adr-0009-device-discovery-out-of-scope.md)). Resolve the port name with a
discovery library and pass it to `SerialPortStream`. `System.Management` is no longer a
dependency.

A custom transport that is already a `Stream` needs no pipe class of its own:

```csharp
var transceiver = new Transceiver(PipeReader.Create(stream), PipeWriter.Create(stream));
```

### 4. Send and receive take a decoder

`ITransceiver` is now `Send` plus two `Receive(IFrameDecoder, ...)` overloads. The five
`SendReceive` overloads were interface members in 1.1.1. Their replacements are extension
methods in `TransceiverExtensions`, with the framing passed as a value from `Frame` in
`CallAndResponse.Framing`.

| 1.1.1 | 2.x |
|---|---|
| `SendReceive(string, char, token)` | `SendReceiveString(string, char, token)` |
| `SendReceive(string, string, token)` | `SendReceiveString(string, string, token)` |
| `SendReceive(bytes, int numBytesExpected, token)` | `SendReceive(bytes, Frame.Exactly(numBytesExpected), token)` |
| `SendReceive(bytes, ReadOnlyMemory<byte> pattern, token)` | `SendReceive(bytes, Frame.UntilPattern(pattern), token)` |
| `SendReceive(bytes, Func<ReadOnlyMemory<byte>, int>, token)` | `SendReceive(bytes, decoder, token)`. See entry 6 |
| `ReceiveMessage(Func<ReadOnlyMemory<byte>, int>, token)` | `Receive(decoder, token)`. See entry 6 |

The returned payloads match: both versions leave the terminator or pattern out. What happens
to the bytes after it has changed, in entry 5.

A test double that implemented `ITransceiver` now implements `Send` and the two `Receive`
overloads, and nothing else.

### 5. Bytes after a frame wait for the next receive

> **This one does not announce itself.** 1.1.1 read each reply into a fresh buffer and
> returned the start of it. Everything after the message in that buffer was dropped with it:
> the terminator, a `\n` after a `\r`, a second reply that arrived in the same read. 2.x
> consumes the frame and its delimiter and leaves the rest in the pipe, where the next
> receive starts.

A device that answers `OK\r\n`, read with a `'\r'` terminator, shows the difference. 1.1.1
lost the `\n` when it arrived in the same read as the `\r`, and returned it at the start of
the next reply when it did not. 2.x returns `\nOK` for the second reply every time.
Terminate on the whole sequence:

```csharp
var reply = await transceiver.SendReceiveString(command, "\r\n", token);
```

Two 1.1.1 hangs are gone for the same reason. `SendReceive(bytes, n, token)` completed only
when the buffer held exactly `n` bytes, so a device that sent more before the read returned
was waited on until cancellation or the 1024-byte cap (entry 7). `Frame.Exactly(n)` returns
the first `n` and keeps the rest. An empty reply, where the terminator is the first byte,
never completed either, because a detector result of `0` meant "keep reading". It now
returns an empty payload.

### 6. A custom detector becomes a decoder

1.1.1's `Func<ReadOnlyMemory<byte>, int>` returned the length of the message at the head of
the buffer, or `0` to keep reading. An `IFrameDecoder` writes the payload and reports how
many bytes it consumed. Check the `Frame` catalogue first: `LengthPrefixed`, `Between`,
`UntilIdle` and the `Validated` combinator cover most hand-written detectors. For one they
do not, this adapter keeps the 1.1.1 function as it is:

```csharp
using System.Buffers;
using CallAndResponse.Framing;

static IFrameDecoder FromDetector(Func<ReadOnlyMemory<byte>, int> detectMessage) => Frame.OverSpan(
    (received, isIdle, isTransportComplete, payload) =>
    {
        int length = detectMessage(received.ToArray());
        if (length <= 0 || length > received.Length) return FrameDecodeResult.NeedMoreData;
        payload.Write(received[..length]);
        return FrameDecodeResult.Frame(length);
    });

var reply = await transceiver.SendReceive(request, FromDetector(detectMessage), token);
```

A length longer than the buffer waits for more bytes, where 1.1.1 returned what it had. The
adapter copies the buffer on every call; `Frame.Custom` reads the `ReadOnlySequence<byte>`
without one.

### 7. The receive buffer has no size cap

> **This one does not announce itself.** 1.1.1's serial transport threw `IOException` with
> "buffer overflow" once 1024 bytes arrived without a message. 2.x buffers until the decoder
> finds a frame or the token fires, so a peer that never sends the delimiter grows memory
> instead of failing.

Put a cap back with `WithMaxLength`, which throws `FramingException`:

```csharp
var line = await transceiver.Receive(Frame.UntilTerminator((byte)'\n').WithMaxLength(1024), token);
```

### 8. Logging uses `Microsoft.Extensions.Logging`

1.1.1 took a `Serilog.ILogger`, and built a Serilog console logger when given none. 2.x
takes `ILogger<T>` from `Microsoft.Extensions.Logging` and logs nothing unless you pass one.

| 1.1.1 | 2.x |
|---|---|
| `SerialPortTransceiver(..., Serilog.ILogger logger)` | `new Transceiver(pipe, ILogger<Transceiver> logger)` |
| `ModbusRtuClient` built its own console logger | `new ModbusRtuClient(channel, ILogger<ModbusRtuClient> logger)` |

`Serilog` and `Serilog.Sinks.Console` no longer arrive as transitive dependencies. Code that
used them through CallAndResponse needs its own package reference.
`Serilog.Extensions.Logging` bridges a Serilog logger into the 2.x constructors.

### 9. `ModbusRtuClient` takes a `ModbusRtuChannel`

1.1.1's client took an `ITransceiver`, and `IModbusClient` had `Open` and `Close`. 2.x's
client takes a `ModbusRtuChannel`, which binds RTU framing (the CRC and the inter-frame gap)
to a transceiver. `Open` and `Close` are gone, because the port is yours (entry 3).

```csharp
// Before
var client = new ModbusRtuClient(transceiver);
await client.Open(token);

// After
var client = new ModbusRtuClient(ModbusRtu.Channel(transceiver, baudRate: 115200));
```

`ReadHoldingRegisters` and `WriteRegisters` keep their signatures and their byte order: each
16-bit register is byte-swapped on the way in and on the way out, as before.
`ModbusTransportException` is now public, so it can be caught by type.

### 10. Modbus requests and responses are checked

> **This one does not announce itself.** The signatures are unchanged. What goes over the
> wire, and what comes back as an exception, are not.

- **`WriteRegisters` sends a valid FC16 request.** 1.1.1 put the byte count in the quantity
  field, left out the byte-count field, and waited for 5 reply bytes where the device sends
  8. 2.x sends the register count, the byte count and the data, and checks the reply.
- **Response CRCs are verified.** 1.1.1 never checked them, so a corrupted reply came back as
  register values. 2.x throws `ModbusFramingException`.
- **An exception response raises `ModbusProtocolException`.** 1.1.1's `ReadHoldingRegisters`
  waited for a full-length reply that never came, and `WriteRegisters` threw
  `IndexOutOfRangeException` reading the exception code. 2.x frames on the inter-frame gap,
  so the short reply parses, and `ExceptionCode` carries the device's code.

### 11. The BLE transport has no 2.x package

`CallAndResponse.Transport.Ble` (`BleNordicUartTransceiver`, `CreateBleTransceiver`) was
published up to 1.6.1-alpha and has no 2.x release. Its 2.x counterpart, `BleNordicUartPipe`
in `CallAndResponse.Transport.BleNordicUart`, is not published; reference the project or copy
the file. It pairs two pipes and does nothing else. Your code owns the BLE connection, the
notification handler that writes into `RxWriter`, and the loop that drains `TxReader` to the
characteristic. [ADR-0021](adr/adr-0021-drop-transport-packages.md) proposes removing it.

### Removed without a replacement

| 1.1.1 | Instead |
|---|---|
| `ArrayExtensions.Locate(byte[], byte[])` | `MemoryExtensions.IndexOf` finds the first match |
| `TransceiverConnectionException` | nothing. 2.x never opens a connection, so nothing throws it |
| `TransceiverFactory` | see entry 3 |
