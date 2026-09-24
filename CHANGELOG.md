# Changelog

All notable changes to CallAndResponse are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Every release gets a section here**, including one that changes nothing a caller can see —
`.github/workflows/publish.yml` refuses a tag whose newest heading is not that version.
[docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md) is the other half: what a break costs
you and what to write instead, for the releases that have one.

## 2.0.0-alpha.7 - 2026-09-03

Framing becomes a bidirectional codec. The migration for every entry marked **Breaking** is
in [docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md#v200-alpha7--since-v200-alpha6).

**Correction, 2026-09-23.** This section first listed "`SerialDuplexPipe` over both serial
backends — the BCL `SerialPort` and RJCP's `SerialPortStream` (ADR-0019)" under Added. That
was false, and the entry is removed. `CallAndResponse.Transport.Serial` 2.0.0-alpha.7 contains
one public type, `SerialDuplexPipe`, whose only constructor takes an
`RJCP.IO.Ports.SerialPortStream`. Its public surface is unchanged since 2.0.0-alpha.5, and the
package's one behavioral change in this release is the #11 read-pump fix under Fixed. No
release ships ADR-0019's `System.IO.Ports` backend.

### Added
- **`IFrameEncoder` and `IFrameCodec`** — the send-side seam `ITransceiver` never had. Every
  `SendReceive*` handed the caller's bytes to `Send` unmodified, so framing was modelled as a
  receive-only concern. That is false for SLIP and RFC 1662 async HDLC, which must escape on
  the way out, and for HDLC, which must append an FCS (ADR-0020).
- **`IMessageTransceiver`, `WithFraming` and `AsByteStream`.** `ITransceiver` is the byte
  channel whose reads are caller-directed; `IMessageTransceiver` is the message channel whose
  framing is a property of the link and fixed for its lifetime. A self-delimiting link cannot
  give `Receive(decoder)` a meaning and a raw byte link cannot give `SendMessage` one, so they
  are two interfaces with explicit adapters rather than one interface with dead members.
- **`SlipCodec` (RFC 1055) and `HdlcCodec` (RFC 1662)**, in the core package under
  `CallAndResponse.Framing`. Neither adds a dependency, so neither earns a package. The FCS
  lives inside `HdlcCodec` rather than in a stackable CRC layer, because RFC 1662 computes it
  over the *unescaped* frame and then escapes it — a CRC layer above an escaping layer would
  compute over escaped bytes and get the RFC wrong. Verified against the published
  CRC-16/X-25 check value, not a round trip.
- **The `Frame` decoder catalogue**: `Exactly`, `UntilTerminator`, `UntilPattern`, `Between`,
  `UntilIdle`, `UntilTransportComplete`, `LengthPrefixed`, `Custom` and `OverSpan`, plus the
  `WithIdleTimeout`, `WithMaxLength` and `Validated` combinators. `LengthPrefixed` and the
  combinators are new capability rather than a rename: Modbus RTU's real framing rule is
  `Frame.UntilIdle(gap).Validated(crc)`, which the detector API could not express at all.
- **`ModbusRtu.Channel`, `ModbusRtu.Codec` and `ModbusRtu.GapFor`** — RTU framing as a codec
  that owns the CRC and the inter-frame gap.
- **Four AN3155 commands implemented**: `GetChecksum(address, numWords, ...)`,
  `EraseMemory(pageNumbers)`, `EraseAllMemory`, and the `ExtendedEraseMass` /
  `ExtendedEraseBank` / `ExtendedErasePages` split (ADR-0016, ADR-0018).
- **`Stm32VersionInfo`** and **`Stm32BootloaderException`**.

### Changed
- **Breaking: frame detection is replaced by frame decoding, and `FrameDetectionResult` is
  removed** (ADR-0020). A detector described where the payload sat, as three offsets into the
  received bytes. That cannot express a framing whose payload is not a contiguous slice of the
  wire: a SLIP frame `C0 41 DB DC 42 C0` carries the payload `41 C0 42`, which appears nowhere
  in the buffer. An `IFrameDecoder` produces the payload into an `IBufferWriter<byte>` instead.
- **Breaking: `ITransceiver` is `Send` plus `Receive(IFrameDecoder)`.** `ReceiveMessage` and
  `ReceiveUntilIdle` are gone, along with the twelve `Receive*` / `SendReceive*` extension
  methods that were one `Send` cross-produced with one of five detectors.
- **Breaking: `ModbusRtuClient` takes a `ModbusRtuChannel`**, from
  `ModbusRtu.Channel(transceiver, baudRate)`, not an `ITransceiver`. It had been reading every
  reply as a CRC-checked, gap-delimited RTU frame while accepting any channel that satisfied
  the interface, so a caller could hand it a SLIP link and get requests with no CRC and
  responses that were never validated, silently. The framing is now guaranteed by the type.
- **Breaking: `Stm32BootloaderClient.GetId` returns `ushort`** (was `byte`), and
  **`GetProtocolVersion` returns `Task<Stm32VersionInfo>`** (was `Task`).
- **Breaking: five never-implemented commands are `[Obsolete(error: true)]`** — `WriteProtect`,
  `WriteUnprotect`, `ReadoutProtect`, `EraseMemory(address, length)` and the no-argument
  `GetChecksum()`. Each threw `NotImplementedException` at runtime. The three protection
  commands can leave a part this library cannot recover, so they are not shipped without
  hardware to verify them against; the other two had no workable signature (#10).
- **`ExtendedEraseMemoryPages` is `[Obsolete]` as a warning**, delegating to
  `ExtendedErasePages`. Its bytes on the wire are unchanged for every input it previously
  accepted, including `0xFFFD`–`0xFFFF`, which built a malformed page-list frame and still do.
  A deprecation is not entitled to change behaviour.
- The receive loop passes the decoder a pooled staging writer and commits to the caller's
  destination only on `Frame`, so a decoder that writes and then asks for more data cannot
  duplicate its output, and one that writes and then rejects cannot leak bytes into the
  caller's buffer. The buffer is a `ReadOnlySequence<byte>` rather than a copy.

### Fixed
- **A Modbus exception response hung the call** (#21). RTU frames on the inter-frame gap, so a
  five-byte exception response is a complete frame; the old client read past its end and waited
  for bytes that were never coming. It now raises `ModbusProtocolException` at the call.
- **An STM32 NACK blocked until cancellation** (#22). Every command waited for the ACK byte to
  *appear* in the stream, and a NACK never satisfied that wait, so a rejected command was
  indistinguishable from a dead link — and hung outright under `CancellationToken.None`. All
  seven ACK checks now read one status byte and throw `Stm32BootloaderException` naming the
  command and the byte.
- **The idle timer could cancel the read after the one it was armed for** (#23). Arming is now
  CAS-guarded.
- **`WithIdleTimeout` woke at the gap and carried on waiting.** The content decoders ignore
  `IsIdle`, so `Frame.UntilTerminator(x).WithIdleTimeout(gap)` went on asking for a terminator
  that was never coming. Silence with bytes in hand now asks the inner decoder once more with
  the transport presented as complete. It never returns the raw buffered bytes in the decoder's
  place, which would bypass unescaping, checksums and anything `Validated` wrapped around it.
- **`Stm32BootloaderClient.GetId` returned `0x79` for every part** (#6). It read byte `[4]` of
  the AN3155 reply, which is the closing ACK. The id is bytes `[2..3]`, and the reply's framing
  is now checked before they are combined, so a stream left out of sync by an earlier command
  cannot hand back a plausible-looking wrong id that would select the wrong flash layout.
- **`Ping` reported a non-bootloader peer as `OperationCanceledException`** (#9), which is
  indistinguishable from the caller's own cancellation. It is the autobaud handshake, so it is
  the call most likely to be talking to something that is not an STM32 at all. It now throws
  `Stm32BootloaderException` naming the byte that arrived.
- **AN3155 §3.7's special erase codes were unreachable** (#8, ADR-0016).
  `ExtendedEraseMemoryPages` always emitted an explicit page list, so `0xFFFF` mass, `0xFFFE`
  bank 1 and `0xFFFD` bank 2 could not be sent; passing `0xFFFF` fell into the page loop and
  built 65537 half-words.
- **A matched delimiter stayed in the pipe** (#7). `ReceiveUntilTerminator`,
  `ReceiveUntilTerminatorPattern` and `ReceiveUntilHeaderFooterMatch` returned a payload that
  stopped short of the delimiter they matched and advanced the reader only that far, so the
  delimiter satisfied the next command's detector and shifted every subsequent reply by a
  frame.
- **A failed serial read looked like a clean close** (#11). `SerialDuplexPipe`'s read pump
  caught every exception and completed the writer with no argument, so a port that failed
  mid-session and one that closed cleanly both surfaced as "transport closed before frame was
  complete" and the real cause was discarded at the `catch`. The exception now reaches
  `PipeWriter.Complete(failure)`. The clean-shutdown filter keys on the exception's own token
  rather than on "is a shutdown under way", so a driver aborting a read for its own reasons at
  the instant `DisposeAsync` runs is preserved as a failure.
- **A throwing decoder wedged the pipe.** The receive loop advances the reader in a `finally`,
  `Discard` bounds accumulation, and `NeedMoreData` at end of stream throws naming the unframed
  bytes instead of spinning on a completed pipe or dropping the remainder silently.
- **`ByteStreamAdapter` dropped a mid-message close.** Every `TransceiverTransportException`
  from the message channel was treated as a clean end of stream; the original is now kept as
  the inner exception.

## 2.0.0-alpha.6 - 2026-08-27

First release from the public repository. No source changed since `2.0.0-alpha.5`.

### Added
- `.github/workflows/publish.yml`, publishing to nuget.org via Trusted Publishing (OIDC, no
  stored API key). This is the first tag that could actually publish; the previous workflow
  referenced a secret that did not exist.

### Fixed
- A `workflow_dispatch` could publish from any ref. The push step is gated on the event rather
  than on an input, so only a `v*` tag reaches nuget.org.

## 2.0.0-alpha.5 - 2026-08-27

Initial public release of the 2.x line. Earlier history is not described here.
