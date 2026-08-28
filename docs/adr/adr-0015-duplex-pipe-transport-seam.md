---
title: "ADR-0015: Adopt IDuplexPipe as the Transport Seam"
status: "Accepted"
date: "2026-08-27"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "pipelines", "transport"]
supersedes: "ADR-0003, ADR-0004, ADR-0007, ADR-0010, ADR-0012, ADR-0013, ADR-0014"
superseded_by: ""
---

# ADR-0015: Adopt IDuplexPipe as the Transport Seam

> **Written after the fact.** The change described here shipped over several commits without a decision
> record, and its absence made six earlier ADRs read as current when they described a design the library
> had already left. This record exists to close that gap and to give those ADRs something to point at.
> It documents what was decided and why; it is not a proposal.

## Status

**Accepted**

## Context

- **CTX-001**: ADR-0007 introduced `IByteSource`, a three-member interface (`WriteAsync`,
  `ReadByteAsync`, `ReadChunkAsync`) that transports implemented and `Transceiver` consumed. It was the
  seam between "moving bytes" and "framing bytes".

- **CTX-002**: `IByteSource` was a hand-rolled version of something the BCL already ships.
  `System.IO.Pipelines` provides `PipeReader`, `PipeWriter`, and `IDuplexPipe`, with buffer management,
  backpressure, and partial-read semantics already solved and already understood by .NET developers.

- **CTX-003**: The single-byte `ReadByteAsync` primitive forced every transport to either buffer
  internally or pay a per-byte cost. `SerialByteSource` and `TreehopperByteSource` both grew private
  buffering to work around it. `PipeReader.ReadAsync` returns a `ReadOnlySequence<byte>` and lets the
  consumer decide how much to consume, which is the same problem solved once, correctly, upstream.

- **CTX-004**: ADR-0011 removed lifecycle ownership from `ITransceiver`, and ADR-0014 proposed removing
  it from `IByteSource` too. Both were working toward an abstraction that owns no resources.
  `IDuplexPipe` already has no lifecycle members — it is two properties.

- **CTX-005**: A run of ADRs (0010, 0012, 0013) accumulated proposals for composition helpers —
  `Transceiver.Wrap`, delegating transceivers, adapter factories — all of which existed to bridge
  `IByteSource` to something else. Every one of them dissolves if the seam is a type the rest of the
  ecosystem already produces.

- **CTX-006**: Adopting a BCL interface makes transports that need no package at all. A `NetworkStream`,
  a `NamedPipeClientStream`, or any `Stream` becomes a transport through `PipeReader.Create` and
  `PipeWriter.Create`, with no code in this repository.

## Decision

Replace `IByteSource` with `System.IO.Pipelines.IDuplexPipe` as the transport seam.

- **DEC-001**: `IByteSource` is deleted. It is not deprecated, retained, or bridged. No adapter from
  `IByteSource` to `IDuplexPipe` ships.

- **DEC-002**: `Transceiver` is `sealed` and takes its input from pipes, through two constructors:
  `Transceiver(IDuplexPipe, ILogger<Transceiver>?)` and
  `Transceiver(PipeReader, PipeWriter, ILogger<Transceiver>?)`. The abstract-base-class shape from
  ADR-0007 is gone; transports no longer subclass anything.

- **DEC-003**: `DuplexPipeExtensions.AsTransceiver(this IDuplexPipe, ILogger<Transceiver>?)` is the
  convenience composition point. It is a two-line extension method, not a factory type.

- **DEC-004**: `Transceiver` never completes the reader or the writer, and never disposes the pipe.
  Lifecycle stays entirely with the caller, extending ADR-0011 to the transport seam.

- **DEC-005**: Transport packages ship an `IDuplexPipe` implementation rather than a `Transceiver`
  subclass. `SerialDuplexPipe` wraps `RJCP.IO.Ports.SerialPortStream` with a background read pump and
  implements `IAsyncDisposable` for that pump. `BleNordicUartPipe` is a pipe pair the caller drives from
  both ends.

- **DEC-006**: The builder API (`TransceiverBuilder`, `UseSerial(...).Build()`, the staged-builder
  extensions from ADR-0004) is deleted. Construction is `new Transceiver(pipe)`.

- **DEC-007**: A transport gets its own package only when the adaptation is non-trivial — a background
  pump, a framing quirk, a vendor SDK that is not stream-shaped. Anything reachable through
  `PipeReader.Create(stream)` needs no package.

## Consequences

### Positive

- **POS-001**: The library sheds an invented abstraction in favour of a BCL one. New contributors need
  to learn `System.IO.Pipelines`, which is documented by Microsoft, rather than a bespoke interface
  documented only here.

- **POS-002**: Buffer management, backpressure, and partial reads move out of this repository entirely.

- **POS-003**: Transports become nearly free. TCP and named pipes need no adapter code, only wiring.

- **POS-004**: The composition proposals in ADR-0010, ADR-0012, and ADR-0013 are moot rather than
  pending. `IDuplexPipe` is already the composable shape they were designing toward.

- **POS-005**: `ITransceiver` and the seam beneath it are both lifecycle-free, so ADR-0011's principle
  now holds all the way down.

### Negative

- **NEG-001**: A hard breaking change with no migration path. Every consumer implementing `IByteSource`
  or subclassing `Transceiver` must rewrite against pipes. This is why the version line moved to 2.x.

- **NEG-002**: `System.IO.Pipelines` becomes a mandatory dependency of the core package. It is a
  Microsoft-shipped MIT package, so the cost is a package reference, not a licensing or trust question.

- **NEG-003**: `IDuplexPipe` is a lower-level contract than `IByteSource`. A transport author must
  understand `ReadResult`, `SequencePosition`, and `AdvanceTo`. `SerialDuplexPipe` exists partly as the
  worked example.

- **NEG-004**: Six ADRs were left describing a design the library no longer had, for several months,
  because this record was not written at the time. That is the cost being paid now.

## Alternatives Considered

- **ALT-001 — Keep `IByteSource` and add a `PipeReader` adapter**: Proposed on a branch as
  `PipeReaderByteSourceAdapter` plus `Transceiver.Wrap(PipeReader)`. **Rejection Reason**: It keeps both
  abstractions and the bridge between them. Deleting one is less code than adapting between two.

- **ALT-002 — Keep `IByteSource` and widen it to return `ReadOnlySequence<byte>`**: **Rejection Reason**:
  At that point it is `PipeReader` with a different name and no ecosystem.

- **ALT-003 — Expose `Stream` as the seam**: **Rejection Reason**: `Stream` carries lifecycle
  (`Dispose`), position, and seeking, none of which a transport should imply. `IDuplexPipe` carries two
  properties and nothing else.

## References

- **REF-001**: `Source/CallAndResponse/Transceiver.cs` — the two pipe constructors
- **REF-002**: `Source/CallAndResponse/DuplexPipeExtensions.cs` — `AsTransceiver`
- **REF-003**: `Source/CallAndResponse.Transport.Serial/SerialDuplexPipe.cs` — worked transport example
- **REF-004**: [System.IO.Pipelines documentation](https://learn.microsoft.com/dotnet/standard/io/pipelines)
