---
title: "ADR-0014: Remove Lifecycle Ownership from IByteSource"
status: "Superseded"
date: "2026-03-25"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "ibytesource", "lifecycle", "composition"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0014: Remove Lifecycle Ownership from IByteSource

> **SUPERSEDED.** Overtaken by a larger change: `IByteSource` was not slimmed, it was deleted. `IDuplexPipe` carries no
> lifecycle members at all, so the outcome this record wanted is in force by construction.
> See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

## Context

- **CTX-001**: ADR-0011 already established the core architectural rule that lifecycle ownership does not belong on `ITransceiver`. `ITransceiver` is session-oriented communication; lifecycle belongs to `IManagedTransceiver` or to an external session owner.

- **CTX-002**: `IByteSource` currently still mixes two responsibilities:
  - lifecycle/session ownership via `IsConnected`, `OpenAsync`, and `CloseAsync`
  - raw byte I/O via `WriteAsync`, `ReadByteAsync`, and `ReadChunkAsync`

- **CTX-003**: In the the companion-session composition model, the lifecycle owner is explicit and external: `DeviceHandleBase<TDevice, TException>` opens the device, supervises reconnect, closes it on disconnect, and defines the active session window. `Transceiver.Wrap(IByteSource)` consumes a byte-capable device inside that window. The wrapped byte source is not the lifecycle owner.

- **CTX-004**: The current public `IByteSource` shape forces externally managed implementations to advertise lifecycle members that the composition path explicitly does not use. This is the same abstraction smell that previously existed on `ITransceiver`: members that are meaningful for some implementations but inert, redundant, or misleading for the most compositionally important case.

- **CTX-005**: Self-managed transports such as `SerialPortTransceiver` still need lifecycle. That requirement does not imply that lifecycle belongs on the public byte-source seam. It only implies that the concrete transport or an internal helper must provide lifecycle operations somewhere.

- **CTX-006**: `Transceiver` already owns the lifecycle surface exposed to callers through `IManagedTransceiver`. Self-managed transport subclasses override `OpenCore` and `CloseCore`. The lifecycle-bearing public abstraction already exists; `IByteSource` does not need to duplicate it.

- **CTX-007**: `Transceiver.Wrap(IByteSource)` already returns `ITransceiver`, not `IManagedTransceiver`. The wrapped path is therefore conceptually an already-active communication view over a byte stream, not a transport object that can be opened or closed by callers.

- **CTX-008**: Promoting `IByteSource` to public in ADR-0010 created a long-lived public contract. If the contract is carrying the wrong responsibility, it is better to correct it now while the repository explicitly accepts breaking architectural cleanup.

## Decision

Redefine `IByteSource` as a byte-I/O-only abstraction. Remove lifecycle ownership from the interface and keep lifecycle on lifecycle-owning transports or session managers.

- **DEC-001**: `IByteSource` is narrowed to the three byte-I/O primitives that `Transceiver` actually needs during an active session:
  - `Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)`
  - `Task<byte> ReadByteAsync(CancellationToken token)`
  - `Task<int> ReadChunkAsync(Memory<byte> destination, CancellationToken token)`

- **DEC-002**: `IsConnected`, `OpenAsync`, and `CloseAsync` are removed from `IByteSource`. They are no longer part of the public byte-source contract.

- **DEC-003**: Lifecycle remains with the component that truly owns it:
  - `IManagedTransceiver` / concrete `Transceiver` subclasses for self-managed transports
  - external session owners such as the companion session library device handles for wrapped transports
  - transport-specific internal helpers where needed

- **DEC-004**: `Transceiver.Wrap(IByteSource)` continues to mean “treat this already-active byte source as an `ITransceiver` for the duration of the current session.” The wrapped transceiver does not own or infer lifecycle from the source. It simply assumes the source is usable for the duration of the operations performed through it.

