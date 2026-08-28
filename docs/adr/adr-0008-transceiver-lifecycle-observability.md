---
title: "ADR-0008: Transceiver Lifecycle Observability"
status: "Superseded"
date: "2026-06-01"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "lifecycle", "disposable", "events"]
supersedes: ""
superseded_by: "ADR-0011, ADR-0014. Lifecycle is no longer a concern of this library."
---

# ADR-0008: Transceiver Lifecycle Observability

## Status

**Superseded** by ADR-0011 and ADR-0014. The library no longer owns transport lifecycle, so lifecycle observability is not applicable.

## Context

Following the ADR-0007 refactoring, `ITransceiver` is a clean 6-member interface covering open/close/send/receive. However, the interface has no mechanism for lifecycle observability — consumers cannot detect unexpected disconnects, resource leaks, or state transitions without polling `IsOpen` or catching exceptions on the next I/O call.

- **CTX-001**: Every concrete transceiver owns an unmanaged or system resource: `SerialPortTransceiver` holds a `System.IO.Ports.SerialPort`, `BleNordicUartTransceiver` holds a BLE device handle (`IDevice`), and `TreehopperTransceiver` holds a USB board (`TreehopperUsb`). None of these types implement `IDisposable` or `IAsyncDisposable`. If a consumer forgets to call `Close()`, or if an exception interrupts the workflow between `Open()` and `Close()`, the underlying resource leaks.
- **CTX-002**: `SerialByteSource.CloseAsync` calls `SerialPort.Close()` and `SerialPort.Dispose()` — but only if `Close` is explicitly called. There is no finalizer safety net and no `using` / `await using` pattern available to consumers.
- **CTX-003**: `BleNordicUartTransceiver` already subscribes to `Adapter.DeviceDisconnected` internally (line 120) and reacts by setting `_isConnected = false` and logging. However, the consumer has **no way to observe** this state change — they discover the disconnect only when their next `Send` or `ReceiveMessage` call throws `TransceiverTransportException`.
- **CTX-004**: Serial ports can be physically unplugged (USB-to-serial adapters) or become unresponsive. `SerialPort.BaseStream.ReadAsync` will throw `IOException` or `UnauthorizedAccessException` in these cases, but there is no proactive notification to the consumer.
- **CTX-005**: The existing TODO on `Transceiver.cs` line 15 reads: *"provide an API to receive spontaneous messages. Will need to provide some events. Primary use case in mind is a COM port barcode scanner that can spit out data at any moment."* The idle-timeout `ReceiveMessage` overload added in ADR-0007 Phase 7 addresses the spontaneous-data framing use case, but the need for lifecycle events remains.
- **CTX-006**: The library targets netstandard2.0, netstandard2.1, and net8.0. `IAsyncDisposable` is available on netstandard2.1+ and net8.0 but requires `Microsoft.Bcl.AsyncInterfaces` on netstandard2.0. `IDisposable` is universally available.
- **CTX-007**: A `DataReceived` event on `ITransceiver` would create a parallel consumption path that competes with the `IByteSource` accumulation loop in `Transceiver.ReceiveMessage`. Consumers subscribed to both `DataReceived` and `ReceiveMessage` would face a race condition by design — bytes consumed by the event handler would not be available to the framing loop, and vice versa.

## Decision

Add resource-disposal semantics and a state-change event to `ITransceiver` and `Transceiver`. Do not add a `DataReceived` event.

- **DEC-001**: `ITransceiver` extends `IDisposable`. The `Dispose()` implementation on `Transceiver` calls `Close(CancellationToken.None)` synchronously (via `.GetAwaiter().GetResult()`) if `IsOpen` is true, then releases any remaining managed resources. This provides a universal safety net on all target frameworks, including netstandard2.0.
- **DEC-002**: `Transceiver` additionally implements `IAsyncDisposable` on netstandard2.1+ and net8.0 (via `#if` conditional compilation or multi-targeting). `DisposeAsync()` awaits `Close(CancellationToken.None)` if `IsOpen` is true, then releases resources. Consumers on modern frameworks can use `await using`; consumers on netstandard2.0 use `using`. Both paths are safe.
- **DEC-003**: `Transceiver` introduces a public event:
  ```
  event EventHandler<TransceiverStateChangedEventArgs>? StateChanged;
  ```
  `TransceiverStateChangedEventArgs` carries:
  - `TransceiverState OldState` — the state before the transition.
  - `TransceiverState NewState` — the state after the transition.

  `TransceiverState` is an enum: `Closed`, `Opening`, `Open`, `Closing`, `Disconnected`, `Disposed`.
