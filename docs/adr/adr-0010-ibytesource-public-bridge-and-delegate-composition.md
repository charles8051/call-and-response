---
title: "ADR-0010: IByteSource as Public Bridge for Delegate-Composed Transceivers"
status: "Superseded"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "ibytesource", "composition", "composition"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0010: IByteSource as Public Bridge for Delegate-Composed Transceivers

> **SUPERSEDED.** `IByteSource` no longer exists, so there is nothing to promote to public and no `Transceiver.Wrap` to add.
> The composition point this record wanted is `new Transceiver(IDuplexPipe)` and `IDuplexPipe.AsTransceiver()`.
> This record also builds its rationale on a companion library that is not publicly available; treat those
> references as historical context, not as a dependency this library has.
> See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

## Context

- **CTX-001**: ADR-0007 established `IByteSource`

- **CTX-002**: the companion session library is a companion library for cross-platform device presence tracking. Its forthcoming IO tier (`the companion session library.Serial`, and potentially others) will produce device handles whose lifecycle — connect, reconnect with exponential backoff, disconnect — is managed by `DeviceHandleBase<TDevice, TException>`. `DeviceHandleBase` provides four override hooks: `OpenDeviceAsync`, `OnConnectedAsync`, `OnDisconnectingAsync`, and `OnLoopAsync`.

- **CTX-003**: `DeviceHandleBase.OnLoopAsync` runs **outside the reconnect lock**, after `IsConnected` becomes `true`, and receives a per-connection `CancellationToken` that is cancelled by the base class when the device disconnects. This is precisely the window during which a transceiver should be alive and usable.

- **CTX-004**: `ITransceiver` provides the full communication surface that protocol clients (`ModbusRtuClient`, custom protocol handlers) depend on. Protocol clients should remain decoupled from the specific transport and from the lifecycle state machine that manages it.

- **CTX-005**: `Transceiver` is an abstract base class. `DeviceHandleBase` is also an abstract base class. .NET does not support multiple inheritance. A `the companion session library.Serial` device handle cannot simultaneously extend both `Transceiver` (to get the accumulation loop and `ITransceiver` convenience methods) and `DeviceHandleBase` (to get the reconnect state machine).

- **CTX-006**: `IByteSource` already defines exactly the six primitives that `Transceiver` requires from any transport: `IsConnected`, `OpenAsync`, `CloseAsync`, `WriteAsync`, `ReadByteAsync`, and `ReadChunkAsync`. No new abstraction is needed — only a visibility change and a factory method.

- **CTX-007**: `DeviceHandle` and `DeviceHandle<TDevice>` (the sealed, delegate-configured leaf types in the companion session library) demonstrate that the preferred the companion session library pattern for lifecycle composition is already delegate-based rather than inheritance-based. An `IByteSource`-implementing device type continues this pattern at the IO layer.

- **CTX-008**: When a transceiver is constructed from an externally managed `IByteSource` (i.e., one whose open/close lifecycle is driven by `DeviceHandleBase`), the `ITransceiver.Open` and `ITransceiver.Close` calls are redundant. The device is guaranteed open for the duration of `OnLoopAsync`. A wrapping transceiver must not attempt to re-open or independently close the underlying source.

- **CTX-009**: A `the companion session library.Serial` package referencing only the `CallAndResponse` core package would acquire a dependency only on the six-primitive `IByteSource` interface, not on any transport-specific package. The dependency graph remains acyclic: `the companion session library.Serial` → `CallAndResponse` core; `CallAndResponse.Transport.Serial` → `CallAndResponse` core. Neither depends on the other.

- **CTX-010**: The `Transceiver.Wrap(IByteSource)` factory would return `ITransceiver`. The concrete wrapping type (`DelegatingTransceiver`) is an internal sealed adapter shim with zero logic of its own; it routes `Transceiver`'s abstract `ByteSource` property to the injected `IByteSource` instance.

- **CTX-011**: Making `IByteSource` public is a **non-breaking** change to existing consumers. All existing transport implementations (`SerialByteSource`, `BleByteSource`, `FakeByteSource`) are already `internal` and are unaffected. Existing tests and protocol clients hold references to `ITransceiver` and `Transceiver`, neither of which changes.

- **CTX-012**: The library targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8. `IByteSource` as defined in ADR-0007 uses only BCL types available on all three targets. No target framework constraint affects this decision.