- **DEC-005**: Self-managed transport helpers may still expose transport-specific lifecycle methods internally. For example, `SerialByteSource` may retain `OpenAsync` and `CloseAsync` as internal members used by `SerialPortTransceiver`, but those members are implementation details rather than part of the public `IByteSource` contract.

- **DEC-006**: Documentation will describe `IByteSource` as the canonical byte-I/O seam between a lifecycle owner and the CallAndResponse framing layer. The layering is:
  1. lifecycle/session owner
  2. byte source
  3. transceiver framing
  4. protocol client

## Consequences

### Positive

- **POS-001**: The public byte-source contract becomes coherent and single-purpose. A type implements `IByteSource` because it can exchange bytes, not because it owns a session lifecycle.

- **POS-002**: The the companion session library composition story becomes cleaner. A the companion session library-owned device implements `IByteSource` for the active session window and does not need to advertise fake or redundant lifecycle members.

- **POS-003**: The architecture becomes internally consistent: lifecycle is separated from both `ITransceiver` and `IByteSource`, and concentrated on explicit lifecycle-owning abstractions.

- **POS-004**: Future transports that expose `PipeReader`, channels, sockets, or other externally managed byte flows can adapt to `IByteSource` without first inventing meaningless open/close semantics.

- **POS-005**: The public `IByteSource` commitment becomes smaller and therefore easier to preserve over time.

### Negative

- **NEG-001**: This is a breaking change for any existing external `IByteSource` implementation. Implementors must remove the lifecycle members from their public interface implementation.

- **NEG-002**: Historical ADRs and documents that described `IByteSource` as six primitives become partially outdated and require interpretation through this ADR.

- **NEG-003**: Self-managed transport helpers may now have lifecycle members that are not visible through the common interface, which slightly increases the conceptual difference between “public seam” and “internal implementation helper.”

## Alternatives Considered

- **ALT-001 — Keep lifecycle on `IByteSource` for convenience**: Rejected because it preserves the same architectural conflation that ADR-0011 removed from `ITransceiver`.

- **ALT-002 — Introduce a second public interface (for example `IActiveByteSource` or `IManagedByteSource`) and keep `IByteSource` unchanged**: Rejected for now because it keeps the overly broad base interface in place and adds more public surface instead of removing the wrong responsibility from the existing seam.

- **ALT-003 — Replace `IByteSource` entirely with `PipeReader` / `PipeWriter`**: Rejected as the immediate refactor target. `PipeReader` may still be an excellent transport surface in the companion session library or in future adapters, but removing lifecycle from `IByteSource` is a narrower, cleaner architectural correction that does not force a complete framing-engine rewrite.

## Implementation Notes

- **IMP-001**: Update `Source/CallAndResponse/IByteSource.cs` so it documents an already-active byte source and exposes only the three byte-I/O members.

- **IMP-002**: Update `Transceiver.Wrap(IByteSource)` and `ByteSourceExtensions.AsTransceiver()` documentation so they describe wrapping an already-active source, without referring to `IByteSource.OpenAsync` / `CloseAsync`.

- **IMP-003**: Keep lifecycle in `SerialPortTransceiver.OpenCore` / `CloseCore`. `SerialByteSource` may retain internal lifecycle methods used by those overrides.

- **IMP-004**: Remove interface lifecycle members from `BleByteSource` and `FakeByteSource`; keep any internal activation/deactivation helpers that remain useful to their owning transports/tests.

- **IMP-005**: Update `docs/ARCHITECTURE.md` so the public architecture reflects the narrowed contract and the four-layer ownership story.

## References

- **REF-001**: `docs/adr/adr-0007-byte-source-abstraction-and-transceiver-layering.md`
- **REF-002**: `docs/adr/adr-0010-ibytesource-public-bridge-and-delegate-composition.md`
- **REF-003**: `docs/adr/adr-0011-remove-lifecycle-ownership-from-transceiver.md`
- **REF-004**: `Source/CallAndResponse/IByteSource.cs`
- **REF-005**: `Source/CallAndResponse/Transceiver.cs`