- **DEC-004**: `Transceiver` raises `StateChanged` at the following transition points:
  - `Open()` entry → `Closed → Opening`
  - `Open()` success → `Opening → Open`
  - `Open()` failure → `Opening → Closed`
  - `Close()` entry → `Open → Closing`
  - `Close()` success → `Closing → Closed`
  - Unexpected disconnect (detected by transport) → `Open → Disconnected`
  - `Dispose()` / `DisposeAsync()` → `* → Disposed`
- **DEC-005**: The `StateChanged` event is declared on `ITransceiver` so that consumers programming against the interface can observe lifecycle transitions without downcasting. The `TransceiverState` enum and `TransceiverStateChangedEventArgs` are public types in the core package.
- **DEC-006**: Transport subclasses signal unexpected disconnects by calling a new `protected` method on `Transceiver`:
  ```
  protected void OnDisconnected()
  ```
  This method sets `IsOpen` to `false` and raises `StateChanged` with `Open → Disconnected`. Transport authors are responsible for calling `OnDisconnected()` when they detect a transport-level disconnect (e.g., BLE `DeviceDisconnected`, serial `IOException` on read). `Transceiver` does not attempt automatic reconnection — that is a consumer-level concern.
- **DEC-007**: A `DataReceived` event is **not** added to `ITransceiver`. The ADR-0007 architecture centralizes all byte accumulation in `Transceiver.ReceiveMessage` via `IByteSource`. A `DataReceived` event would create a competing consumption path, leading to race conditions between event subscribers and the framing loop. The spontaneous-data use case (barcode scanners, GPS NMEA) is already served by the idle-timeout `ReceiveMessage` overload (ADR-0007 DEC-008). If a future use case requires true push-based observation of raw bytes, it should be modeled as a separate `IObservable<ReadOnlyMemory<byte>>` or a dedicated monitoring tap on `IByteSource`, not as an event on the public transceiver interface.
- **DEC-008**: `ITransceiver` adds `StateChanged` as its 7th member. `ITransceiverFactory.CreateTransceiver` and `TransceiverBuilder.Build()` return types are unchanged in this ADR — the return-type alignment (`ITransceiver` → `Transceiver`) is tracked separately as a consistency fix from ADR-0007 Phase 2.

## Consequences

### Positive

- **POS-001**: `IDisposable` on `ITransceiver` enables `using` blocks for all consumers, preventing resource leaks from forgotten `Close()` calls or exception-interrupted workflows.
- **POS-002**: `IAsyncDisposable` on `Transceiver` (netstandard2.1+) enables `await using`, which avoids the synchronous blocking of `Dispose()` in async codepaths.
- **POS-003**: The `StateChanged` event gives consumers a single, consistent mechanism to observe all lifecycle transitions — including unexpected disconnects that were previously silent.
- **POS-004**: Transport authors have a clean `OnDisconnected()` hook rather than ad-hoc internal state management. `BleNordicUartTransceiver.DeviceDisconnectedHandler` becomes a one-liner: `OnDisconnected()`.
- **POS-005**: The `TransceiverState` enum provides a finite state machine that can be documented, tested, and reasoned about. Invalid transitions (e.g., `Send` while `Closed`) can be guarded by checking state rather than relying on transport-specific exceptions.
- **POS-006**: Not adding `DataReceived` preserves the single-reader accumulation model from ADR-0007, avoiding a class of concurrency bugs.

### Negative

