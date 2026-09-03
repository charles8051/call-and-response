---
title: "ADR-0020: Replace Frame Detection with a Bidirectional Framing Codec"
status: "Accepted"
date: "2026-09-03"
authors: "Repository maintainer"
tags: ["architecture", "decision", "framing", "codec", "slip", "hdlc", "modbus"]
supersedes: "adr-0017-frame-consumed-length"
superseded_by: ""
---

# ADR-0020: Replace Frame Detection with a Bidirectional Framing Codec

## Status

**Accepted**

*Implementation status: not implemented. `IFrameDecoder`, `IFrameCodec`, `IMessageTransceiver`, and the
`Frame` catalogue do not exist; `ITransceiver` and `FrameDetectionResult` are still as
[ADR-0017](adr-0017-frame-consumed-length.md) left them. This record fixes the design before the code is
written, and the refactor will be prototyped on a branch before it is merged.*

## Context

- **CTX-001**: Four kinds of framing are in scope, and they differ in kind rather than in parameters.
  **Caller-decided**: read exactly N, read to a terminator or pattern, read between a header and footer,
  read until a caller-supplied predicate is satisfied. **Temporal**: read until the line has been idle
  for a gap, for Modbus RTU's inter-frame silence and for unsolicited bursts. **Self-delimiting with
  byte stuffing**: SLIP and RFC 1662 async HDLC, where the framing decides the boundary and the payload
  is escaped on the wire. **Length-prefixed and checksummed**: Modbus RTU and STM32 AN3155.

- **CTX-002**: `FrameDetectionResult` cannot express the third kind, structurally.
  `PayloadOffset`, `PayloadLength`, and `ConsumedLength` are all indices into the received bytes, so the
  payload is by construction a contiguous subrange of the wire. A SLIP frame `C0 41 DB DC 42 C0` carries
  the payload `41 C0 42`, which appears nowhere contiguously in the buffer. The detector *describes* a
  payload; SLIP requires *producing* one.

- **CTX-003**: `ITransceiver.Send` has no framing seam at all. Every `SendReceive*` in
  `TransceiverExtensions` hands `writeBytes` to `Send` unmodified, so the library models framing as a
  receive-only concern. That is false for half the framings in CTX-001: SLIP and HDLC must escape on the
  way out, and HDLC must append an FCS. Neither has anywhere to live.

- **CTX-004**: The four kinds are two axes, and the empty cell is the point.

  | | payload is a verbatim slice of the wire | payload must be transformed |
  |---|---|---|
  | **boundary chosen by the caller** | exactly-N, terminator, header/footer, predicate | — |
  | **boundary chosen by the framing** | length-prefixed | SLIP, async HDLC |
  | **boundary chosen by time** | idle gap | — |

  Decoding is uniform across every populated cell: "given the bytes so far and whether the line is idle,
  where does this frame end and what is its payload" is well-posed for all of them. Encoding is not.
  For every kind but the last cell there is nothing to encode.

- **CTX-005**: SLIP ([RFC 1055][ref-1055]): `END` is `0xC0`, `ESC` is `0xDB`, `0xC0` encodes as
  `0xDB 0xDC` and `0xDB` as `0xDB 0xDD`. A leading `END` is recommended so line noise forms its own
  empty frame, and receivers discard empty frames. No checksum, no length, no addressing, no error
  detection of any kind. Behaviour on an invalid escape is left to the implementation.

- **CTX-006**: RFC 1662 async HDLC framing ([RFC 1662][ref-1662]) is the same shape plus integrity, and
  the two **interleave**. The flag is `0x7E`, the control escape is `0x7D`, and an escaped octet is
  transmitted XORed with `0x20`. The FCS is CRC-16/X-25 — reflected polynomial `0x8408`, initial
  `0xFFFF`, final XOR `0xFFFF`, transmitted least significant octet first — computed over the
  **unescaped** frame contents, and then escaped along with everything else:

  ```
  encode: payload → append FCS over payload → escape the whole thing → wrap in 0x7E … 0x7E
  decode: find the 0x7E boundaries → unescape → verify FCS over the unescaped bytes → strip it
  ```

  Escaping and integrity are therefore not two stackable layers. A CRC layer sitting above an escaping
  layer would compute over escaped bytes and get RFC 1662 wrong. This is the single strongest constraint
  in this record and it eliminates every layered-decorator design on its own.

