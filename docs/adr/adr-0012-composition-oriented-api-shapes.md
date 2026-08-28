---
title: "ADR-0012: Composition-Oriented API Shapes for the Byte-Source Lending Model"
status: "Withdrawn"
date: "2026-06-12"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "composition", "ibytesource", "itransceiver", "async-enumerable", "result-types", "functional", "session"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0012: Composition-Oriented API Shapes for the Byte-Source Lending Model

> **WITHDRAWN.** Never implemented. Every shape proposed here is premised on the `IByteSource` lending model, which was
> removed in favour of `IDuplexPipe`. Kept for the reasoning about composition, not as a roadmap.
> References a companion library that is not publicly available.
> See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Proposed**

## Implementation Planning Update

> **Added 2026-03-25.** Subsequent implementation planning has narrowed the near-term scope of this ADR to reduce architectural risk and keep the CallAndResponse/the companion session library seam clean. Not all decisions in this ADR are equally near-term commitments. The table below makes the distinction explicit. The full decision analysis that follows is preserved as design exploration and proposal history.
>
> The near-term composition plan this line pointed at has been removed; it described a two-repository
> roadmap premised on the `IByteSource` lending model. See
> [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) for what was actually built.

| Decision | Near-term status |
|---|---|
| DEC-001 — `AsTransceiver()` extension method composition chain | **Planned now** — highest-value ergonomic win; aligns with ADR-0010/0011; non-breaking |
| DEC-002 — `WithLogging(ILogger)` decorator | **Planned now** (logging only); `WithRetry` is deferred pending semantics and options record |
| DEC-003 — `ReceiveFrames` on `ITransceiver` | **Deferred** — widening `ITransceiver` conflicts with the capability-interface principle; prefer `IHasRawFrames` capability interface first |
| DEC-004 — Protocol-layer discriminated-union results | **Planned direction** — affirmed for protocol packages; not yet committed to any specific hierarchy shape |
| DEC-005 — `BeginSession()` / `ITransceiverSession` | **Deferred** — session semantics are not yet sharp enough; may be redundant with the companion session library's `OnLoopAsync` window |
| DEC-006 — `UseAsTransceiverAsync()` loan helper | **Deferred** — creates a third composition entry point; revisit only if `AsTransceiver()` proves insufficient |
| DEC-007 — `ByteSourceExtensions` static class | **Planned now** — natural home for `AsTransceiver()` and any future non-deferred extension methods |
| DEC-008 — Breaking-change note for `ReceiveFrames` | **Moot while DEC-003 is deferred** |
| DEC-009 — `IAsyncDisposable` on `ITransceiverSession` | **Moot while DEC-005 is deferred** |

### Implementation planning: planned now

- **`AsTransceiver(this IByteSource)`** — Add as the preferred public ergonomic entry point. Delegates to `Transceiver.Wrap(source)`. Aligns with the shared seam: the companion session library's lifecycle window calls `AsTransceiver()` to produce an `ITransceiver` for the session duration.
- **Protocol-layer typed result direction** — Affirm that protocol packages should move toward discriminated-union results at the protocol layer. `ITransceiver` remains exception-oriented.
- **Documentation alignment** — Update documentation to present `AsTransceiver()` as the ergonomic path and demote `Transceiver.Wrap()` to the lower-level primitive. Cross-reference ADR-0010 and ADR-0011.

### Implementation planning: deferred / open questions

- **`ReceiveFrames` directly on `ITransceiver`** — Evaluate capability-interface (`IHasRawFrames`) first. If it proves universal, revisit promotion to the base interface in a future decision.
- **`BeginSession()` / `ITransceiverSession`** — Deferred until session-owned state, disposal semantics, and the misuse the abstraction prevents are crisply defined.
- **`UseAsTransceiverAsync()` loan helper** — Deferred; avoid creating a third first-class composition story alongside `Wrap()` and `AsTransceiver()`.
- **`WithRetry(int maxAttempts)`** — Deferred; requires `RetryOptions` record and documented idempotency contract before shipping.
- **TFM-split core public story** — The ADR's conditional-compilation approach for `ReceiveFrames` and `ITransceiverSession` is not pursued while both items are deferred. The core `ITransceiver` surface should remain uniform across TFMs.

## Context