- **CTX-013**: During design of this ADR, a related question was raised: should `Open`, `Close`, and `IsOpen` be removed from `ITransceiver` entirely, leaving it as a pure communication interface? The argument is that, in the `Transceiver.Wrap` scenario, lifecycle is already managed externally by `DeviceHandleBase`, making those members meaningless on the returned `ITransceiver`. This question was considered and explicitly rejected; see ALT-005.

## Decision

Promote `IByteSource` to a public API surface and provide a `Transceiver.Wrap(IByteSource)` static factory that returns a pre-opened `ITransceiver` whose lifecycle is externally managed.

- **DEC-001**: `IByteSource` visibility is changed from `internal` to `public`. Its member signatures are unchanged. The interface is part of the `CallAndResponse` core package and namespace. No members are added or removed as part of this promotion.

- **DEC-002**: A static factory method `Transceiver.Wrap(IByteSource source)` is added to `Transceiver`. It returns `ITransceiver`. The returned instance is a `DelegatingTransceiver` — an `internal sealed` subclass of `Transceiver` that supplies the injected `IByteSource` via `Transceiver`'s abstract `ByteSource` property (or equivalent construction-time injection point). `DelegatingTransceiver` contains no logic beyond this routing.

- **DEC-003**: `ITransceiver.Open` and `ITransceiver.Close` on a `DelegatingTransceiver` are **no-ops**. The lifecycle contract for a wrapped transceiver is: the caller guarantees that the `IByteSource` is open and will remain open for the duration of use. `DelegatingTransceiver.IsOpen` delegates to `IByteSource.IsConnected`. Calling `Open` or `Close` on a `DelegatingTransceiver` does not throw; it has no effect on the underlying source.

- **DEC-004**: The natural integration point in the companion session library IO libraries is `DeviceHandleBase.OnLoopAsync`. An `IByteSource`-implementing device type opens the raw IO resource in `OpenDeviceAsync`, returns it as `TDevice`, and in `OnLoopAsync` calls `Transceiver.Wrap(device)` to obtain a full-fidelity `ITransceiver` for the duration of that connection.

- **DEC-005**: `the companion session library.Serial` (and any future the companion session library IO package) references the `CallAndResponse` core package solely for `IByteSource` and `ITransceiver`. It does not reference any `CallAndResponse.Transport.*` package. The dependency is on the interface and the factory, not on any hardware-specific transport implementation.

- **DEC-006**: `IByteSource` is documented at the interface level to state the externally-managed lifecycle contract: implementations provided to `Transceiver.Wrap` must be open before wrapping and must not be closed by the wrapping transceiver. This contract is enforced by documentation and by the no-op `Open`/`Close` implementation in `DEC-003`; it is not enforced by runtime state guards in the initial implementation.

- **DEC-007**: `FakeByteSource` remains `internal`. It is a test primitive for the `CallAndResponse` test suite and is not part of the public `IByteSource` contract. Third-party consumers implementing `IByteSource` for test purposes write their own fakes against the public interface.

## Consequences

### Positive

- **POS-001**: the companion session library IO libraries gain the full `Transceiver` accumulation loop, framing strategy, idle-timeout support, and all convenience methods (`SendReceiveExactly`, `SendReceiveHeaderFooter`, etc.) without inheriting `Transceiver` or any CallAndResponse base class.

- **POS-002**: The dependency between libraries is narrow and directional: `the companion session library.Serial` → `IByteSource` + `ITransceiver`. No circular dependency is introduced. CallAndResponse has no knowledge of the companion session library.

- **POS-003**: Protocol clients (`ModbusRtuClient`, custom handlers) remain unchanged. They accept `ITransceiver`; whether that transceiver wraps a `SerialByteSource`, a `BleByteSource`, or a `the companion session library.Serial` device is invisible to them.

- **POS-004**: The pattern is open-ended: any external library, embedded hardware adapter, or test harness can implement the six-primitive `IByteSource` and receive production-grade framing and convenience without adopting any CallAndResponse base class.

- **POS-005**: `DeviceHandleBase`'s reconnect state machine and `Transceiver`'s accumulation loop compose without interference. The reconnect loop cancels `_connectionCts`; the accumulation loop observes the same token. When `OnLoopAsync` exits (via cancellation or exception), `DeviceHandleBase` triggers reconnect as designed. No coordination between the two state machines is required.

- **POS-006**: Making `IByteSource` public is non-breaking. Existing transport packages, protocol clients, and consumers are unaffected. The change introduces new surface area; it does not modify existing surface area.

- **POS-007**: Third-party transport authors who previously had to subclass `Transceiver` (an abstract class) may now instead implement `IByteSource` (an interface) and use `Transceiver.Wrap()`. This is strictly more flexible: interface implementation is composable with any other class hierarchy the author needs.