- **CTX-007**: The ACCM, the async-control-character map, is a 32-bit mask naming which of `0x00`–`0x1F`
  must be escaped. It is normally negotiated by LCP; its pre-negotiation default is `0xFFFFFFFF`. Without
  LCP it has to be configuration.

- **CTX-008**: PPP itself is not framing. LCP, PAP/CHAP, and the NCPs are a link state machine with a
  negotiation exchange, authentication, and keepalives.
  [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) removed lifecycle ownership from
  `ITransceiver` and [ADR-0009](adr-0009-device-discovery-out-of-scope.md) put connection establishment
  outside the library. A PPP link state machine is the same category both records excluded.

- **CTX-009**: A detect function must not throw. `Transceiver.ReceiveMessage` calls `_reader.AdvanceTo`
  on every path it controls, but an exception escaping `detectMessage` leaves the loop before any of
  them. `PipeReader` requires `AdvanceTo` between reads, so the next `ReadAsync` throws
  `InvalidOperationException` for the rest of the session. The invalid-*result* path at
  `Transceiver.cs:97` was handled carefully; the throwing-*detector* path was not.

- **CTX-010**: A frame that never completes accumulates without bound. The incomplete path calls
  `AdvanceTo(buffer.Start, buffer.End)`, which consumes nothing, and `FrameDetectionResult` has no way
  to say "these leading bytes are junk, drop them and keep reading". A noisy line or a device at the
  wrong baud rate is a slow memory leak with no recovery short of tearing down the pipe.

- **CTX-011**: End of stream is unconditionally an error. `Transceiver.cs:133` throws
  `TransceiverTransportException` when `readResult.IsCompleted`, even when the buffered bytes are a
  complete frame by the caller's own rule. "Read until the transport closes" is not expressible.

- **CTX-012**: The receive loop copies the whole accumulated buffer on every iteration once the pipe
  segments — `buffer.IsSingleSegment ? buffer.First : (ReadOnlyMemory<byte>)buffer.ToArray()` at
  `Transceiver.cs:90` — and every built-in detector rescans from offset zero. Receiving one long frame
  is quadratic in both copying and scanning. Escaped framings make frames longer.

- **CTX-013**: The detector is re-invoked on a growing buffer that always starts at the same byte, so it
  must be a pure function of the buffer. Any design that lets it accumulate semantic state or write to a
  captured output will double-consume, once per read iteration.