- **CTX-001**: ADR-0011 removes `IsOpen`, `Open`, and `Close` from `ITransceiver`, redefining it as a session-oriented, communication-only contract. `IByteSource` is promoted to `public` and `Transceiver.Wrap(IByteSource)` is introduced as the primary composition mechanism. The concrete `Transceiver` base class retains lifecycle, but `ITransceiver` carries none.

- **CTX-002**: The architectural model that emerges from ADR-0011 is best understood as **byte-source lending**: a raw transport resource (`IByteSource`) is created and lifecycle-managed by one owner (a `DeviceHandleBase`, a factory, an application-layer DI container), then *lent* to a framing layer (`ITransceiver`) and subsequently to a protocol client (`IModbusClient`, `Stm32BootloaderClient`) for the duration of a session. The framing layer and the protocol client are consumers of the resource, not owners of it.

- **CTX-003**: The lending model is structurally sound, but the current API surface does not yet *express* it. `Transceiver.Wrap(source)` is a factory call; the resulting `ITransceiver` is used imperatively; responses arrive as `Task<Memory<byte>>`. Nothing in the type system or the call-site shape communicates the lending contract, the session boundary, or the streaming nature of the receive side.

- **CTX-004**: Modern C# provides several idioms that can make the lending contract visible at the call site and reduce the structural distance between the architecture and the code: higher-order async functions (the *loan pattern*), extension method composition chains, `IAsyncEnumerable<T>` for streaming receives, sealed record hierarchies as discriminated unions for typed outcome modeling, and `IAsyncDisposable`-scoped session objects.

- **CTX-005**: Five distinct API shapes were identified and evaluated for fitness to this codebase. They are not mutually exclusive; they address different layers of the stack and different call-site concerns. The question is which to adopt, at which layer, and in what order.

- **CTX-006**: The library targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8. This constraint is load-bearing for two of the five options. `IAsyncEnumerable<T>` is part of the BCL on netstandard2.1 and net8.0 but requires `System.Linq.Async` for query operators and is absent from netstandard2.0 without a polyfill. `IAsyncDisposable` is similarly unavailable on netstandard2.0 without `Microsoft.Bcl.AsyncInterfaces`. Records and sealed class hierarchies are available on all three targets when compiled with C# 9 or later.

- **CTX-007**: ADR-0005 evaluated result types at the `ITransceiver` boundary and deferred them, noting that the appropriate place for compile-time exhaustive error handling is protocol clients and application code, not the byte-stream transport primitive. That reasoning remains valid for `ITransceiver`. Protocol clients are the correct layer for typed outcome modeling, and ADR-0012 addresses that layer specifically.

- **CTX-008**: The five options evaluated are:
  - **Option A — Loan Pattern**: Higher-order async functions that accept a callback, create the framing layer for the callback's duration, and dispose it on exit. The byte source is never yielded to the caller; the framing layer is structurally confined to the callback scope.
  - **Option B — Extension Method Composition Chain**: `IByteSource` gains extension methods that return progressively narrower protocol-facing types. `byteSource.AsTransceiver()` returns `ITransceiver`; `transceiver.AsModbusClient()` returns `IModbusClient`. Each step narrows the surface; the byte source is held separately by its owner.
  - **Option C — `IAsyncEnumerable<T>` Streaming Receiver**: `ITransceiver` exposes a `ReceiveFrames(detectFrame, token)` method returning `IAsyncEnumerable<Memory<byte>>`, making the receive side a first-class async stream composable with `await foreach` and LINQ operators.
  - **Option D — Discriminated Union Result Types**: Protocol client methods return `Task<TResult>` where `TResult` is a sealed record hierarchy covering all meaningful outcomes — `Success`, `Timeout`, `TransportFailure`, `Disconnected`. Pattern matching enforces exhaustive handling at the call site.
  - **Option E — Scoped Session Object**: `IByteSource` or `Transceiver` gains a `BeginSession()` / `CreateSession()` method returning an `ITransceiverSession : ITransceiver, IAsyncDisposable`. The session scope makes the lending window structurally visible; disposal tears down the framing context without touching the underlying byte source.

- **CTX-009**: Options B, C, D, and E are additive and complementary. They address different call-site concerns at different layers: B addresses how the framing layer is composed onto a byte source; C addresses how continuous frame streams are consumed; D addresses how protocol clients express typed outcomes; E addresses how session lifetimes are scoped in the the companion session library composition model. No two of them conflict.