### Negative

- **NEG-001**: Promoting `IByteSource` to `public` is a **permanent, forward-compatible commitment**. Future changes to the interface (adding members, changing signatures) will be breaking changes for any external implementor. The interface must be considered stable before promotion; any anticipated extensions should be evaluated now.

- **NEG-002**: The no-op `Open`/`Close` contract on `DelegatingTransceiver` (DEC-003) diverges from the behavior of all other `ITransceiver` implementations, where `Open` and `Close` have meaningful side effects. Code that defensively calls `Open` before use or `Close` after use will silently succeed without the expected effect. This is correct by design but requires clear documentation.

- **NEG-003**: External `IByteSource` implementors must understand and uphold the `ReadByteAsync` and `ReadChunkAsync` contract: blocking until data is available, correct behavior on cancellation, and the single-reader assumption documented in ADR-0007. Incorrect implementations will produce silent framing errors rather than loud exceptions.

- **NEG-004**: `DelegatingTransceiver` as an `internal sealed` type means that `Transceiver.Wrap` cannot be subclassed or extended by consumers. If a the companion session library IO library needs to override any `Transceiver` behavior beyond routing the `IByteSource`, it must subclass `Transceiver` directly (regaining the original inheritance constraint) or use a separate composition approach.

## Alternatives Considered

- **ALT-001 — Keep `IByteSource` internal; require subclassing `Transceiver`**: The status quo. the companion session library IO libraries would subclass `Transceiver` to get accumulation and framing, and then cannot also subclass `DeviceHandleBase`. Rejected because it forces a choice between the reconnect state machine and the framing engine; both are needed simultaneously.

- **ALT-002 — Introduce a new public `ITransportPrimitive` interface parallel to `IByteSource`**: A new interface with different naming could be introduced to avoid committing the exact `IByteSource` name. Rejected because it duplicates an already-correct abstraction and forces all existing `IByteSource` implementations to be adapted or re-declared. The `IByteSource` name is already internally consistent and accurate.

- **ALT-003 — Move accumulation loop to a standalone static helper, not tied to `Transceiver`**: A `TransceiverHelper.Accumulate(IByteSource, detectMessage, ct)` method would decouple framing from the `Transceiver` class entirely. Rejected because it would fragment the `ITransceiver` convenience method implementations and require consumers to wire the helper manually. `Transceiver.Wrap()` preserves the existing convenience API unchanged.

- **ALT-004 — Provide a `CallAndResponse.Composition` package with `DelegatingTransceiver` as a public type**: The adapter could live in a separate package that both `CallAndResponse` core and the companion session library packages reference. Rejected as over-engineering for a two-type addition (one public interface, one internal adapter shim) that fits naturally in the existing core package.

- **ALT-005 — Remove lifecycle members (`Open`, `Close`, `IsOpen`) from `ITransceiver`**: Split `ITransceiver` into two interfaces — a pure communication contract (`Send`, `ReceiveMessage`) and a separate lifecycle contract (`IsOpen`, `Open`, `Close`) — so that `Transceiver.Wrap` could return an interface with no lifecycle surface at all, accurately reflecting that the wrapped transceiver's lifecycle is externally managed.

  *Arguments for*: The Interface Segregation Principle supports separating concerns that have different callers. Protocol clients (`ModbusRtuClient`, custom handlers) only call `Send` and `ReceiveMessage`; they do not open or close the transceiver. Lifecycle is an operational concern belonging to the application layer, not the protocol layer. Removing `Open`/`Close` from `ITransceiver` would make a `DelegatingTransceiver`'s no-op contract honest at the type level rather than documented-but-silent: callers who hold only the communication interface physically cannot call lifecycle methods on a pre-opened instance. This also opens the door to wrapping arbitrary pre-opened channels (`Stream`, `WebSocket`, named pipe) whose notion of open/close is either not applicable or already managed elsewhere.

  *Arguments against*: `Send` and `ReceiveMessage` both guard on `IsOpen` and throw `InvalidOperationException` if the transceiver has not been opened. Without `IsOpen` on the interface, that guard becomes invisible to protocol clients and untestable against the interface type. Splitting the interface would require protocol clients to either accept two interface parameters (communication + lifecycle), accept `Transceiver` concretely (regressing the abstraction), or drop the open guard entirely. The existing `ModbusRtuClient` and `Stm32BootloaderClient` accept `ITransceiver` and their callers are responsible for opening it first — a clean, explicit contract that works correctly today. Furthermore, this is a **breaking change** to `ITransceiver` that does not resolve the composition problem being addressed here: `DelegatingTransceiver` already handles the no-op lifecycle contract internally via its `OpenCore`/`CloseCore` overrides, without any change to the public interface. The tension the proposal identifies is real, but the `Transceiver.Wrap` + no-op pattern resolves it at the implementation level without imposing an interface split on all existing consumers. Rejected; the current `ITransceiver` surface is already minimal (ADR-0005, ADR-0007 DEC-006) and the identified asymmetry is better addressed by documentation than by interface surgery.

  > **Note (superseded by ADR-0011)**: The rejection of ALT-005 in this ADR was subsequently revisited in [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md), which accepted the interface split as the correct long-term architecture. ADR-0011 introduces `IManagedTransceiver` (carrying `IsOpen`, `Open`, `Close`) as an extension of the now-slim `ITransceiver` (carrying only `Send`, `ReceiveMessage`). The core decision of this ADR — making `IByteSource` public and providing `Transceiver.Wrap` — remains unchanged and was a prerequisite for ADR-0011.

