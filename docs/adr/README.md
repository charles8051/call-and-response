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
| [0016](adr-0016-stm32-extended-erase-api-shape.md) | Split the STM32 Extended Erase API by AN3155 Erase Form | Accepted |
| [0018](adr-0018-stm32-bootloader-command-surface.md) | Scope of the STM32 Bootloader Command Surface | Accepted |
| [0019](adr-0019-dual-serial-transport-backends.md) | Ship Serial Transports for Both System.IO.Ports and RJCP.SerialPortStream | Accepted |
| [0020](adr-0020-framing-codec-abstraction.md) | Replace Frame Detection with a Bidirectional Framing Codec | Accepted |
| [0021](adr-0021-drop-transport-packages.md) | Drop the Serial and BLE Transport Packages | Proposed |

ADR-0001 and ADR-0005 hold, but each names a type or a target framework that has since changed; both
carry a note saying which.

ADR-0019 is accepted but not yet implemented. It describes packages and types that do not exist, and
says so at the top. ADR-0020 is implemented, and ADR-0017 moved to superseded when that code landed:
the type it is about no longer exists.

ADR-0021 proposes removing the transport packages and withdrawing ADR-0019 with them. It is Proposed,
not Accepted — nothing has been deleted, and its serial half is gated on the replacement reaching
nuget.org (ADR-0021 DEC-005a). When it is implemented, the ADR-0019 sentence above has to change with
it.

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
| [0017](adr-0017-frame-consumed-length.md) | Separate Payload Extent from Frame Extent in FrameDetectionResult | Superseded | [0020](adr-0020-framing-codec-abstraction.md) |

## What changed, in one paragraph

ADR-0007 introduced `IByteSource`, a hand-rolled three-member byte-I/O interface, and transports
implemented it. ADR-0011 then removed lifecycle ownership from `ITransceiver`, and ADR-0014 proposed
removing it from `IByteSource`. ADR-0015 settled the direction by deleting `IByteSource` outright in
favour of `System.IO.Pipelines.IDuplexPipe`, which is lifecycle-free by construction and already
understood across the ecosystem. That change also disposed of the builder API (ADR-0004) and made the
composition proposals in ADR-0010, ADR-0012, and ADR-0013 unnecessary rather than pending.

ADR-0019 is the first record to build on that seam rather than rearrange it. Because a transport is now
a constructor and two properties, a second serial backend is a package rather than an API change, and
ADR-0003's objection to shipping two serial types no longer holds.

ADR-0020 rearranges again, on the framing side. Adding SLIP and RFC 1662 async HDLC exposed two things
`FrameDetectionResult` cannot do — produce a payload that is not a contiguous slice of the wire, and
frame anything on the send path — so it replaces frame detection with a bidirectional codec and splits
the byte channel from the message channel. It undoes ADR-0017 in the course of doing so.

## Two notes on numbering

**There are two ADR-0009s.** `adr-0009-device-discovery-out-of-scope.md` is the accepted one.
`adr-0009-logging-and-diagnostics-strategy.md` is an earlier draft on the same subject as ADR-0006,
listed above as 0009b. It was never given frontmatter and never adopted. Renumbering it would break
inbound links, so it keeps the filename and carries a banner instead.

**ADR-0014 was briefly used twice.** A branch proposed an unrelated "ADR-0014: PipeReader/PipeWriter
unification" that was never merged. The accepted ADR-0014 is *Remove Lifecycle Ownership from
IByteSource*.