- **CTX-010**: Option A (loan pattern) and Option B (extension chain) are partial substitutes for the same concern — how to compose a framing layer onto a byte source. They differ in whether the composed object is returned to the caller (B) or confined to a callback scope (A). This is the only genuine choice between options rather than a composition of them.

## Decision

Adopt Options B, C, D, and E as a coherent layered strategy. Reject Option A as the primary composition mechanism; retain it as an optional ergonomic helper where callback-scoped composition is natural.

- **DEC-001**: **Extension method composition chain (Option B)** is the primary API for composing a framing layer onto a byte source. `IByteSource` gains a public extension method `AsTransceiver()` that returns `ITransceiver` by calling `Transceiver.Wrap(source)`. Protocol packages gain corresponding extension methods: `AsModbusClient()` returns `IModbusClient`. The chain is open-ended — any library that references the core package can add extension methods for its own protocol or framing concern without modifying the core. The byte source is held separately by its lifecycle owner; the extension methods produce protocol-facing views over it.

- **DEC-002**: Decorator extension methods `WithLogging(ILogger)` and `WithRetry(int maxAttempts)` are introduced as wrapping decorators over `ITransceiver`, returning `ITransceiver`. They slot naturally into the composition chain between `AsTransceiver()` and any protocol-client extension method. These decorators are additive; the underlying byte source and its lifecycle are unaffected.

- **DEC-003**: **`IAsyncEnumerable<T>` streaming receiver (Option C)** is added to `ITransceiver` as a new member alongside the existing `ReceiveMessage` overloads. The new member is:
  ```
  IAsyncEnumerable<Memory<byte>> ReceiveFrames(
      Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectFrame,
      CancellationToken token);
  ```
  It yields one `Memory<byte>` per complete frame, indefinitely, until the token is cancelled or the transport disconnects. The existing `ReceiveMessage` overloads are retained unchanged; they remain the correct primitive for request-response flows. `ReceiveFrames` is the correct primitive for spontaneous-message transports (telemetry, barcode scanners, GPS NMEA) and for any consumer that prefers `await foreach` composition. Because `IAsyncEnumerable<T>` is unavailable on netstandard2.0, this member is conditionally compiled behind `#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER` on the interface and on `Transceiver`'s implementation.

- **DEC-004**: **Discriminated union result types (Option D)** are adopted at the protocol client layer, not at the `ITransceiver` layer. This position is consistent with ADR-0005's conclusion that the transport primitive (`ITransceiver`) is correctly exception-oriented at the byte-stream level; it is protocol clients — where failures carry domain meaning — that benefit from typed, exhaustive outcome modeling. A sealed record hierarchy is introduced in each protocol package:
  ```
  public abstract record TransceiverResult;
  public sealed record Success(Memory<byte> Payload)        : TransceiverResult;
  public sealed record Timeout                              : TransceiverResult;
  public sealed record Disconnected                         : TransceiverResult;
  public sealed record TransportFailure(Exception Cause)    : TransceiverResult;
  ```
  Protocol client methods that are expected to succeed or fail in domain-meaningful ways adopt `Task<TResult>` return types where `TResult` is a protocol-specific sealed record hierarchy extending the base shape above. Protocol client methods that wrap the `TransceiverResult` hierarchy add protocol-specific cases (e.g., `ModbusProtocolError`, `FramingMismatch`) as additional sealed record subtypes. Exhaustive `switch` expression handling is enforced by the compiler. `ITransceiver` itself remains exception-oriented.

- **DEC-005**: **Scoped session object (Option E)** is introduced for the the companion session library composition model and for any caller that needs an explicit, structurally-enforced session boundary. A new interface is introduced:
  ```
  public interface ITransceiverSession : ITransceiver, IAsyncDisposable
  ```
  `IByteSource` gains a public extension method `BeginSession(CancellationToken token)` returning `ITransceiverSession`. Disposing the session tears down the internal framing context (clears the receive buffer, stops the accumulation loop) without affecting the underlying byte source's owner-managed lifecycle. `ITransceiverSession` is conditionally compiled behind the same netstandard2.1+ / net8.0 guard as `IAsyncEnumerable<T>` in DEC-003, for the same reason. On netstandard2.0, `Transceiver.Wrap(source)` remains the only composition mechanism.