- **CTX-014**: Two live bugs are framing rules the abstraction could not express, which is the evidence
  that the abstraction and not the clients is what is wrong.
  [#21][ref-21]: `ModbusRtuClient` frames on `5 + 2 * numRegisters`, the *success* response length, so a
  5-byte Modbus exception response never completes the frame and the call blocks until cancellation —
  making the `ModbusProtocolException` branch in `ValidateResponse` unreachable for FC03. RTU's actual
  framing rule is the inter-frame gap, under which an exception response is just a shorter frame.
  [#22][ref-22]: seven `Stm32BootloaderClient` sites check for an ACK with `SendReceivePerfectMatch`,
  which scans for the ACK byte and returns `Incomplete` until it finds one, so a NACK blocks until
  cancellation and reaches the user as a timeout.

- **CTX-015**: The built-in catalogue has no length-prefix decoder, which is the most common framing
  shape in embedded protocols and one of the four kinds in CTX-001. `Stm32BootloaderClient.GetSupportedCommands`
  consequently frames a length-prefixed reply — AN3155's `ACK, N, version, N bytes, ACK` — with
  `SendReceiveHeaderFooter({Ack}, {Ack})`, which skips any leading stray byte silently and would
  truncate the frame if a command byte were `0x79`. Latent only because no AN3155 command code is.

- **CTX-016**: The current public version is `v2.0.0-alpha.6`. Breaking changes to public API are cheap
  now and expensive after `v2.0.0`.

## Decision

- **DEC-001**: Replace frame *detection* with frame *decoding*. A decoder produces the payload rather
  than describing where it is, which is what makes CTX-002 tractable.

  ```csharp
  namespace CallAndResponse.Framing;

  public readonly ref struct FrameContext
  {
      public ReadOnlySequence<byte> Received { get; }  // always from byte zero of the frame
      public bool IsIdle { get; }                      // IdleTimeout elapsed with no new bytes
      public bool IsTransportComplete { get; }         // pipe completed; no more bytes will arrive
  }

  public enum FrameDecodeStatus { NeedMoreData, Frame, Discard, Invalid }

  public readonly struct FrameDecodeResult
  {
      public FrameDecodeStatus Status { get; }
      public int ConsumedLength { get; }   // bytes to remove from the head of Received
      public string? Reason { get; }       // Invalid only

      public static FrameDecodeResult NeedMoreData { get; }
      public static FrameDecodeResult Frame(int consumedLength);
      public static FrameDecodeResult Discard(int consumedLength);
      public static FrameDecodeResult Invalid(int consumedLength, string reason);
  }

  public interface IFrameDecoder
  {
      TimeSpan? IdleTimeout { get; }

      /// Write the payload to <paramref name="payload"/> only when returning Frame.
      /// The writer is a staging buffer owned by the receive loop; see DEC-004a.
      FrameDecodeResult Decode(in FrameContext context, IBufferWriter<byte> payload);
  }
  ```

- **DEC-002**: `PayloadOffset` and `PayloadLength` leave the public API. Three numbers become one, and
  ADR-0017's payload-extent-versus-frame-extent distinction stops being something a caller can get
  wrong: they were only ever conflatable because both were expressed as offsets.
  `Transceiver.FrameFitsBuffer` and its `ArgumentException` path collapse to
  `0 <= ConsumedLength <= Received.Length`.

- **DEC-003**: `Received` is a `ReadOnlySequence<byte>`, not a flattened `ReadOnlyMemory<byte>`,
  addressing CTX-012. Flattening becomes opt-in through a `Frame.OverSpan` adapter for decoder authors
  who do not want to write against `SequenceReader<byte>`.

- **DEC-004**: `Decode` is total. It never throws, per CTX-009. A malformed frame returns
  `Invalid(consumedThroughTheDelimiter, reason)`; the transceiver consumes those bytes so the corrupt
  frame cannot re-fire forever, and then throws. The receive loop additionally advances the reader in a
  `finally` so a misbehaving third-party decoder cannot wedge the pipe.

- **DEC-004a**: Decoder output is transactional, and the receive loop enforces it rather than trusting
  the decoder. `IBufferWriter<byte>` has no rewind, so a decoder that writes and then returns
  `NeedMoreData` would duplicate its output on the next read, and one that writes and then returns
  `Discard` or `Invalid` would leak bytes into the caller's destination. Neither is preventable by
  DEC-005's purity rule alone: purity constrains what the decoder *reads*, not what it has already
  *written*. So the loop passes a pooled staging writer, not the caller's destination, and copies to
  the destination only on `Frame`. The staging buffer is reset before every `Decode` call. The
  interface documents "write only when returning `Frame`" as the contract; the staging buffer is what
  makes violating it harmless. The `Validated` combinator (DEC-010) depends on this directly, since it
  decides pass or fail only after the wrapped decoder has written.

- **DEC-005**: `Decode` must be a pure function of its context, per CTX-013. This is a documented
  contract, not an enforced one. Caching keyed on `Received.Length` is permitted; carrying semantic
  state across calls is not. DEC-004a contains the damage when it is violated, but a decoder that
  carries a partial-parse cursor across calls still mis-frames; only duplication is prevented.

- **DEC-006**: `Discard` bounds CTX-010, and `IsTransportComplete` resolves CTX-011 by letting the
  decoder decide whether end of stream completes a frame or fails.

- **DEC-006a**: `NeedMoreData` returned when `IsTransportComplete` is set is a terminal error, not an
  ordinary incomplete read. Without this rule the loop either spins on a completed pipe that will never
  produce another byte, or advances past the buffered remainder and loses it silently. The loop invokes
  the decoder once on the final buffered bytes — so a decoder that can complete a frame at EOF, such as
  `Frame.UntilTransportComplete`, gets its chance — and if that call still returns `NeedMoreData` it
  throws `TransceiverTransportException` naming how many bytes were left unframed. `Frame.Exactly(n)`
  given fewer than `n` bytes at EOF is the ordinary way to reach this, and a truncated response should
  say so rather than hang or vanish.

- **DEC-007**: Add the encoding half, which CTX-003 says has nowhere to live today.

  ```csharp
  public interface IFrameEncoder { void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination); }
  public interface IFrameCodec : IFrameEncoder, IFrameDecoder { }
  ```

- **DEC-008**: Two top-level contracts, split on the encoder because that is where the meaning changes
  (CTX-004). `ITransceiver` is a byte channel whose reads are caller-directed and whose `Send` writes
  bytes verbatim. `IMessageTransceiver` is a message channel whose framing is a property of the link,
  fixed for its lifetime.

  ```csharp
  namespace CallAndResponse;

  public interface ITransceiver
  {
      Task Send(ReadOnlyMemory<byte> bytes, CancellationToken token);
      Task<Memory<byte>> Receive(IFrameDecoder decoder, CancellationToken token);
      Task Receive(IFrameDecoder decoder, IBufferWriter<byte> destination, CancellationToken token);
  }

  public interface IMessageTransceiver
  {
      Task SendMessage(ReadOnlyMemory<byte> payload, CancellationToken token);
      Task<Memory<byte>> ReceiveMessage(CancellationToken token);
  }
  ```

  The rule this encodes: two operations belong on one interface only if every implementation gives every
  member a meaning. A self-delimiting link cannot give `Receive(decoder)` one, and a raw byte link
  cannot give `SendMessage` one.

- **DEC-009**: Binding a codec to a link is a decorator, and adapting between the two contracts is
  explicit in both directions.

  ```csharp
  public sealed class Transceiver : ITransceiver;                  // over IDuplexPipe, unchanged in spirit
  public sealed class MessageTransceiver : IMessageTransceiver     // over ITransceiver + IFrameCodec
  {
      public MessageTransceiver(ITransceiver inner, IFrameCodec codec, ILogger? logger = null);
  }

  public static IMessageTransceiver WithFraming(this ITransceiver t, IFrameCodec codec);
  public static ITransceiver        AsByteStream(this IMessageTransceiver c);
  ```

- **DEC-009a**: `AsByteStream` needs a stated contract in both directions, and only one of them is
  lossless. **Receive**: it buffers whole decoded messages and serves caller-directed reads from them,
  carrying the remainder forward; a read that spans two messages is satisfied by concatenation.
  **Send**: each `Send` call becomes exactly one `SendMessage`. There is no buffering and no flush,
  because a byte channel has no concept that would say when a message ends. The consequence has to be
  stated plainly rather than hidden: a client that builds one logical frame from two `Send` calls emits
  two messages, and no adapter can know it meant one. `Stm32BootloaderClient.Write256` sends its
  command, address, and data separately and each is separately acknowledged, so one message per send is
  right for AN3155 — but that is a property of AN3155, not a guarantee the adapter provides. Callers
  whose sends do not align with message boundaries must use `IMessageTransceiver` directly.

- **DEC-010**: Replace the twelve methods in `TransceiverExtensions` — mostly a `Send` cross-produced
  with one of five detectors — with `SendReceive(write, decoder)` plus a decoder catalogue.

  ```csharp
  public static class Frame
  {
      public static IFrameDecoder Exactly(int count);
      public static IFrameDecoder UntilTerminator(byte terminator, bool keepInPayload = false);
      public static IFrameDecoder UntilPattern(ReadOnlyMemory<byte> pattern, bool keepInPayload = false);
      public static IFrameDecoder Between(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> footer);
      public static IFrameDecoder UntilIdle(TimeSpan gap);
      public static IFrameDecoder UntilTransportComplete();
      public static IFrameDecoder LengthPrefixed(int prefixOffset, int prefixSize, Endianness endianness,
                                                 int lengthAdjustment = 0, int payloadOffset = 0,
                                                 int trailerLength = 0);
      public static IFrameDecoder Custom(FrameDecodeCallback decode, TimeSpan? idleTimeout = null);

      public static IFrameDecoder WithIdleTimeout(this IFrameDecoder inner, TimeSpan gap);
      public static IFrameDecoder WithMaxLength(this IFrameDecoder inner, int maxFrameLength);
      public static IFrameDecoder Validated(this IFrameDecoder inner, FrameValidator validate);
  }
  ```

  `LengthPrefixed` closes CTX-015. The combinators make the hybrids real devices actually present
  expressible: `Frame.Exactly(5).WithIdleTimeout(gap)` and `Frame.UntilIdle(gap).Validated(Crc16Modbus)`
  are neither of them expressible today.

- **DEC-011**: `ReceiveUntilIdle` leaves `ITransceiver` and becomes `Frame.UntilIdle(gap)` — a decoder
  whose `IdleTimeout` is non-null and which returns a frame when `IsIdle` and the buffer is non-empty.
  Temporal framing stops being a second hand-written receive loop, becomes composable with content
  framing, and drops off every mock. The timer race in [#23][ref-23] is in the loop this removes, but
  that bug is fixed on its own terms because this refactor is not yet committed to.

- **DEC-012**: `SlipCodec` and `HdlcCodec` ship in the core `CallAndResponse` package under the
  `CallAndResponse.Framing` namespace. Both are small, both are RFC-stable, and neither adds a
  dependency, so [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) DEC-007's rule that a dependency
  earns a package boundary says no package.

- **DEC-013**: The FCS lives inside `HdlcCodec` rather than in a stackable CRC layer. This is forced by
  CTX-006, not chosen for convenience.

- **DEC-014**: Framing integrity failures throw by default rather than being silently discarded as
  RFC 1662 permits. In a request/response library a silently discarded response is a call that hangs
  until cancellation and tells the caller nothing — which is exactly the failure mode of [#21][ref-21]
  and [#22][ref-22]. Add `FramingException` to the core package, with `FrameIntegrityException` for a
  failed FCS. Policy is a codec option for callers who want the RFC behaviour.

  ```csharp
  public enum InvalidFrameAction { Throw, Discard }

  public sealed class SlipCodec : IFrameCodec
  {
      public bool EmitLeadingEnd { get; init; } = true;
      public InvalidFrameAction OnInvalidEscape { get; init; } = InvalidFrameAction.Throw;
      public int MaxFrameLength { get; init; } = 1006;      // RFC 1055 receive convention
  }

  public sealed record HdlcOptions
  {
      public InvalidFrameAction OnFcsMismatch { get; init; } = InvalidFrameAction.Throw;
      public uint SendAccm { get; init; } = 0xFFFFFFFF;     // RFC 1662 §7.1
      public uint ReceiveAccm { get; init; } = 0xFFFFFFFF;
      public int MaxFrameLength { get; init; } = 1500;      // default PPP MRU
      public byte[]? AddressAndControl { get; init; } = null;  // null = framing only, not PPP
  }
  ```

- **DEC-015**: The HDLC type is named for the framing, not for PPP. It implements RFC 1662 framing and
  nothing above it, per CTX-008, and a type named `PppTransceiver` would promise LCP and the NCPs.

- **DEC-016**: `ModbusRtuClient` moves to `IMessageTransceiver`, framed by a Modbus RTU codec that
  appends CRC-16 on encode, validates and strips it on decode, and sets `IdleTimeout` to the
  3.5-character gap.

  ```csharp
  public ModbusRtuClient(IMessageTransceiver channel, ILogger<ModbusRtuClient>? logger = null);
  // new ModbusRtuClient(link.WithFraming(ModbusRtu.Codec(ModbusRtu.GapFor(baudRate))))
  ```

  This fixes [#21][ref-21] structurally rather than by arithmetic: an exception response is simply a
  shorter message and parses normally. `ModbusRtuRequestBuilder.AddCrc` and the
  `// TODO: Validate CRC` at `ModbusRtuClient.cs:94` both disappear, the latter because forgetting
  becomes unrepresentable. The gap needs the baud rate, which the transceiver deliberately does not
  know and the application does, so it enters through `ModbusRtu.GapFor(int baudRate)`.

- **DEC-017**: `Stm32BootloaderClient` stays on `ITransceiver`. AN3155 has no framing; every reply's
  length is dictated by the command just sent, so it is genuinely a caller-directed byte stream and
  modelling it as a message channel would be dishonest. Its port is mechanical:
  `SendReceiveExactly(f, n)` becomes `SendReceive(f, Frame.Exactly(n))`, the seven
  `SendReceivePerfectMatch` ACK checks become `SendAndExpectAck` (which is [#22][ref-22], fixed on its
  own terms first), and `GetSupportedCommands` moves to `Frame.LengthPrefixed`, closing CTX-015.

- **DEC-018**: A protocol client written against `IMessageTransceiver` runs over SLIP, over HDLC, and
  over a terminator codec without modification, because it never expressed a byte-boundary opinion. A
  client written against `ITransceiver` runs over a framed link through `AsByteStream`, subject to
  DEC-009a's send rule: free when its sends already align with message boundaries, and requiring the
  client to move to `IMessageTransceiver` when they do not. Both directions exist and neither pretends
  the two contracts are the same.

## Consequences

### Positive

- **POS-001**: SLIP and async HDLC become expressible at all, which they are not today (CTX-002,
  CTX-003).

- **POS-002**: Two live bugs are fixed structurally rather than by patching arithmetic. [#21][ref-21]
  goes away because RTU framing becomes the gap it always was; [#22][ref-22] goes away because
  `PerfectMatch` has no successor in the catalogue and `Frame.Exactly(1)` plus validation is the only
  spelling. CTX-014's observation is that both existed because the abstraction offered no way to say
  the right thing.

- **POS-003**: `FramingException` and `Discard` mean a corrupt or desynchronised line produces an error
  instead of a hang. Today's failure mode for every framing problem is "blocks until the caller's token
  fires", which tells the caller nothing.

- **POS-004**: Four latent defects in the receive loop close as a side effect: the wedged pipe on a
  throwing detector (CTX-009), unbounded accumulation (CTX-010), unconditional EOF failure (CTX-011),
  and quadratic copying (CTX-012).

- **POS-005**: Twelve extension methods become one plus a catalogue, and the catalogue composes.
  `Frame.UntilIdle(gap).Validated(crc)` is a framing rule that the current API cannot state at all.

- **POS-006**: The type system enforces the byte-channel/message-channel distinction instead of the
  documentation asking for it. `ReceiveExactly(5)` does not compile against a SLIP-framed link.

### Negative

- **NEG-001**: This is a rewrite of the library's central abstraction, not an addition to it.
  `ITransceiver`, `FrameDetectionResult`, `TransceiverExtensions`, `Transceiver`, and `ModbusRtuClient`
  all change. CTX-016 says the timing is right; it does not make the change small.

- **NEG-002**: ADR-0017 is effectively undone. Its analysis was correct for the model it was working in,
  and DEC-002 removes the model. That is a cost paid in reviewer confusion, and the ADR index has to say
  so plainly.

- **NEG-003**: `IFrameDecoder` is a harder interface to implement than
  `Func<ReadOnlyMemory<byte>, FrameDetectionResult>`. `ReadOnlySequence<byte>`, `IBufferWriter<byte>`,
  and a four-state result are correct and are not beginner-friendly. `Frame.OverSpan` and `Frame.Custom`
  exist to keep the easy case easy, and they are a mitigation rather than a fix.

- **NEG-004**: DEC-005's purity requirement is a contract the compiler cannot check, and violating it
  produces duplicated payloads rather than an exception. It is the sharpest hazard for third-party
  decoder authors and needs to be stated on the interface, not only here.

- **NEG-005**: `AsByteStream` is lossy in both directions in ways DEC-009a can state but not fix. Reads
  concatenate across message boundaries, and each `Send` becomes one message whether or not the caller
  meant one. It is the one place in the design where a contract mismatch is papered over rather than
  made impossible, and its correctness depends on a property of the protocol using it.

- **NEG-006**: The FCS table, the escape loops, and the ACCM handling are new correctness surface in the
  core package, verifiable only against published test vectors. A one-bit error in the CRC table is
  invisible until it meets a real peer.

- **NEG-007**: Nothing in the repository needs `IMessageTransceiver` today. Modbus and STM32 both run
  over raw links, and DEC-016 is a reorganisation rather than a response to demand. The load-bearing
  half of this record is the codec types (DEC-001 through DEC-007); the channel split (DEC-008,
  DEC-009) is additive on top and could be deferred — but deferring it means changing
  `ModbusRtuClient`'s constructor later, which is cheap now and expensive after `v2.0.0`. The automated
  review on [#24][ref-24] reached ALT-004 independently on exactly this ground, which is worth weighing:
  two readings of the same evidence preferred the smaller mechanism, and this record accepts the larger
  one on the strength of a future consumer rather than a present one.

## Alternatives Considered

- **ALT-001 — SLIP and HDLC as decorators implementing `ITransceiver` over an `ITransceiver`**: this was
  the first draft of this record and it is wrong three ways, any one of which is disqualifying.
  **Rejection Reason**: (a) `ReceiveMessage` has no correct implementation, because SLIP has already
  chosen the boundary — the wrapper must ignore the caller's detector, or apply it to the decoded
  payload so that `ReceiveExactly(5)` silently means "the first 5 bytes of the next message" with the
  remainder homeless, or throw on the base contract's principal member. (b) `Send` is worse: the wrapper
  must reinterpret it from "write these bytes" to "these bytes are one whole message", and
  `Stm32BootloaderClient.Write256` relies on the former by building one logical exchange from three
  send/receive pairs. (c) The extension methods bind on the static type, so `slipTransceiver.ReceiveExactly(5)`
  compiles and misbehaves. Note the precise line: decoration is fine — `MessageTransceiver` is a
  decorator — but decoration that *preserves the interface* fails, because the interface's contract
  cannot survive the wrap.

- **ALT-002 — Keep `ITransceiver` and remove only `ReceiveUntilIdle` from it, so a SLIP decorator has
  no member it must throw on**: **Rejection Reason**: it fixes the smallest of ALT-001's three problems.
  `ReceiveMessage`'s ignored detector and `Send`'s reinterpretation both survive.

- **ALT-003 — One interface covering all four kinds**: **Rejection Reason**: its receive member must
  admit both "the caller decides the boundary" and "the framing decides the boundary", forcing either an
  ignored parameter or a `NotSupportedException`, and its `Send` must mean both "write these bytes" and
  "write these bytes as one message".

- **ALT-004 — Codecs as free-standing values the caller applies by hand**, with no
  `IMessageTransceiver`: `await link.Send(Slip.Encode(payload), ct)` and
  `await link.Receive(Slip.Decoder, ct)`. **Rejection Reason**: it is genuinely defensible and it is the
  runner-up. It buys a smaller surface and reinterprets no existing contract, and it costs
  substitutability: `ModbusRtuClient` cannot be handed a SLIP-framed link and just work, so somebody
  writes `ModbusOverSlip` by hand. It also lets a caller silently forget the send half —
  `link.Send(payload)` compiles and puts unescaped bytes on the wire — which DEC-008 makes
  unrepresentable. **The condition that flips this record to ALT-004**: if no protocol in scope will
  ever run *over* a self-delimiting link, i.e. SLIP and HDLC are used *as* the protocol rather than
  *under* one. Concretely, take this record if the driver is "a device speaks SLIP-wrapped Modbus", and
  ALT-004 if it is "someone wants to write a SLIP client".

- **ALT-005 — Stackable `WithEscaping()` and `WithCrc16()` layers**: **Rejection Reason**: CTX-006. The
  FCS is computed inside the escape, so the layers would have to interleave, and layers cannot.

- **ALT-006 — Put the codec into the existing detect delegate**: **Rejection Reason**: the delegate
  returns offsets into the received buffer and SLIP's payload is not a subrange of it. Returning escaped
  bytes for the caller to unescape moves framing out of the framing layer and forces every call site to
  know it is on SLIP. Writing to a captured buffer and returning a dummy `Complete(0, 0, consumed)`
  makes the return value a lie and breaks CTX-013, appending the payload once per read iteration. And it
  does nothing for the send path, which is half of SLIP and all of the FCS.

- **ALT-007 — Implement PPP properly, with LCP and the NCPs**: **Rejection Reason**: CTX-008.

- **ALT-008 — Negotiate the ACCM by implementing LCP configuration option 2 only**: **Rejection
  Reason**: the option arrives inside a Configure-Request and is answered with a Configure-Ack or
  Configure-Nak. Implementing one option means implementing the exchange.

## Implementation Notes

- **IMP-001**: Fix [#21][ref-21], [#22][ref-22], and [#23][ref-23] on the current abstraction first, on
  their own branches. They are live bugs in shipped code and must not wait on this refactor, which may
  yet be abandoned after the prototype.

- **IMP-002**: Build in dependency order and keep each step green: `IFrameDecoder` and the `Frame`
  catalogue with the receive loop rewritten; then `IFrameEncoder`/`IFrameCodec` and
  `MessageTransceiver`; then `SlipCodec` and `HdlcCodec`; then the Modbus and STM32 ports. The first
  step alone closes POS-004 and is worth having even if the rest is abandoned.

- **IMP-003**: Verify the CTX-006 constants against published vectors before implementing, not from
  this record. The reflected polynomial, the initial and final values, the octet order, and RFC 1662's
  minimum frame length are all stated here from the RFC and have not been run.

- **IMP-004**: Property-test each codec's round trip: `Decode(Encode(x)) == x` for random payloads, and
  explicitly for the empty payload, an all-delimiter payload, an all-escape payload, and a payload of
  exactly `MaxFrameLength`.

- **IMP-005**: Test the boundary cases that distinguish the two codecs from a naive implementation: two
  HDLC frames sharing one `0x7E`; a SLIP frame preceded by several `0xC0`; a truncated escape at the end
  of a frame; an unknown escape octet; a bad FCS; an over-length frame. The over-length case must
  additionally assert the link still works afterwards, which is what `Discard` is for.

- **IMP-006**: Test the ACCM in both directions — a flagged control octet must be escaped on send, and a
  flagged control octet arriving unescaped must be discarded on receive rather than reaching the
  payload.

- **IMP-007**: Pin the contract hazards with tests aimed at misbehaving decoders rather than the happy
  path. A decoder that throws must not wedge the link (CTX-009). A decoder that writes and then returns
  `NeedMoreData` must not duplicate its payload, and one that writes and then returns `Discard` or
  `Invalid` must not leak bytes into the caller's destination (DEC-004a) — both need a deliberately
  badly-behaved decoder in the suite, since no correct decoder produces them. A decoder returning
  `NeedMoreData` at `IsTransportComplete` must throw naming the unframed byte count, and
  `Frame.UntilTransportComplete` must still get its final call (DEC-006a).

- **IMP-007a**: Test `AsByteStream` against DEC-009a in both directions: a read spanning two messages
  returns the concatenation, and two `Send` calls produce two messages rather than one. Both are the
  documented behaviour rather than the desirable one, so the tests exist to stop a later change from
  quietly making them something else.

- **IMP-008**: All of this is hardware-free and belongs in the existing unit suite; see
  [ADR-0001](adr-0001-testing-strategy.md). Add loopback coverage for the two codecs when the serial
  work in [ADR-0019](adr-0019-dual-serial-transport-backends.md) lands.

- **IMP-009**: Update `docs/ARCHITECTURE.md` when the code exists, not before, following ADR-0019
  IMP-006. The layer diagram, the `ITransceiver` member tree, the message-detection section, the
  exception hierarchy, and the design-patterns table all change.

- **IMP-010**: Mark ADR-0017 as superseded by this record in the index when the code lands, not when
  this record merges. Until then its description of `FrameDetectionResult` is still how the library
  works.

## References

- **REF-001**: [RFC 1055][ref-1055] — SLIP, CTX-005
- **REF-002**: [RFC 1662][ref-1662] — PPP in HDLC-like framing; the FCS-inside-the-escape ordering in
  CTX-006 and the ACCM default in CTX-007
- **REF-003**: [ADR-0017](adr-0017-frame-consumed-length.md) — the model DEC-002 replaces
- **REF-004**: [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) — DEC-007, the rule that a dependency
  earns a package
- **REF-005**: [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) and
  [ADR-0009](adr-0009-device-discovery-out-of-scope.md) — the scope boundary CTX-008 applies to PPP
- **REF-006**: `Source/CallAndResponse/Transceiver.cs` — the `AdvanceTo` paths in CTX-009, the
  accumulation in CTX-010, the EOF throw in CTX-011, the flattening in CTX-012
- **REF-007**: [#21][ref-21], [#22][ref-22], [#23][ref-23] — the live bugs in CTX-014 and DEC-011
- **REF-008**: [PipeReader.AdvanceTo][ref-advanceto] — the read-then-advance contract CTX-009 depends on

[ref-1055]: https://www.rfc-editor.org/rfc/rfc1055
[ref-1662]: https://www.rfc-editor.org/rfc/rfc1662
[ref-advanceto]: https://learn.microsoft.com/en-us/dotnet/api/system.io.pipelines.pipereader.advanceto
[ref-21]: https://github.com/charles8051/call-and-response/issues/21
[ref-22]: https://github.com/charles8051/call-and-response/issues/22
[ref-23]: https://github.com/charles8051/call-and-response/issues/23
[ref-24]: https://github.com/charles8051/call-and-response/pull/24