## Implementation Notes

- **IMP-001**: Change `IByteSource` access modifier from `internal` to `public` in `Source/CallAndResponse/IByteSource.cs`. No other changes to the file are required.

- **IMP-002**: Add `internal sealed class DelegatingTransceiver : Transceiver` in the `CallAndResponse` core package. It has a single constructor accepting `IByteSource` and overrides the abstract member that supplies the byte source to the base class. It overrides `Open` and `Close` to no-ops; `IsOpen` delegates to `_source.IsConnected`.

- **IMP-003**: Add `public static ITransceiver Wrap(IByteSource source)` to `Transceiver`. One line: `return new DelegatingTransceiver(source);`.

- **IMP-004**: XML doc on `IByteSource` should state: "Implementations provided to `Transceiver.Wrap` must be open before wrapping. The wrapping transceiver will not call `OpenAsync` or `CloseAsync`; lifecycle management is the caller's responsibility."

- **IMP-005**: XML doc on `Transceiver.Wrap` should state: "Returns a pre-opened transceiver backed by the provided `IByteSource`. `Open` and `Close` on the returned `ITransceiver` are no-ops. The caller is responsible for the lifecycle of `source`."

- **IMP-006**: A `the companion session library.Serial` integration example should demonstrate the pattern: implement `IByteSource` on the device type, return it from `OpenDeviceAsync`, and call `Transceiver.Wrap(device)` at the top of `OnLoopAsync`.

- **IMP-007**: Evaluate whether `IByteSource` should live in a new `CallAndResponse.Abstractions` package (netstandard2.0 only, zero dependencies) to allow the companion session library IO packages to take the smallest possible dependency. This is a packaging question deferred to the implementation phase; it does not affect the interface contract.

- **IMP-008**: Existing `FakeByteSource` in the test project remains `internal`. No test-facing `IByteSource` adapter is shipped in this change.

- **IMP-009**: `Transceiver` already accepts `IByteSource` via constructor injection — `internal Transceiver(IByteSource byteSource)` and `internal Transceiver(IByteSource byteSource, ILogger logger)`. `DelegatingTransceiver` simply calls `base(source)` with no abstract property needed. Because `DelegatingTransceiver` is `internal` and lives in the same assembly as `Transceiver`, it can call those `internal` constructors directly — no visibility promotion is required. The only structural changes to `Transceiver` are the addition of the `public static Wrap(IByteSource)` factory method; the existing constructors are untouched.

## References

- **REF-001**: [ADR-0007](adr-0007-byte-source-abstraction-and-transceiver-layering.md) — established `IByteSource` as `internal` and documented the single-reader accumulation loop contract.
- **REF-002**: [ADR-0009](adr-0009-device-discovery-out-of-scope.md) — established the companion session library as the recommended companion for device discovery; the companion session library IO libraries are the primary consumer of this new public surface.
- **REF-003**: `Source/CallAndResponse/IByteSource.cs` — current `internal` declaration of the six-primitive interface.
- **REF-004**: `Source/CallAndResponse/Transceiver.cs` — abstract base class that will receive the `Wrap` factory method.
- **REF-005**: `the companion session library/DeviceHandleBase.cs` — lifecycle state machine whose `OnLoopAsync` hook is the intended injection point for `Transceiver.Wrap`.
- **REF-006**: `the companion session library/DeviceHandle.cs` — delegate-composition pattern in the companion session library that this decision extends to the IO layer.