- **DEC-006**: **Loan pattern (Option A)** is not adopted as the primary composition API. It is optionally available as an ergonomic helper extension method for callers that prefer a callback-scoped form:
  ```
  public static Task UseAsTransceiverAsync(
      this IByteSource source,
      Func<ITransceiver, Task> use,
      CancellationToken token = default)
  ```
  This is a thin wrapper over `BeginSession` / `Wrap` + disposal and adds no structural capability beyond what Options B and E provide. It is provided because the callback form eliminates the local variable declaration and disposal boilerplate for short-lived, single-operation uses. It is not the idiomatic API for multi-operation sessions.

- **DEC-007**: All extension methods in DEC-001, DEC-002, and DEC-006 are `public static` methods in a new static class `ByteSourceExtensions` in the core `CallAndResponse` package. Protocol-package extension methods (`AsModbusClient()`) live in their respective protocol packages and reference only `ITransceiver` and `IModbusClient` from the core package. No circular dependencies are introduced.

- **DEC-008**: The `IAsyncEnumerable<T>` `ReceiveFrames` member added to `ITransceiver` in DEC-003 is a breaking change for any external implementation of `ITransceiver` compiled against netstandard2.1 or net8.0. This is mitigated by the conditional compilation guard (netstandard2.0 implementations are unaffected) and by the observation, established in ADR-0007, that external implementations of `ITransceiver` are not the primary consumer model — `Transceiver` subclasses are. `Transceiver` provides a default implementation of `ReceiveFrames` in terms of the existing `IByteSource` accumulation loop, so no existing subclass requires modification.

- **DEC-009**: `ITransceiverSession` (DEC-005) adds `IAsyncDisposable` to the interface hierarchy. External `ITransceiverSession` implementors must implement `DisposeAsync`. This is a new interface, not a modification to an existing one; no existing code is broken.

## Rationale

### 1. Extension chain over loan pattern as the primary composition primitive

The loan pattern (Option A) is structurally sound — it makes the framing layer impossible to outlive the callback scope, which is a strong lifetime guarantee. However, it does not compose gracefully when a session spans more than one operation. The moment a caller needs to call `ReadHoldingRegisters` and then `WriteRegisters` over the same framing layer, the callback must contain both operations, and the result of the first must flow into the scope of the second. At three or more operations this produces nesting that `async/await` was specifically designed to eliminate. The extension chain (Option B) avoids nesting entirely: the caller holds an `ITransceiver`, uses it for as many operations as needed, and the byte source owner retains independent lifecycle control. The loan pattern remains available as a convenience (DEC-006) for single-operation use cases where its structural guarantee is a real benefit.

### 2. `IAsyncEnumerable<T>` on the receive side is the correct streaming primitive

The existing `ReceiveMessage(detectFrame, token)` is a pull primitive: the caller requests one frame, awaits it, processes it, then requests the next. For request-response protocols (Modbus, STM32 bootloader) this is correct. For spontaneous-message transports — a barcode scanner that emits codes whenever a barcode is scanned, a GPS module emitting NMEA sentences at 1 Hz, a telemetry stream — the caller does not initiate frames; it simply observes them. `IAsyncEnumerable<T>` is the BCL-native idiom for exactly this shape: the producer generates items asynchronously; the consumer iterates them with `await foreach`. LINQ operators from `System.Linq.Async` (filtering, batching, transformation) compose naturally over the enumerable without modifying `ITransceiver`. The idle-timeout overload of `ReceiveMessage` becomes unnecessary for streaming consumers; the `CancellationToken` passed to `ReceiveFrames` terminates the stream.

### 3. Discriminated union results belong at the protocol layer, not the transport layer

ADR-0005's analysis of result types at the `ITransceiver` boundary concluded correctly: at the byte-stream level, failures are either transport failures (exception), cancellation (`OperationCanceledException`), or partial frames (accumulation continues). There is no domain taxonomy of outcomes at that level. At the protocol client level, there is: a Modbus transaction can succeed, time out, receive a framing error, receive a protocol exception code, or lose the transport link. These are meaningfully different outcomes and callers benefit from compile-time enforcement that each case is handled. Sealed records with `switch` expressions enforce exhaustive handling without requiring a third-party discriminated union library. The type hierarchy is transparent and portable to all three targets.

### 4. Session scoping makes the lending window structurally visible

