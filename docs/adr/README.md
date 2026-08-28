# Architectural Decision Records

Decision records for CallAndResponse. They are a history, not a specification — several describe
abstractions the library no longer has. The **Status** column below is authoritative; every record whose
subject has since changed carries a banner at the top saying so.

If you want to know how the library works today, read [`../ARCHITECTURE.md`](../ARCHITECTURE.md).
If you want to know *why*, read these.

## Current

| ADR | Title | Status |
|---|---|---|
| [0001](adr-0001-testing-strategy.md) | Automated Testing Strategy | Accepted |
| [0002](adr-0002-modbus-function-code-expansion.md) | Expand Modbus RTU Support with Additional Function Codes | Proposed |
| [0005](adr-0005-result-types-for-top-level-itransceiver-api.md) | Evaluate Result Types for the Top-Level ITransceiver API | Accepted |
| [0006](adr-0006-logging-abstraction-strategy.md) | Logging Abstraction Strategy | Accepted |
| [0009](adr-0009-device-discovery-out-of-scope.md) | Device Discovery is Out of Scope for CallAndResponse Transports | Accepted |
| [0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) | Remove Lifecycle Ownership from ITransceiver | Accepted |
| [0015](adr-0015-duplex-pipe-transport-seam.md) | Adopt IDuplexPipe as the Transport Seam | Accepted |

ADR-0001 and ADR-0005 hold, but each names a type or a target framework that has since changed; both
carry a note saying which.

## Superseded and withdrawn

| ADR | Title | Status | Superseded by |
|---|---|---|---|
| [0003](adr-0003-serial-transport-revision.md) | Revise Serial Transport Implementation for Receive Reliability | Superseded | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0004](adr-0004-unify-transceiver-builder-api.md) | Unify Transceiver Builder API | Superseded | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0007](adr-0007-byte-source-abstraction-and-transceiver-layering.md) | Byte-Source Abstraction and Transceiver Layering | Superseded | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0008](adr-0008-transceiver-lifecycle-observability.md) | Transceiver Lifecycle Observability | Superseded | [0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md), [0014](adr-0014-remove-lifecycle-from-ibytesource.md) |
| [0009b](adr-0009-logging-and-diagnostics-strategy.md) | Logging and Diagnostics Strategy | Superseded | [0006](adr-0006-logging-abstraction-strategy.md) |
| [0010](adr-0010-ibytesource-public-bridge-and-delegate-composition.md) | IByteSource as Public Bridge for Delegate-Composed Transceivers | Superseded | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0012](adr-0012-composition-oriented-api-shapes.md) | Composition-Oriented API Shapes for the Byte-Source Lending Model | Withdrawn | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0013](adr-0013-additional-modern-composable-api-patterns.md) | Additional Modern Composable API Patterns Beyond ADR-0012 | Withdrawn | [0015](adr-0015-duplex-pipe-transport-seam.md) |
| [0014](adr-0014-remove-lifecycle-from-ibytesource.md) | Remove Lifecycle Ownership from IByteSource | Superseded | [0015](adr-0015-duplex-pipe-transport-seam.md) |

## What changed, in one paragraph

ADR-0007 introduced `IByteSource`, a hand-rolled three-member byte-I/O interface, and transports
implemented it. ADR-0011 then removed lifecycle ownership from `ITransceiver`, and ADR-0014 proposed
removing it from `IByteSource`. ADR-0015 settled the direction by deleting `IByteSource` outright in
favour of `System.IO.Pipelines.IDuplexPipe`, which is lifecycle-free by construction and already
understood across the ecosystem. That change also disposed of the builder API (ADR-0004) and made the
composition proposals in ADR-0010, ADR-0012, and ADR-0013 unnecessary rather than pending.

## Two notes on numbering

**There are two ADR-0009s.** `adr-0009-device-discovery-out-of-scope.md` is the accepted one.
`adr-0009-logging-and-diagnostics-strategy.md` is an earlier draft on the same subject as ADR-0006,
listed above as 0009b. It was never given frontmatter and never adopted. Renumbering it would break
inbound links, so it keeps the filename and carries a banner instead.

**ADR-0014 was briefly used twice.** A branch proposed an unrelated "ADR-0014: PipeReader/PipeWriter
unification" that was never merged. The accepted ADR-0014 is *Remove Lifecycle Ownership from
IByteSource*.