- **NEG-001**: Adding `IDisposable` to `ITransceiver` is a **breaking change** for any external implementation of the interface that does not already implement `IDisposable`. In practice, the only known implementations are in this repository.
- **NEG-002**: Adding `StateChanged` to `ITransceiver` is a **breaking change** — any existing `ITransceiver` implementation must add the event. This is mitigated by the ADR-0007 observation that external implementations are unlikely and that protocol clients already depend on `Transceiver` directly.
- **NEG-003**: `IDisposable.Dispose()` calling `Close()` synchronously via `.GetAwaiter().GetResult()` can deadlock in single-threaded synchronization contexts (e.g., UI threads). Consumers on such contexts should prefer `await using` with `IAsyncDisposable`. The synchronous path is a safety net, not the primary disposal mechanism.
- **NEG-004**: The `TransceiverState` enum introduces a state machine that must be kept consistent across all transport implementations. A transport that forgets to call `OnDisconnected()` will leave the consumer observing `Open` state on a dead connection.
- **NEG-005**: Adding `Microsoft.Bcl.AsyncInterfaces` as a dependency for netstandard2.0 `IAsyncDisposable` support adds a transitive NuGet dependency. The alternative — `IAsyncDisposable` only on netstandard2.1+ — is acceptable and avoids the dependency at the cost of `await using` not being available on netstandard2.0.

## Alternatives Considered

### Use `IAsyncDisposable` only, without `IDisposable`

- **ALT-001**: **Description**: Implement only `IAsyncDisposable` on `Transceiver`, skipping `IDisposable` entirely.
- **ALT-002**: **Rejection Reason**: `IAsyncDisposable` is not available on netstandard2.0 without `Microsoft.Bcl.AsyncInterfaces`. More importantly, `IDisposable` is the universal .NET resource-management contract — analyzers, `using` statements, and existing patterns all expect it. Omitting it would leave netstandard2.0 consumers with no disposal safety net.

### Use a callback delegate instead of an event for state changes

- **ALT-003**: **Description**: Accept an `Action<TransceiverState, TransceiverState>` callback in the `Transceiver` constructor instead of exposing a public event.
- **ALT-004**: **Rejection Reason**: A constructor callback is a single-subscriber model. Events support multiple subscribers, which is important for scenarios like UI binding + logging + reconnection logic all observing the same transceiver. Events are also the idiomatic .NET pattern for observable state changes.

### Use `IObservable<TransceiverState>` instead of events

- **ALT-005**: **Description**: Expose lifecycle transitions as an `IObservable<TransceiverState>` stream, enabling Rx-style composition.
- **ALT-006**: **Rejection Reason**: `IObservable<T>` is defined in the BCL but practical use requires `System.Reactive`, which is a heavy dependency for a library targeting embedded serial/BLE transports. The event model is simpler, universally understood, and sufficient for the identified use cases. `IObservable` can be added later as an adapter without breaking changes.

### Add a `DataReceived` event for raw byte observation

- **ALT-007**: **Description**: Add `event EventHandler<DataReceivedEventArgs>? DataReceived` to `ITransceiver`, fired whenever bytes arrive from the transport.
- **ALT-008**: **Rejection Reason**: This creates a parallel consumption path that competes with `Transceiver.ReceiveMessage`. The framing loop reads bytes from `IByteSource` and accumulates them in `AsyncBuffer`; a `DataReceived` event would need to tap the same byte stream, leading to either duplicated bytes (event + buffer both see the byte) or consumed bytes (event steals bytes from the buffer). Neither model is correct. The idle-timeout `ReceiveMessage` overload serves the spontaneous-data use case without this conflict. See DEC-007.

### Automatic reconnection in `Transceiver`

- **ALT-009**: **Description**: When an unexpected disconnect is detected, have `Transceiver` automatically attempt to reopen the connection.
- **ALT-010**: **Rejection Reason**: Reconnection policy (retry count, backoff, timeout, whether to reconnect at all) is application-specific. Embedding it in the transport layer couples the library to a single recovery strategy. The `StateChanged` event with `Disconnected` state gives consumers the information they need to implement their own reconnection logic.

## Implementation Notes