`Transceiver.Wrap(source)` and `BeginSession(token)` both produce a communication view over a byte source. The difference is that `ITransceiverSession` carries an `IAsyncDisposable` contract that signals end-of-session without touching the source's lifecycle. In the the companion session library model, `OnLoopAsync` is a connection window: it starts when the device is connected and ends when the connection is lost. `await using var session = device.BeginSession(connectionToken)` expresses this window precisely — the session is alive for the `OnLoopAsync` body and disposed at exit. `Transceiver.Wrap` remains the lower-level primitive for callers that do not need the `IAsyncDisposable` surface or are on netstandard2.0.

### 5. Multi-target constraint shapes the conditional compilation boundary

netstandard2.0 is the lowest common denominator and is in active use (the STM32 bootloader package targets it). Rather than excluding netstandard2.0 from the new API surface entirely, the modern primitives (`IAsyncEnumerable<T>`, `IAsyncDisposable`) are gated behind a single conditional compilation symbol. The netstandard2.0 surface remains `Transceiver.Wrap` + `ITransceiver`. The netstandard2.1 / net8.0 surface adds `ReceiveFrames`, `BeginSession`, and `ITransceiverSession`. This is consistent with the BCL's own multi-targeting strategy.

## Consequences

### Positive

- **POS-001**: The composition chain (`byteSource.AsTransceiver().AsModbusClient()`) reads as a literal description of the lending model. The architecture and the code are aligned at the expression level.

- **POS-002**: External packages can extend the chain without modifying the core. A `the companion session library.Serial` package adds `.AsTransceiver()` on its device type; a future `CallAndResponse.Transport.Mqtt` adds its own. The extension points are open by construction.

- **POS-003**: `IAsyncEnumerable<T>` on the receive side unblocks the spontaneous-message use case cleanly, without introducing a `DataReceived` event (which ADR-0008 explicitly rejected as creating competing consumption paths).

- **POS-004**: Discriminated union results at the protocol client layer give callers compile-time enforcement of outcome handling without changing the transport primitive or introducing a third-party dependency.

- **POS-005**: `ITransceiverSession` makes the companion session library integration idiomatic. `await using var session = device.BeginSession(connectionToken)` inside `OnLoopAsync` expresses the connection window precisely and guarantees framing context cleanup on exit.

- **POS-006**: The loan pattern helper (DEC-006) is available for callers that genuinely benefit from the structural lifetime guarantee on single-operation use cases, without forcing all callers through the callback shape.

- **POS-007**: All five shapes are additive. No existing call site, test, or protocol client requires modification as a precondition to adopting any of them. They layer on top of the changes introduced by ADR-0011.

### Negative

- **NEG-001**: `ReceiveFrames` on `ITransceiver` is a breaking change for external netstandard2.1+ implementations of the interface. The default implementation on `Transceiver` mitigates this for subclass-based consumers but not for independent implementations.

- **NEG-002**: The conditional compilation guard for `IAsyncEnumerable<T>` and `IAsyncDisposable` surfaces means that netstandard2.0 consumers see a narrower API. Call sites targeting netstandard2.0 cannot use `ReceiveFrames`, `BeginSession`, or `ITransceiverSession`. This is a documentation and discoverability concern, not a correctness concern.

- **NEG-003**: Two composition mechanisms now exist for netstandard2.1+ callers: `Transceiver.Wrap(source)` (returns `ITransceiver`) and `source.BeginSession(token)` (returns `ITransceiverSession`). The distinction between them — whether disposal semantics are needed — must be communicated clearly in documentation.

- **NEG-004**: The discriminated union hierarchy per protocol package means that protocol authors must maintain the result hierarchy as their protocol's failure modes evolve. New failure cases are additive (new sealed record subtype) but require callers to update exhaustive `switch` expressions.

- **NEG-005**: The decorator extension methods in DEC-002 (`WithLogging`, `WithRetry`) introduce a decorator wrapping pattern that, if extended arbitrarily, can produce a long chain of objects at runtime. Each decorator is a small allocation; for embedded targets where allocation pressure matters, the chain length should be bounded.

## Alternatives Considered

### Loan pattern as the sole and primary composition mechanism

- **ALT-001**: **Description**: Adopt Option A exclusively. Every composition of a framing layer onto a byte source is expressed as a callback:
  ```
  await byteSource.UseAsTransceiverAsync(async t => { ... }, token);
  ```
  The composed framing layer is never returned to the caller; it is structurally confined to the callback scope. The lending contract is expressed at the type level — the caller physically cannot hold an `ITransceiver` past the end of the callback.

