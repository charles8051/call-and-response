---
title: "ADR-0011: Remove Lifecycle Ownership from ITransceiver"
status: "Accepted"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "lifecycle", "composition"]
supersedes: ""
superseded_by: ""
---

# ADR-0011: Remove Lifecycle Ownership from ITransceiver

> **Implemented.** `ITransceiver` carries `Send`, `ReceiveMessage`, and `ReceiveUntilIdle` and nothing else.
> `Open`, `Close`, `IsOpen`, and `TransceiverConnectionException` are gone. The mechanism that delivered it
> was the move to `IDuplexPipe` — see [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

## Context

- **CTX-001**: `ITransceiver` currently mixes transport lifecycle and protocol-facing I/O in a single abstraction. Its public surface includes `IsOpen`, `Open(CancellationToken)`, `Close(CancellationToken)`, `Send(...)`, and `ReceiveMessage(...)`.

- **CTX-002**: ADR-0007 intentionally slimmed `ITransceiver` down to a smaller primitive surface, but retained lifecycle methods alongside send/receive operations. This preserved the existing assumption that a transceiver is both the protocol-facing communication primitive and the owner of transport open/close behavior.

- **CTX-003**: ADR-0010 proposes promoting `IByteSource` to a public abstraction and adding `Transceiver.Wrap(IByteSource)` so externally managed byte transports can be adapted into a full `ITransceiver`.

- **CTX-004**: Under ADR-0010, the wrapped transceiver model requires `Open` and `Close` on the resulting `ITransceiver` to be no-ops, because the wrapped `IByteSource` is already opened and closed by an external owner. `IsOpen` merely reflects the underlying source state.

- **CTX-005**: Device-session libraries commonly treat lifecycle as a first-class responsibility of a device-handle base type that owns connection establishment, disconnect handling, reconnection, cancellation, and per-connection execution windows. Such a handle is explicitly the lifecycle owner for an active hardware session.

- **CTX-006**: In that composition model, the natural flow is:
  1. a device handle opens the hardware resource,
  2. that resource exposes raw byte I/O,
  3. CallAndResponse layers framing and request/response behavior over the active byte channel,
  4. protocol clients consume only the message-oriented transceiver surface.

- **CTX-007**: This means the most compositionally important use case for `ITransceiver` is no longer “object that opens and closes itself,” but rather “object that performs framed communication over an already-available byte stream.”

- **CTX-008**: An abstraction whose members are semantically meaningful in one usage mode and intentionally inert in another is a sign that the contract may be carrying more responsibility than belongs at that layer.

- **CTX-009**: Protocol clients such as Modbus or STM32 bootloader code fundamentally require message exchange semantics, not ownership of transport lifecycle. Their core concern is whether they can send a request, receive a response, detect framing completion, and handle transport failure or cancellation.

- **CTX-010**: Lifecycle ownership is orthogonal to protocol semantics. A serial port, BLE link, socket, USB handle, or reconnecting device loop may all differ radically in how they open, close, recover, and report state. Those concerns are transport/session orchestration concerns, not protocol concerns.

- **CTX-011**: The architecture should prefer a single clear owner for lifecycle. If both a transceiver and an external device/session manager can claim authority over opening and closing, the resulting model is ambiguous:
  - who is responsible for initial open,
  - who is responsible for final close,
  - what `Close()` means while a reconnect loop is active,
  - whether consumer code can accidentally interfere with an externally managed session.

- **CTX-012**: The library should optimize for clean layering and seamless interplay with companion session-owning libraries rather than preserving a convenience shape that conflates concerns.

## Decision

Remove lifecycle ownership from the core `ITransceiver` abstraction. Redefine `ITransceiver` as a session-oriented, protocol-facing communication contract that assumes an already-available byte transport.

- **DEC-001**: `ITransceiver` will no longer expose lifecycle members such as `IsOpen`, `Open(...)`, or `Close(...)`. Its responsibility is limited to framed message exchange and related communication behavior.

- **DEC-002**: Transport/session lifecycle is owned by the component that actually manages the underlying connection. This may be:
  - a concrete transport type,
  - a factory or builder,
  - an externally managed `IByteSource`,
  - or an externally managed device-session window.

- **DEC-003**: `Transceiver.Wrap(IByteSource)` remains a valid composition mechanism, but the wrapped transceiver no longer needs special no-op lifecycle semantics because lifecycle is not part of the contract it implements.

- **DEC-004**: The core library (`CallAndResponse`) contains zero lifecycle surface. No `IManagedTransceiver` interface, no `IsOpen` property, no `Open`/`Close` methods, and no abstract `OpenCore`/`CloseCore` template methods on `Transceiver`. Transport packages that need self-managed lifecycle own it entirely in their concrete types; the core library does not provide scaffolding for it.

- **DEC-005**: Protocol clients and higher-level libraries should depend on the narrower `ITransceiver` contract only. They should not be coupled to transport ownership semantics.

- **DEC-006**: The architecture will prefer “active-session composition” over “universal self-opening abstraction.” In other words, the primary mental model for `ITransceiver` is: *this object can communicate over the current session*, not *this object is responsible for establishing the session*.

## Rationale

### 1. Separation of concerns

The core argument is that protocol communication and lifecycle orchestration are different responsibilities.

A protocol-facing transceiver should answer questions like:

- Can I send bytes?
- Can I receive and frame a message?
- How do I detect a complete frame?
- How do failures propagate?

It should not also need to answer:

- How is the cable or radio link opened?
- Who retries after disconnect?
- What reconnect policy is in force?
- Who decides when the session ends?

Those latter questions belong to transport/session ownership, not to the protocol exchange contract.

### 2. Single ownership model

A good architecture prefers one unambiguous owner for lifecycle.

If `ITransceiver` owns lifecycle in some cases, while an external session owner controls the real device lifecycle in others, then the abstraction becomes inconsistent. In one scenario `Open()` means “establish the transport.” In another it means “nothing; someone else already did that.” That inconsistency is a contract smell.

By removing lifecycle from `ITransceiver`, ownership becomes singular and explicit:

- the session owner manages lifecycle,
- the transceiver performs communication within that session.

### 3. Cleaner interplay with session-owning libraries

A device-session library is explicitly layered and lifecycle-centric. Its device-handle type already provides the correct home for open, close, disconnect, reconnect, and cancellation behavior, and it defines the execution window in which a device is alive and usable.

That makes such a library a natural lifecycle owner and CallAndResponse a natural message-layer consumer of the opened device. The relationship is much cleaner if `ITransceiver` does not also claim to own the session lifecycle.

### 4. No-op contract members indicate the wrong abstraction boundary

ADR-0010’s wrapped transceiver works, but only by declaring `Open` and `Close` to be no-ops in the composition scenario. That is a pragmatic compatibility move, but it is not an ideal contract.

A strong abstraction should not require “real” and “do nothing” versions of core members depending on composition mode. Once that is necessary, it is worth reconsidering whether those members belong on the abstraction at all.

### 5. Protocol clients do not need lifecycle ownership

Higher-level clients such as Modbus or bootloader code care about request/response semantics, framing, timeouts, validation, and error propagation. They generally do not need to decide how a serial port, BLE pipe, or reconnecting device handle opens or closes. Removing lifecycle from their dependency surface makes them more portable and more honest about what they truly require.

### 6. Better future extensibility

A lifecycleless `ITransceiver` composes naturally with multiple session models:

- directly managed transports,
- DI-created transports,
- externally opened streams,
- reconnecting device handles,
- test doubles and in-memory fakes.

This keeps the protocol contract stable even as lifecycle strategies evolve.

## Consequences

### Positive

- **POS-001**: `ITransceiver` becomes a tighter, more coherent abstraction focused purely on communication semantics.

- **POS-002**: Composition with a session-owning library becomes architecturally clean. That library owns lifecycle; CallAndResponse owns framing and message behavior.

- **POS-003**: `Transceiver.Wrap(IByteSource)` no longer needs special no-op lifecycle behavior to satisfy an overly broad interface.

- **POS-004**: Protocol clients depend only on what they actually need: communication capability, not connection ownership.

- **POS-005**: The architecture gains a clearer layering story:
  - raw byte transport/session
  - framed transceiver behavior
  - protocol client logic

- **POS-006**: Alternative lifecycle models remain possible without distorting the protocol abstraction. Different transports can be self-managed, externally managed, or reconnect-managed without changing what `ITransceiver` means.

- **POS-007**: Test doubles become conceptually simpler because they do not need to model lifecycle unless the specific test cares about lifecycle semantics.

### Negative

- **NEG-001**: Consumers who currently think of `ITransceiver` as a self-contained “open/send/receive/close” object may need to adapt to a more explicit separation between session creation and session use.

- **NEG-002**: Some existing concrete transport types may need a secondary public shape for lifecycle convenience if that convenience remains desirable.

- **NEG-003**: Documentation, examples, and builder flows will need to explain the distinction between acquiring an active transceiver session and using it.

- **NEG-004**: If lifecycle observability remains important, the project must decide whether that concern belongs on a richer secondary interface, on concrete types, or outside the transceiver abstraction entirely.

## Alternatives Considered

### Keep lifecycle methods on `ITransceiver` and use no-op implementations for externally managed sessions

- **ALT-001**: **Description**: Retain `IsOpen`, `Open`, and `Close` on `ITransceiver`. For wrapped or externally managed transceivers, implement them as no-ops or passive state reflections.

- **ALT-002**: **Rejection Reason**: This preserves compatibility but weakens the abstraction. Core contract members become semantically inconsistent across implementations. The interface continues to mix communication behavior with lifecycle ownership and relies on callers understanding hidden contextual rules.

### Keep `ITransceiver` as-is because some transports are naturally self-managed

- **ALT-003**: **Description**: Since serial/BLE/socket transports often do open and close themselves, keep lifecycle methods on the shared interface for convenience.

- **ALT-004**: **Rejection Reason**: Convenience on concrete types does not justify widening the foundational abstraction. A self-managed transport can still expose lifecycle behavior without forcing every protocol-facing transceiver to model lifecycle ownership.

### Add more lifecycle nuance to `ITransceiver` instead of removing it

- **ALT-005**: **Description**: Preserve lifecycle ownership and add richer state semantics, reconnect states, or ownership modes to clarify behavior.

- **ALT-006**: **Rejection Reason**: This deepens the conflation rather than fixing it. The problem is not insufficient lifecycle expressiveness; the problem is that lifecycle does not belong on the core protocol abstraction in the first place.

### Move everything to `IByteSource` and remove `Transceiver` as a higher layer

- **ALT-007**: **Description**: Collapse the abstraction stack and have protocol clients work directly on raw byte primitives.

- **ALT-008**: **Rejection Reason**: This would discard valuable shared framing and accumulation behavior already centralized in `Transceiver`. The goal is not to remove layering, but to clarify it.

### Retain `ITransceiver` with lifecycle and simply document external ownership as a special case

- **ALT-009**: **Description**: Keep the current interface and explain in docs that externally composed transceivers are externally managed.

- **ALT-010**: **Rejection Reason**: Documentation can explain an inconsistency but does not eliminate it. The architectural contract remains muddled.

## Implementation Notes

- **IMP-001**: Remove `IsOpen`, `Open(...)`, and `Close(...)` from `ITransceiver`.

- **IMP-002**: Ensure `Transceiver.Wrap(IByteSource)` returns a lifecycleless `ITransceiver` without special-case no-op lifecycle behavior.

- **IMP-003**: Delete `IManagedTransceiver` from the core library. Remove all lifecycle scaffolding from the `Transceiver` base class: `IsOpen`, `Open`, `Close`, `OpenCore`, `CloseCore`, `ForceDisconnected`, and all `IsOpen` guard checks. Transport packages that need lifecycle own it entirely in their concrete classes.

- **IMP-004**: Audit protocol clients so they depend only on communication primitives, not lifecycle members.

- **IMP-005**: Update architecture docs to describe the stack explicitly:
  - lifecycle/session owner,
  - byte-source transport,
  - transceiver framing layer,
  - protocol client.

- **IMP-006**: Review ADR-0008 lifecycle observability in light of this decision. If lifecycle state transitions are still needed, they should be hosted on the lifecycle-owning abstraction, not forced onto every protocol-facing transceiver.

- **IMP-007**: Review builders and factories to determine whether they should return a ready-to-use session-bound transceiver, a lifecycle-owning transport object, or both.

## References

- **REF-001**: `Source/CallAndResponse/ITransceiver.cs`
- **REF-002**: `docs/adr/adr-0007-byte-source-abstraction-and-transceiver-layering.md`
- **REF-003**: `docs/adr/adr-0008-transceiver-lifecycle-observability.md`
- **REF-004**: `docs/adr/adr-0010-ibytesource-public-bridge-and-delegate-composition.md`