- **IMP-001**: Define `TransceiverState` as a public enum in `Source/CallAndResponse/TransceiverState.cs`.
- **IMP-002**: Define `TransceiverStateChangedEventArgs` as a public class in `Source/CallAndResponse/TransceiverStateChangedEventArgs.cs`. It inherits from `EventArgs` and carries `TransceiverState OldState` and `TransceiverState NewState`.
- **IMP-003**: Add `event EventHandler<TransceiverStateChangedEventArgs>? StateChanged` to `ITransceiver.cs`.
- **IMP-004**: Add `IDisposable` to `ITransceiver`. Implement `Dispose()` on `Transceiver` with the `Close`-if-open pattern.
- **IMP-005**: Implement `IAsyncDisposable` on `Transceiver` conditionally via `#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER`. Use `DisposeAsync()` that awaits `Close(CancellationToken.None)`.
- **IMP-006**: Add a private `TransceiverState _state` field to `Transceiver`. Add a `protected void SetState(TransceiverState newState)` method that updates the field and raises `StateChanged`. Wire `Open`, `Close`, and `Dispose` to call `SetState` at the appropriate transition points.
- **IMP-007**: Add `protected void OnDisconnected()` to `Transceiver`. It calls `SetState(TransceiverState.Disconnected)`.
- **IMP-008**: Refactor `BleNordicUartTransceiver.DeviceDisconnectedHandler` to call `OnDisconnected()` instead of directly setting `_isConnected = false`.
- **IMP-009**: For `SerialPortTransceiver`, detect disconnect by catching `IOException` or `UnauthorizedAccessException` from `SerialByteSource.ReadByteAsync` and calling `OnDisconnected()`. Alternatively, subscribe to `SerialPort.ErrorReceived` inside `SerialByteSource` and surface it.
- **IMP-010**: Update `FakeTransceiver` to support `Dispose` and `StateChanged` — inherited from the updated `Transceiver` base. Add tests for state transitions in `TransceiverTests`.
- **IMP-011**: Consider guarding `Send` and `ReceiveMessage` in `Transceiver` with a state check: throw `InvalidOperationException` if `_state` is not `Open`. This centralizes the guard logic that `BleNordicUartTransceiver.Send` currently implements ad-hoc (line 184). This is tracked separately as a GitHub issue for state guard centralization.

## Implementation Plan

### Phase 1 — `TransceiverState` enum, `TransceiverStateChangedEventArgs`, and `StateChanged` event

**Goal**: Introduce the state model and event plumbing. No behavioral change to existing transports yet.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/TransceiverState.cs` | **New** — public enum with `Closed`, `Opening`, `Open`, `Closing`, `Disconnected`, `Disposed`. |
| `Source/CallAndResponse/TransceiverStateChangedEventArgs.cs` | **New** — public class with `OldState` and `NewState` properties. |
| `Source/CallAndResponse/ITransceiver.cs` | Add `event EventHandler<TransceiverStateChangedEventArgs>? StateChanged`. |
| `Source/CallAndResponse/Transceiver.cs` | Add `_state` field, `SetState()`, `OnDisconnected()`, and `StateChanged` event implementation. Wire into `Open`/`Close` lifecycle (guarded by base-class calls that subclasses invoke via `base.Open`/`base.Close` or a template-method pattern). |

**Acceptance criteria**:
- Build is clean.
- All existing tests pass.
- `StateChanged` is raised on `Open` and `Close` in `FakeTransceiver`-based tests.

---

### Phase 2 — `IDisposable` and `IAsyncDisposable`

**Goal**: Add disposal semantics to the interface and base class.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/ITransceiver.cs` | Add `: IDisposable`. |
| `Source/CallAndResponse/Transceiver.cs` | Implement `Dispose()` and conditionally `DisposeAsync()`. Transition to `Disposed` state. |
| `Source/CallAndResponse/CallAndResponse.csproj` | Conditionally add `Microsoft.Bcl.AsyncInterfaces` for netstandard2.0 if `IAsyncDisposable` is desired there, or skip (see NEG-005). |

**Acceptance criteria**:
- Build is clean.
- All existing tests pass.
- New test: `Dispose` on an open transceiver calls `Close` and transitions to `Disposed`.
- New test: `Dispose` on an already-closed transceiver transitions to `Disposed` without error.

---

### Phase 3 — Wire transport subclasses to `OnDisconnected()`

**Goal**: Concrete transports use the new lifecycle hooks.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` | `DeviceDisconnectedHandler` calls `OnDisconnected()` instead of directly setting `_isConnected`. |
| `Source/CallAndResponse.Transport.Serial/SerialByteSource.cs` | Catch `IOException` in `ReadByteAsync` and surface disconnect (mechanism TBD — either a callback to the transceiver or an `IsConnected` flag that `Transceiver` polls). |

**Acceptance criteria**:
- Build is clean.
- All existing tests pass.
- BLE disconnect test (if feasible with `FakeTransceiver`) verifies `StateChanged` fires with `Open → Disconnected`.