- **ALT-002**: **Rejection Reason**: The structural lifetime guarantee is the loan pattern's primary advantage, and it is real. But the guarantee comes at the cost of composability. Multi-operation sessions require all operations to be nested inside a single callback, which is the continuation-passing style that `async/await` was introduced to flatten. For request-response protocols where a session consists of several sequential exchanges (a Modbus transaction sequence, an STM32 bootloader erase-then-write flow), the callback form produces the same nesting that motivated the design of `async/await`. The extension chain (DEC-001) provides the same resource-separation properties — the byte source is always held separately from the `ITransceiver` it backs — without the callback pyramid. The loan pattern is retained as an optional helper (DEC-006) for single-operation cases.

### `IObservable<T>` instead of `IAsyncEnumerable<T>` for streaming receives

- **ALT-003**: **Description**: Expose `ReceiveFrames` as `IObservable<Memory<byte>>` instead of `IAsyncEnumerable<Memory<byte>>`, enabling reactive composition via `System.Reactive`.

- **ALT-004**: **Rejection Reason**: `IObservable<T>` is defined in the BCL but practical use requires `System.Reactive`, which is a heavyweight dependency for a library targeting embedded serial and BLE transports. The push-based model of `IObservable<T>` also complicates backpressure — a slow frame consumer can fill the channel while the transport continues to produce bytes. `IAsyncEnumerable<T>` is pull-based: the transport produces bytes as fast as they arrive, but frames are only yielded when the consumer awaits the next one. This is the correct backpressure model for a byte-stream framing layer. `IObservable<T>` can be added as an adapter over `IAsyncEnumerable<T>` without changes to `ITransceiver` for callers who need reactive composition.

### Shared `Result<T>` base type in the core package for all protocol clients

- **ALT-005**: **Description**: Define a single generic `Result<T, TError>` type in the core `CallAndResponse` package and require all protocol client methods to use it.

- **ALT-006**: **Rejection Reason**: A generic `Result<T, TError>` does not express the full discriminated union of outcomes needed by protocol clients. Modbus distinguishes between `FramingMismatch`, `ProtocolException`, `Timeout`, and `TransportFailure` — these are not a binary success/error split. A sealed record hierarchy per protocol client is richer, protocol-specific, and requires no shared base type in the core package. Each protocol package owns its own result taxonomy.

### Introduce `ITransceiverSession` on all targets by adding `Microsoft.Bcl.AsyncInterfaces` on netstandard2.0

- **ALT-007**: **Description**: Take a dependency on `Microsoft.Bcl.AsyncInterfaces` on netstandard2.0 to make `IAsyncDisposable` — and therefore `ITransceiverSession` — available uniformly across all three targets.

- **ALT-008**: **Rejection Reason**: `Microsoft.Bcl.AsyncInterfaces` is a Microsoft package and is not inherently risky, but introducing a transitive NuGet dependency into a core library that previously had none is a meaningful decision. The netstandard2.0 target exists specifically to minimize dependency surface for constrained consumers. The conditional compilation approach (DEC-005) achieves the same goal on the targets that support it without imposing the dependency on consumers who do not need it.

### Remove existing `ReceiveMessage` overloads from `ITransceiver` in favour of `ReceiveFrames` exclusively

- **ALT-009**: **Description**: Replace `Task<Memory<byte>> ReceiveMessage(...)` and its idle-timeout overload with the `IAsyncEnumerable<T>`-based `ReceiveFrames` as the sole receive primitive on `ITransceiver`.

- **ALT-010**: **Rejection Reason**: `ReceiveFrames` is the correct primitive for streaming and spontaneous-message scenarios; it is not the correct primitive for request-response flows where the caller sends a request and awaits exactly one framed response. `await transceiver.ReceiveMessage(detectFrame, token)` is clearer at a request-response call site than `await transceiver.ReceiveFrames(detectFrame, token).FirstAsync()`. Both primitives serve real, distinct usage patterns. Additionally, removing `ReceiveMessage` is a breaking change to all existing call sites, including the protocol client convenience methods, for no behavioral gain.

## Implementation Notes

