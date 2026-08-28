---
title: "ADR-0005: Evaluate Result Types for the Top-Level ITransceiver API"
status: "Accepted"
date: "2026-03-22"
authors: "Repository maintainer; library consumers"
tags: ["architecture", "decision", "api", "errors", "transceiver", "result-types"]
supersedes: ""
superseded_by: ""
---

# ADR-0005: Evaluate Result Types for the Top-Level ITransceiver API

> **Decision still in force.** The API remains exception-oriented. Two details have aged:
> `TransceiverConnectionException` was removed along with the lifecycle members (ADR-0011), leaving
> `TransceiverTransportException`; and the library targets net8.0 only, not the three TFMs cited in CTX-004.


## Status

**Accepted**

## Context

The current top-level transport contract is exception-oriented.

- **CTX-001**: `Source/CallAndResponse/ITransceiver.cs` exposes asynchronous lifecycle, send, receive, and convenience methods that either complete successfully or fail by throwing.
- **CTX-002**: The core package already defines transport-specific exception concepts through `TransceiverConnectionException` and `TransceiverTransportException`.
- **CTX-003**: Current documentation and examples show direct `await` usage that returns payloads or propagates failures as exceptions.
- **CTX-004**: The library targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8, so any top-level API shape should stay lightweight, portable, and dependency-conscious.
- **CTX-005**: The transport abstraction sits at a low level in the architecture. Protocol clients consume `ITransceiver` as an I/O primitive and layer framing, validation, and protocol rules on top.
- **CTX-006**: There is currently no shared `Result` type or error-envelope abstraction in the repository.
- **CTX-007**: Introducing `Result` types at the `ITransceiver` boundary would affect all transport implementations, all protocol packages, documentation, tests, and consumer call sites.

For this ADR, “Result types” means returning a value such as `Result`, `Result<T>`, or an equivalent discriminated success/failure envelope from top-level `ITransceiver` methods instead of relying primarily on exceptions.

## Decision

Do not convert the top-level `ITransceiver` API to `Result`-returning methods at this time. Retain exception-based failure signaling at the transport boundary, while leaving room for optional higher-level result wrappers in protocol or application-facing APIs.

- **DEC-001**: `ITransceiver` remains exception-oriented for lifecycle and I/O operations.
- **DEC-002**: Transport implementers continue to communicate exceptional conditions through existing exception types and standard cancellation behavior.
- **DEC-003**: The repository will not introduce a mandatory shared `Result` dependency or top-level error envelope solely for the transport abstraction.
- **DEC-004**: If result-style flows are needed later, they should be evaluated first at higher layers where domain-specific failure categories are more stable and more meaningful to callers.
- **DEC-005**: This decision does not prohibit future additive APIs such as adapters, helper wrappers, or protocol-level result objects.

## Consequences

### Positive

- **POS-001**: The low-level transport API stays simple and familiar: callers `await` operations and handle exceptions only when failures occur.
- **POS-002**: Existing transports, protocols, docs, and samples remain compatible and avoid a broad breaking change.
- **POS-003**: The core package avoids introducing a new foundational abstraction or third-party dependency across all target frameworks.
- **POS-004**: Exception-based signaling aligns with the existing use of `CancellationToken`, asynchronous I/O, and transport-specific exception classes.
- **POS-005**: Higher layers remain free to translate exceptions into richer domain-oriented results without forcing that shape onto every transport primitive.

### Negative

- **NEG-001**: The API does not force callers to model failure paths explicitly at compile time. This is a deliberate choice for a low-level I/O boundary: at the byte-stream layer, exhaustive compile-time failure modeling via `Result<T>` types produces API noise rather than safety gains. The appropriate place for compile-time exhaustive error handling is protocol clients and application code, where failures carry domain meaning. `ITransceiver` is an I/O primitive, not a domain object.
- **NEG-002**: Consumers wanting functional or pipeline-oriented error handling must build their own adapters around exceptions.
- **NEG-003**: Some transport failures that are operationally common may still feel “exceptional” in shape even when they are recoverable in user workflows.
- **NEG-004**: Without a shared result envelope, callers must learn exception categories rather than inspecting a single structured error object.

## Alternatives Considered

### Adopt Result Types Across the Entire ITransceiver Surface

- **ALT-001**: **Description**: Change top-level methods to return `Task<Result>`, `Task<Result<T>>`, or equivalent envelopes for open, close, send, receive, and convenience methods.
- **ALT-002**: **Rejection Reason**: This would be a large breaking change across the repository, introduce substantial API noise on low-level primitives, and require designing a durable cross-transport error taxonomy before the library has stabilized one.

### Use Result Types Only for Select Transport Operations

- **ALT-003**: **Description**: Convert only some methods such as `Open`, `Close`, or `SendReceive*` to result-based returns while leaving others exception-based.
- **ALT-004**: **Rejection Reason**: A mixed error-signaling model at the same abstraction level would make the API harder to predict and harder to teach.

### Add an Optional Adapter Layer Over the Existing Interface

- **ALT-005**: **Description**: Keep `ITransceiver` as-is, but offer additive helpers or wrappers that convert exceptions into `Result` objects for consumers who prefer that style.
- **ALT-006**: **Rejection Reason**: Not rejected as a future option, but not selected as the primary top-level design because it does not require changing the core contract now.

### Adopt Result Types at Protocol Boundaries Instead of Transport Boundaries

- **ALT-007**: **Description**: Keep raw transport APIs exception-based and use result objects in protocol clients where failures can be categorized as validation, framing, timeout, device state, or protocol-level errors.
- **ALT-008**: **Rejection Reason**: Not rejected in principle; deferred because the repository has not yet standardized domain result objects for protocol layers.

## Implementation Notes

- **IMP-001**: Continue using transport-specific exceptions such as `TransceiverConnectionException` and `TransceiverTransportException` to communicate low-level failures.
- **IMP-002**: Preserve normal task cancellation semantics by continuing to surface cancellation through `CancellationToken` and `OperationCanceledException` patterns rather than encoding cancellation as a generic result failure. Note: the serial transport's existing misuse of `CancellationTokenSource(10)` for close/reopen is addressed separately in ADR-0003 IMP-009 and should not influence the general cancellation contract.
- **IMP-003**: If future work explores result-style APIs, prototype them as additive wrappers first to validate ergonomics without breaking existing consumers.
- **IMP-004**: If a repository-wide `Result` abstraction is ever introduced, define a stable error taxonomy before applying it to the transport boundary.
- **IMP-005**: Revisit this ADR if protocol packages begin exposing richer domain outcomes. The most likely first candidate is `ModbusRtuClient`: a `ModbusResult<T>` type that encodes success, Modbus exception code, transport failure, and timeout as first-class cases would provide meaningful compile-time guidance at the protocol layer without polluting the transport primitive. Any such result type should be defined in the protocol package, not in the core `CallAndResponse` library.

## References

- **REF-001**: `Source/CallAndResponse/ITransceiver.cs`
- **REF-002**: `Source/CallAndResponse/TransceiverConnectionException.cs`
- **REF-003**: `Source/CallAndResponse/TransceiverTransportException.cs`
- **REF-004**: `Source/CallAndResponse/Transceiver.cs`
- **REF-005**: `docs/ARCHITECTURE.md`
- **REF-006**: `README.md`
