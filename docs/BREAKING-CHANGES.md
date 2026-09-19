# Breaking changes

Each release that changes what a caller sees gets a section here, newest first. An entry
says what changed, who it affects, and what to write instead. Entries within a release
are ordered by how likely you are to hit them.

A removed or changed signature fails your build, so it announces itself. A behaviour
change does not, so behaviour changes are listed too and are called out where they
appear.

CallAndResponse is pre-1.0 in spirit while the 2.x line is in alpha: the public surface
moves between alpha releases, and this file is what that owes you in return. Releases
before `v2.0.0-alpha.7` are not described here.

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