- **IMP-001**: `ByteSourceExtensions` is a `public static class` in `Source/CallAndResponse/ByteSourceExtensions.cs`. Its initial members are `AsTransceiver(this IByteSource source)` (returning `Transceiver.Wrap(source)`) and, on netstandard2.1+, `BeginSession(this IByteSource source, CancellationToken token)` (returning a new `TransceiverSession(source)`).

- **IMP-002**: `ITransceiverSession` is defined in `Source/CallAndResponse/ITransceiverSession.cs` under `#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER`. It extends `ITransceiver` and `IAsyncDisposable`. Its `DisposeAsync` tears down the framing context without affecting the underlying byte source's owner-managed lifecycle.

- **IMP-003**: `ReceiveFrames` on `ITransceiver` and its default implementation on `Transceiver` use `yield return` inside an `async IAsyncEnumerable<Memory<byte>>` method with `[EnumeratorCancellation]` on the token parameter, following the BCL convention for async streams. The implementation is a loop over the existing `ReceiveMessage` accumulation logic.

- **IMP-004**: Protocol package discriminated union hierarchies use `abstract record` as the base and `sealed record` for each case. All types are in the protocol package's own namespace. The hierarchy is defined once per protocol package and extended by adding new `sealed record` subtypes; existing records are never modified after release.

- **IMP-005**: Decorator extension methods `WithLogging(this ITransceiver t, ILogger logger)` and `WithRetry(this ITransceiver t, int maxAttempts)` return `ITransceiver`. Their concrete implementations are `internal sealed` classes in the core package. Each decorator stores a reference to the inner `ITransceiver` and delegates all members, adding its cross-cutting behavior around `Send` and `ReceiveMessage`.

- **IMP-006**: `UseAsTransceiverAsync` (the loan pattern helper, DEC-006) is an extension method on `IByteSource` in `ByteSourceExtensions`. On netstandard2.1+, its implementation is `await using var session = source.BeginSession(token); await use(session);`. On netstandard2.0, it uses `Transceiver.Wrap(source)` and delegates cleanup to a try/finally block.

- **IMP-007**: Before implementing `ReceiveFrames`, evaluate whether the `[EnumeratorCancellation]` cooperative cancellation pattern is sufficient or whether the internal `AsyncBuffer<byte>` needs an explicit flush path for clean cancellation mid-frame. The existing `ReceiveMessage` loop exits on `OperationCanceledException`; the streaming version must propagate that exception as stream termination rather than re-throwing at the `await foreach` site.

- **IMP-008**: ADR-0005 is unchanged in its position on `ITransceiver`: `ITransceiver` remains exception-oriented. ADR-0012 DEC-004 applies only to protocol client interfaces (`IModbusClient` and its equivalents) and their concrete implementations. These are distinct layers.

## References

- **REF-001**: [ADR-0005](adr-0005-result-types-for-top-level-itransceiver-api.md) — established that result types belong at protocol client layer, not the transport primitive.
- **REF-002**: [ADR-0007](adr-0007-byte-source-abstraction-and-transceiver-layering.md) — established `IByteSource` and the single-reader accumulation loop; `DataReceived` event explicitly rejected as creating competing consumption paths.
- **REF-003**: [ADR-0008](adr-0008-transceiver-lifecycle-observability.md) — rejected `DataReceived` event on `ITransceiver`; `IAsyncEnumerable<T>` via `ReceiveFrames` is the correct push-free alternative for spontaneous-message transports.
- **REF-004**: [ADR-0010](adr-0010-ibytesource-public-bridge-and-delegate-composition.md) — introduced `Transceiver.Wrap(IByteSource)` and the public `IByteSource` surface.
- **REF-005**: [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) — established the byte-source lending model and removed lifecycle from `ITransceiver`; ADR-0012 directly extends its consequences.
- **REF-006**: `Source/CallAndResponse/ITransceiver.cs` — current interface; receives `ReceiveFrames` member.
- **REF-007**: `Source/CallAndResponse/IByteSource.cs` — promoted to public by ADR-0011; receives `AsTransceiver()` and `BeginSession()` extension methods.
- **REF-008**: `Source/CallAndResponse/Transceiver.cs` — receives `ReceiveFrames` default implementation and `Wrap` factory method.
- **REF-009**: `Source/CallAndResponse.Protocol.Modbus/IModbusClient.cs` — receives discriminated union result types per DEC-004.
