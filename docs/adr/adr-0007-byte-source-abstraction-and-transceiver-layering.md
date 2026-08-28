---
title: "ADR-0007: Byte-Source Abstraction and Transceiver Layering"
status: "Superseded"
date: "2026-06-01"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "transport", "abstraction"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0007: Byte-Source Abstraction and Transceiver Layering

> **SUPERSEDED.** `IByteSource` no longer exists. The layering idea in this record was right and survives; the interface it
> introduced was replaced wholesale by `System.IO.Pipelines.IDuplexPipe`, which supplies the same seam from
> the BCL. See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

*Implementation complete for Phases 1–7. `FrameDetectionResult`, `IByteSource`, `AsyncBuffer<T>` (internal), `SerialByteSource`, `BleByteSource`, `FakeByteSource`, and idle-timeout `ReceiveMessage` are all in production. `ITransceiver` is slimmed to five members. `ModbusRtuClient` and `Stm32BootloaderClient` accept `Transceiver`. `FakeTransceiver` delegates to `FakeByteSource` with no `ReceiveMessage` override. One deviation from DEC-002: `ReceiveMessage` is `virtual` rather than `sealed` on `Transceiver`; `TreehopperTransceiver` retains a legacy `ReceiveMessage` override pending migration to an `IByteSource`-based implementation (see NEG-005).*

## Context

The current `Transceiver` / `ITransceiver` architecture has a structural inconsistency that grows more visible as transport implementations are added.

- **CTX-001**: `ITransceiver` declares `ReceiveMessage(Func<ReadOnlyMemory<byte>, (int, int)> detectMessage, CancellationToken)` as an abstract method that every concrete transport must implement independently.
- **CTX-002**: `SerialPortTransceiver.ReceiveMessage` polls `BytesToRead` on a tight loop, reads into a `byte[]` array in chunks, and invokes `detectMessage` at the array level after each chunk.
- **CTX-003**: `BleNordicUartTransceiver.ReceiveMessage` reads one byte at a time from an internal `Channel<byte>` fed by a BLE NOTIFY callback, accumulating into a `List<byte>`, and invokes `detectMessage` after each byte.
- **CTX-004**: Both implementations duplicate the same accumulation loop, the same `detectMessage` invocation pattern, the same buffer-overflow guard, and the same cancellation check. They differ only in how they obtain the next byte or chunk.
- **CTX-005**: The `detectMessage` delegate returns a raw `(int offset, int length)` tuple with no documented contract for what sentinel values (`(0, 0)`, `(-1, -1)`) mean. The existing `Transceiver` base class uses both inconsistently across its own convenience methods.
- **CTX-006**: Because `ReceiveMessage` is abstract, every new transport author must re-implement the framing loop correctly — including the accumulation strategy, cancellation handling, and buffer management. This is a transport concern that has leaked into protocol-layer logic.
- **CTX-007**: The experimental branch introduced `AsyncBuffer<T>` (a `ConcurrentQueue<T>` + `SemaphoreSlim` combination) and a pair of `Channel<byte>` fields (`_bytesIn`, `_bytesOut`) on `Transceiver` itself. These were connected to the sketch of a channel-based `ReceiveMessage` implementation in pseudocode but were never wired to any transport.
- **CTX-008**: `AsyncBuffer<T>` and `Channel<byte>` exist as two competing queue abstractions for the same conceptual job in the experimental branch. `Channel<byte>` is already in the BCL and is used by `BleNordicUartTransceiver`. `AsyncBuffer<T>` adds one capability `Channel<T>` lacks: non-destructive `Snapshot()` of the current queue contents, which is necessary for `detectMessage` to inspect accumulated bytes without consuming them.
- **CTX-009**: A `ReceiveChunk` method was sketched in the experimental branch to handle devices that do not frame their output — e.g., barcode scanners or GPS modules that emit unsolicited data. The sketch correctly identified that the existing `detectMessage` delegate cannot implement idle-timeout termination because the delegate is only invoked after a new byte arrives; a timeout that fires before new data arrives will never trigger `detectMessage`.
- **CTX-010**: The experimental branch did not attempt to change `ITransceiver`, the public interface, or the convenience methods. The exploration was focused entirely on the internal layering between `Transceiver` and its concrete subclasses.
- **CTX-011**: The library targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8. Any new abstraction must be implementable on netstandard2.0. `Channel<T>` requires netstandard2.1; `AsyncBuffer<T>` as written is portable to netstandard2.0.
- **CTX-012**: `ITransceiver` currently declares 12 methods. Eight of these are convenience wrappers over `Send` + `ReceiveMessage` that are implemented entirely in `Transceiver` and have no transport-specific behavior. Declaring them on the interface forces mock authors to stub all 12 and forces transport authors to be aware of methods they should never override. This is a separate but related concern addressed in the decision below.
- **CTX-013**: The sentinel inconsistency in CTX-005 is not hypothetical — it exists today in `Transceiver.cs`. Five of the six internal `detectMessage` lambdas signal an incomplete frame by returning `(0, 0)` (`ReceiveUntilTerminatorPattern`, `ReceiveUntilHeaderFooterMatch`, `ReceiveUntilTerminator`, `ReceiveExactly`, and the `SendReceive` pass-through). One outlier — `ReceiveUntilPerfectMatch` — returns `(-1, -1)` for incomplete. `FakeTransceiver.ReceiveMessage`, which is the Tier 2 test primitive, exits its accumulation loop when `length > 0`. This condition is correct for the `(0, 0)` convention but silently wrong for `(-1, -1)`: a test exercising `ReceiveUntilPerfectMatch` through `FakeTransceiver` would loop forever rather than exit when a complete match is found. `FrameDetectionResult.IsComplete` eliminates this entire class of defect; it is not separable from the `IByteSource` accumulation loop because both describe the same centralized framing contract that currently does not exist.

## Decision

Introduce an internal byte-source abstraction that decouples transport-level I/O from the accumulation and framing logic in `Transceiver`. Simultaneously slim `ITransceiver` to its four primitive operations.

- **DEC-001**: Define an internal interface, provisionally named `IByteSource`, that represents the raw I/O contract a transport must fulfill. It declares:
  - `Task OpenAsync(CancellationToken token)` — acquire and open the underlying I/O resource.
  - `Task CloseAsync(CancellationToken token)` — release the I/O resource.
  - `Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)` — write bytes to the transport.
  - `Task<byte> ReadByteAsync(CancellationToken token)` — read exactly one byte, blocking until one is available or the token is cancelled.
  - `bool IsConnected { get; }` — synchronous connectivity check for guard logic.
- **DEC-002**: `Transceiver` owns the accumulation loop and `detectMessage` invocation. It accepts an `IByteSource` at construction time and implements `ReceiveMessage` once, correctly, without duplication. No concrete transport subclass may override `ReceiveMessage`.
- **DEC-003**: `Transceiver` uses `AsyncBuffer<byte>` as its internal accumulation buffer during a `ReceiveMessage` call. The buffer accumulates bytes from `IByteSource.ReadByteAsync` and exposes them to `detectMessage` via `Snapshot()` for non-destructive inspection. When `detectMessage` signals a complete frame, the buffer is drained by the confirmed payload range.
- **DEC-004**: `AsyncBuffer<T>` remains in the core package but is `internal`. It is not a public API. Transport authors and library consumers never reference it directly.
- **DEC-005**: `IByteSource` is `internal`. Transport packages subclass `Transceiver` and provide a concrete `IByteSource` implementation to the base constructor. The `IByteSource` seam is never exposed in the public API.
- **DEC-006**: `ITransceiver` is slimmed to four methods that represent observable public behavior:
  - `bool IsOpen { get; }`
  - `Task Open(CancellationToken token)`
  - `Task Close(CancellationToken token)`
  - `Task Send(ReadOnlyMemory<byte> writeBytes, CancellationToken token)`
  - `Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectMessage, CancellationToken token)`
  The eight convenience methods (`SendReceiveExactly`, `SendReceiveHeaderFooter`, etc.) are removed from `ITransceiver`. They remain as concrete implementations on `Transceiver` and are available to all consumers via the concrete type or through a future static extension class. `ModbusRtuClient` and `Stm32BootloaderClient` accept `Transceiver` directly; their Tier 3 tests use `FakeTransceiver` rather than NSubstitute mocks.
- **DEC-007**: The `detectMessage` return tuple `(int offset, int length)` is replaced by a named struct `FrameDetectionResult` with properties `bool IsComplete`, `int PayloadOffset`, and `int PayloadLength`. A static `FrameDetectionResult.Incomplete` sentinel eliminates the ambiguous `(0, 0)` / `(-1, -1)` dual-sentinel problem. This is a breaking change to `ITransceiver` and all `detectMessage` call sites but is manageable because it is a purely mechanical rename.
- **DEC-008**: Idle-timeout framing (the `ReceiveChunk` use case) is supported by adding an optional `TimeSpan? idleTimeout` parameter to `ReceiveMessage`. The accumulation loop in `Transceiver` applies the timeout between calls to `IByteSource.ReadByteAsync`; if no byte arrives within the idle window, the current buffer contents are treated as a complete frame and returned. This is distinct from the `CancellationToken`, which represents caller-requested cancellation of the entire operation.
- **DEC-009**: The `_bytesOut` outbound channel from the experimental branch is not adopted. `Send` / `WriteAsync` is already a direct synchronous-to-async call; an outbound buffer adds latency and complexity without addressing any identified defect.
- **DEC-010**: Existing concrete transport subclasses (`SerialPortTransceiver`, `BleNordicUartTransceiver`) have been refactored to provide `IByteSource` implementations. `TreehopperTransceiver` retains a legacy `ReceiveMessage` override because the Treehopper USB HID receive API (`IUart.ReceiveAsync`) does not fit a one-byte-at-a-time `IByteSource` without an intermediate queue. A `TreehopperByteSource` is the intended follow-on to complete this migration.

## Consequences

### Positive

- **POS-001**: The accumulation loop, `detectMessage` invocation, buffer management, and cancellation handling are implemented exactly once in `Transceiver`. Every transport gets correct framing behavior without any per-transport logic.
- **POS-002**: New transport authors implement only `IByteSource` — four methods covering open, close, write, and read-one-byte. This is the minimal possible contract for a byte-stream transport.
- **POS-003**: `ITransceiver` becomes a four-method interface (plus `IsOpen`). Mocking in tests requires stubbing only those primitives. NSubstitute, Moq, and manual fakes all become dramatically simpler.
- **POS-004**: `FrameDetectionResult` eliminates the undocumented sentinel convention and makes `detectMessage` delegates self-documenting at the type level.
- **POS-005**: Idle-timeout support (`DEC-008`) closes the gap for push-oriented and unframed devices (barcode scanners, GPS NMEA streams, serial telemetry) without adding a new public method to `ITransceiver`.
- **POS-006**: `AsyncBuffer<T>` solves the non-destructive-peek problem that `Channel<T>` cannot — `detectMessage` can inspect the accumulated buffer without consuming it, which is required for correct framing.
- **POS-007**: The `IByteSource` seam makes transport internals testable in isolation. A `FakeByteSource` can be injected into any `Transceiver` subclass, enabling unit tests that verify framing behavior without hardware or a full transport.
- **POS-008**: Slimming `ITransceiver` to primitives aligns with the decision in ADR-0005 that transport abstractions should remain simple I/O primitives; higher-level behavior lives above them.

### Negative

- **NEG-001**: Slimming `ITransceiver` (DEC-006) and replacing the `detectMessage` tuple (DEC-007) are **breaking changes**. Any consumer depending on the convenience methods via the interface type, or passing a `(int, int)`-returning delegate, will need to migrate. In practice this primarily affects protocol clients in this repository (`ModbusRtuClient`, `Stm32BootloaderClient`) and any external consumers — the impact is bounded but real.
- **NEG-002**: `IByteSource.ReadByteAsync` is a one-byte-at-a-time contract. For high-throughput serial scenarios, byte-by-byte async transitions add overhead compared to chunk reads. The accumulation loop will need a chunk-read fast path (`ReadChunkAsync`) to remain performant on high-baud-rate links.
- **NEG-003**: `AsyncBuffer<T>` is backed by a `List<T>` for simplicity and portability. `Snapshot()` returns a copy via `ToArray()`. This is safe because only one reader calls `detectMessage` per `ReceiveMessage` invocation; the single-reader assumption is documented on the class.
- **NEG-004**: The `TimeSpan? idleTimeout` parameter on `ReceiveMessage` adds complexity to the accumulation loop. Correctly implementing a resettable idle timer inside an async loop without introducing race conditions or allocation pressure requires care.
- **NEG-005**: Transport implementations that need non-standard framing lose the `ReceiveMessage` override escape hatch once `ReceiveMessage` is sealed. In practice `TreehopperTransceiver` is the one real case: its `IUart.ReceiveAsync` API delivers data in chunks rather than individual bytes, making a clean `IByteSource.ReadByteAsync` wrapper non-trivial. `ReceiveMessage` is therefore `virtual` rather than `sealed` until `TreehopperTransceiver` is migrated to a `TreehopperByteSource`.

## Alternatives Considered

### Keep `ReceiveMessage` abstract on each transport (status quo)

- **ALT-001**: **Description**: Leave the current design in place. Each transport implements its own `ReceiveMessage`.
- **ALT-002**: **Rejection Reason**: Duplication of the accumulation loop across transports is the root cause of the inconsistency problem. It will worsen as transports are added. The experimental branch confirmed that the seam between byte-acquisition and framing logic is clean and that separating them requires minimal new surface area.

### Use `Channel<byte>` instead of `AsyncBuffer<byte>` as the accumulation buffer

- **ALT-003**: **Description**: Feed bytes from `IByteSource` into a `Channel<byte>` and have `ReceiveMessage` drain from the channel's reader.
- **ALT-004**: **Rejection Reason**: `Channel<T>` does not support non-destructive peek or snapshot of buffered contents. `detectMessage` must be able to inspect the accumulated buffer without consuming it; consuming bytes that belong to the next message would be a correctness defect. `Channel<T>` also requires netstandard2.1, while `AsyncBuffer<T>` is portable to netstandard2.0.

### Use `Channel<byte>` for transport-to-base feeding and `AsyncBuffer<byte>` for accumulation

- **ALT-005**: **Description**: Transports push bytes into a `Channel<byte>` (as BLE already does); the accumulation loop in `Transceiver` drains from the channel into a separate `AsyncBuffer<byte>` for `detectMessage` inspection.
- **ALT-006**: **Rejection Reason**: This adds a two-queue pipeline for every byte received with no correctness advantage. `IByteSource.ReadByteAsync` abstracts the transport's own queueing strategy; `Transceiver` only needs one buffer for accumulated bytes. The extra channel is YAGNI.

### Replace `(int, int)` tuple without introducing `FrameDetectionResult`

- **ALT-007**: **Description**: Keep the raw tuple but add XML documentation to define the sentinel contract.
- **ALT-008**: **Rejection Reason**: Documentation does not prevent callers from passing invalid sentinel combinations. The `(0, 0)` / `(-1, -1)` ambiguity already exists in the base class's own implementations. A named struct with a static `Incomplete` sentinel is a one-time change that eliminates the class of error permanently.

### Add a `ReceiveChunk` method to `ITransceiver` for idle-timeout scenarios

- **ALT-009**: **Description**: Add `Task<Memory<byte>> ReceiveChunk(TimeSpan idleTimeout, CancellationToken token)` as a separate method on `ITransceiver`.
- **ALT-010**: **Rejection Reason**: A new method on `ITransceiver` forces all transport authors and all mocks to implement it. An `idleTimeout` parameter on `ReceiveMessage` achieves the same result without changing the interface contract or the number of methods transport authors must be aware of. `ReceiveChunk` is implementable as a one-line convenience wrapper over `ReceiveMessage` with an `idleTimeout` if a named method is desired.

### Expose `IByteSource` as a public interface

- **ALT-011**: **Description**: Make `IByteSource` public so consumers can provide custom byte sources without subclassing a transport.
- **ALT-012**: **Rejection Reason**: Publishing `IByteSource` commits the library to a second public contract at the transport primitive level. Until the design is proven stable through the refactor of the existing three transports, it should remain internal. It can be promoted to public in a follow-on release without a breaking change.

### Implement `ReceiveMessage` using `System.IO.Pipelines.PipeReader`

- **ALT-013**: **Description**: Replace the custom accumulation loop with `PipeReader`, which provides a well-tested backpressure-aware byte accumulation abstraction.
- **ALT-014**: **Rejection Reason**: `System.IO.Pipelines` is a high-throughput networking abstraction that assumes a streaming data model. The library's primary use case is request-response framing over low-bandwidth embedded links (serial at 115200 baud, BLE at Nordic UART MTU). The complexity and allocation model of `PipeReader` is disproportionate to the problem. Additionally, `System.IO.Pipelines` is not available on netstandard2.0 without a NuGet dependency.

## Implementation Notes

- **IMP-001**: Define `IByteSource` as `internal` in the core `CallAndResponse` package. Do not place it in a transport-specific package; `Transceiver` must reference it at the base-class level.
- **IMP-002**: `Transceiver`'s accumulation loop in the new `ReceiveMessage` implementation: read one byte from `IByteSource.ReadByteAsync(token)`, append to `AsyncBuffer<byte>`, call `detectMessage(buffer.Snapshot())`, check `FrameDetectionResult.IsComplete`. If complete, drain confirmed payload range from the buffer and return. Loop otherwise.
- **IMP-003**: Add a `ReadChunkAsync(Memory<byte> destination, CancellationToken token) : Task<int>` method to `IByteSource` as an optional fast path. Provide a default implementation that calls `ReadByteAsync` in a loop. Transport implementations that can read chunks efficiently (e.g., `SerialPort.BaseStream.ReadAsync`) should override it. `Transceiver` can use chunk reads during the accumulation loop when available.
- **IMP-004**: `SerialByteSource` wraps `System.IO.Ports.SerialPort`. Its `ReadByteAsync` uses `BaseStream.ReadAsync` with a single-byte buffer. Crucially, it does not poll `BytesToRead` and does not contain the close/reopen `CancellationTokenSource(10)` pattern identified as high-risk in ADR-0003. This refactor is the Stage 1 remediation described in ADR-0003.
- **IMP-005**: `BleByteSource` wraps the existing `Channel<byte>` (`rxChannel`) that is already fed by the BLE NOTIFY callback. Its `ReadByteAsync` delegates to `rxChannel.Reader.ReadAsync(token)`. The `BleNordicUartTransceiver` becomes a thin wrapper over `BleByteSource` and the `Transceiver` base.
- **IMP-006**: `FrameDetectionResult` is a `readonly struct` in the core package. It is public because it appears in the signature of `ITransceiver.ReceiveMessage`. Provide implicit conversion operators or factory methods: `FrameDetectionResult.Incomplete`, `FrameDetectionResult.Complete(int offset, int length)`.
- **IMP-007**: The `detectMessage` return-type change to `FrameDetectionResult` affects only the six internal lambda definitions inside `Transceiver.cs` and the abstract/virtual signature on `ITransceiver`. `ModbusRtuClient` and `Stm32BootloaderClient` do not construct `detectMessage` delegates directly; they call convenience methods (`SendReceiveExactly`, `SendReceivePerfectMatch`, `SendReceiveHeaderFooter`) which are implemented entirely in `Transceiver`. Those protocol clients are affected by the DEC-006 interface slim (they must stop depending on `ITransceiver` for convenience methods) but are not affected by the DEC-007 delegate signature change.
- **IMP-008**: Update `FakeTransceiver` in the unit test project to implement the slimmed `ITransceiver`. The fake's `ReceiveMessage` can either inherit from `Transceiver` (gaining the real accumulation loop backed by a fake `IByteSource`) or remain a hand-rolled stub — both are valid depending on what is being tested.
- **IMP-009**: The idle-timeout accumulation path in `Transceiver.ReceiveMessage` should use `CancellationTokenSource.CreateLinkedTokenSource` to combine the caller's `token` with a per-byte idle deadline derived from `idleTimeout`. This avoids introducing a separate timer and integrates naturally with the existing cancellation pattern.
- **IMP-010**: Sequence the implementation: (1) define `FrameDetectionResult`, (2) slim `ITransceiver` and update all call sites, (3) define `IByteSource` and rewrite `Transceiver.ReceiveMessage`, (4) refactor `SerialPortTransceiver` to provide `SerialByteSource`, (5) refactor `BleNordicUartTransceiver` to provide `BleByteSource`, (6) update `FakeTransceiver` and all unit tests, (7) add idle-timeout support.
- **IMP-011**: ADR-0003 Stage 1 is complete. `SerialByteSource.ReadByteAsync` calls `BaseStream.ReadAsync` directly; there is no `BytesToRead` poll and no `CancellationTokenSource(10)` close/reopen. Stage 2 (RJCP.SerialPortStream) is not required.

## Implementation Plan

Each phase is a standalone, buildable, and test-passing increment. No phase may be merged unless all 93+ unit tests pass and the build is clean.

---

### Phase 1 — Introduce `FrameDetectionResult` and migrate all sentinel call sites *(complete)*

**Goal**: Replace the ambiguous `(int, int)` sentinel convention with a named struct. This is a purely mechanical, compile-verified change with no behavioral difference for any caller that was already using the `(0, 0)` convention correctly.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/FrameDetectionResult.cs` | **New** — `public readonly struct` with `bool IsComplete`, `int PayloadOffset`, `int PayloadLength`. Static factories: `FrameDetectionResult.Incomplete`, `FrameDetectionResult.Complete(int offset, int length)`. |
| `Source/CallAndResponse/ITransceiver.cs` | Update `ReceiveMessage` signature: `Func<ReadOnlyMemory<byte>, (int, int)>` → `Func<ReadOnlyMemory<byte>, FrameDetectionResult>`. |
| `Source/CallAndResponse/Transceiver.cs` | Rewrite all six internal `detectMessage` lambdas to return `FrameDetectionResult`. Specifically: fix `ReceiveUntilPerfectMatch` (currently the `(-1, -1)` outlier) to return `FrameDetectionResult.Incomplete` on no-match, consistent with all others. Update the `ReceiveMessage` abstract signature. |
| `Test/CallAndResponse.Test.Unit/Helpers/FakeTransceiver.cs` | Update `ReceiveMessage` override signature. Replace `var (offset, length) = detectMessage(...)` + `if (length > 0)` with `var result = detectMessage(...); if (result.IsComplete)`. |
| `Test/CallAndResponse.Test.Unit/TransceiverTests.cs` | Update any inline `detectMessage` lambdas passed to `ReceiveMessage` to return `FrameDetectionResult`. |

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass. ✓
- No occurrence of `(0, 0)` or `(-1, -1)` as a `detectMessage` return value remains anywhere in the repository. ✓
- `grep -r "(int, int)" Source/` returns no hits in `detectMessage`-related signatures. ✓

---

### Phase 2 — Slim `ITransceiver` to four primitive methods *(complete)*

**Goal**: Remove the eight convenience methods from `ITransceiver`. This is the breaking change for protocol clients and their Tier 3 tests. After this phase, protocol clients depend on `Transceiver` (the concrete base) rather than `ITransceiver`.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/ITransceiver.cs` | Remove eight convenience method declarations: `SendReceiveExactly`, `SendReceiveExactly` (overloads), `SendReceiveHeaderFooter`, `SendReceiveTerminator`, `SendReceiveTerminatorPattern`, `SendReceivePerfectMatch`, `SendReceive`. Retain: `IsOpen`, `Open`, `Close`, `Send`, `ReceiveMessage`. |
| `Source/CallAndResponse.Protocol.Modbus/ModbusRtuClient.cs` | Change constructor parameter type from `ITransceiver` to `Transceiver`. Update any stored field type accordingly. |
| `Source/CallAndResponse.Protocol.Stm32Bootloader/Stm32BootloaderClient.cs` | Same as above. |
| `Test/CallAndResponse.Test.Unit/ModbusRtuClientTests.cs` | Migrate `NSubstitute.For<ITransceiver>()` to `NSubstitute.For<Transceiver>()` (or a hand-written `FakeTransceiver`-based approach — see note). Mock only the four primitives; let convenience methods execute as real code on top of them. |
| `Test/CallAndResponse.Test.Unit/Stm32BootloaderClientTests.cs` | Same migration as `ModbusRtuClientTests.cs`. |

> **Note on Tier 3 test strategy**: `NSubstitute.For<Transceiver>()` requires `Transceiver` to have a mockable (non-sealed) constructor. If NSubstitute cannot mock the abstract base directly (because its constructor is internal-only after Phase 3), the Tier 3 tests should be refactored to use `FakeTransceiver` from Phase 6, which by that point will be a proper in-memory implementation of the full `Transceiver` base.

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass. ✓
- `ITransceiver` has exactly five members (plus the idle-timeout `ReceiveMessage` overload added in Phase 7): `IsOpen`, `Open`, `Close`, `Send`, `ReceiveMessage`. ✓
- No reference to `ITransceiver` in `ModbusRtuClient.cs` or `Stm32BootloaderClient.cs`. ✓

---

### Phase 3 — Define `IByteSource` and rewrite `Transceiver.ReceiveMessage` *(complete)*

**Goal**: Introduce the internal `IByteSource` contract and make `ReceiveMessage` concrete on `Transceiver`. This is the structural heart of the ADR; it removes `ReceiveMessage` as an abstract method and centralizes all framing logic.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/IByteSource.cs` | **New** — `internal interface IByteSource` with `Task OpenAsync(CancellationToken)`, `Task CloseAsync(CancellationToken)`, `Task WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`, `Task<byte> ReadByteAsync(CancellationToken)`, `Task<int> ReadChunkAsync(Memory<byte>, CancellationToken)` (default impl loops `ReadByteAsync`), `bool IsConnected`. |
| `Source/CallAndResponse/Transceiver.cs` | Add protected constructor overload accepting `IByteSource`. Remove `abstract` from `ReceiveMessage`. Implement `ReceiveMessage` as the canonical accumulation loop: create `AsyncBuffer<byte>`, loop `IByteSource.ReadByteAsync`, append, call `detectMessage(buffer.Snapshot())`, exit on `result.IsComplete`, return drained payload. Delegate `Open`/`Close`/`Send`/`IsOpen` to `IByteSource`. |
| `Source/CallAndResponse/AsyncBuffer.cs` | Change access modifier from `public` to `internal`. |

> **Temporary state**: Existing concrete transports (`SerialPortTransceiver`, `BleNordicUartTransceiver`) still compile because they inherit from `Transceiver` via the parameterless constructor path. Their `ReceiveMessage` overrides become dead code until Phase 4/5 remove them. This is acceptable — the compiler will warn about unreachable overrides if `ReceiveMessage` is `sealed` on the base; consider adding `sealed override` to the base implementation to surface these warnings immediately.

> **Deviation from DEC-002**: `ReceiveMessage` was implemented as `virtual` rather than `sealed` in order to support `TreehopperTransceiver`, which still overrides it with a custom receive loop (Treehopper's USB HID receive API does not fit cleanly into a one-byte-at-a-time `IByteSource`). Once `TreehopperTransceiver` is migrated to a `TreehopperByteSource`, the `virtual` modifier can be changed to `sealed`.

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass. ✓
- `ReceiveMessage` is not `abstract` on `Transceiver` (it is `virtual`). ✓
- `AsyncBuffer<T>` is `internal`. ✓
- `IByteSource` is `internal` and not referenced by any public type. ✓

---

### Phase 4 — `SerialByteSource` and `SerialPortTransceiver` refactor *(complete)*

**Goal**: Wire `SerialPortTransceiver` to `IByteSource`. This is also the Stage 1 remediation for ADR-0003: the new `ReadByteAsync`-based path replaces the `BytesToRead` polling loop and the risky `CancellationTokenSource(10)` close pattern.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse.Transport.Serial/SerialByteSource.cs` | **New** — `internal sealed class SerialByteSource : IByteSource`. Wraps `System.IO.Ports.SerialPort`. `OpenAsync` opens the port. `CloseAsync` closes cleanly. `WriteAsync` calls `BaseStream.WriteAsync`. `ReadByteAsync` calls `BaseStream.ReadAsync` with a single-byte buffer. `ReadChunkAsync` calls `BaseStream.ReadAsync` with the provided buffer. `IsConnected` returns `IsOpen`. Does **not** poll `BytesToRead`. Does **not** use `CancellationTokenSource(10)`. |
| `Source/CallAndResponse.Transport.Serial/SerialPortTransceiver.cs` | Construct `SerialByteSource` and pass it to `Transceiver` base constructor. Remove the `ReceiveMessage` override. |

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass. ✓
- `SerialPortTransceiver` has no `ReceiveMessage` override. ✓
- `BytesToRead` polling and `CancellationTokenSource(10)` pattern do not appear in `SerialPortTransceiver.cs` or `SerialByteSource.cs`. ✓
- ADR-0003 Stage 1 is complete: the `SerialByteSource` `BaseStream.ReadAsync`-based path eliminated the dropped-leading-bytes risk. Stage 2 is not required. ✓

---

### Phase 5

**Goal**: Wire `BleNordicUartTransceiver` to `IByteSource`. The existing `rxChannel` (`Channel<byte>`) becomes the backing queue inside `BleByteSource`.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse.Transport.Ble/BleByteSource.cs` | **New** — `internal sealed class BleByteSource : IByteSource`. Holds the existing `Channel<byte>` (`rxChannel`). `ReadByteAsync` delegates to `rxChannel.Reader.ReadAsync(token)`. `WriteAsync` calls the BLE GATT write characteristic method. `OpenAsync`/`CloseAsync` manage the BLE connection lifecycle. `IsConnected` reflects connection state. |
| `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` | Move `rxChannel` ownership into `BleByteSource`. Construct `BleByteSource` and pass it to `Transceiver` base constructor. Remove the `ReceiveMessage` override. |

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass. ✓
- `BleNordicUartTransceiver` has no `ReceiveMessage` override. ✓
- `rxChannel` is owned by `BleByteSource`, not `BleNordicUartTransceiver`. ✓

---

### Phase 6 — Refactor `FakeTransceiver` to use `FakeByteSource` *(complete)*

**Goal**: Replace the hand-rolled `ReceiveMessage` override in `FakeTransceiver` with a `FakeByteSource` that provides pre-queued bytes to the real accumulation loop in `Transceiver`. This validates the accumulation loop end-to-end using real framing logic in tests.

**Files changed**:

| File | Change |
|---|---|
| `Test/CallAndResponse.Test.Unit/Helpers/FakeByteSource.cs` | **New** — `internal sealed class FakeByteSource : IByteSource`. Backs `ReadByteAsync` from a `Queue<byte>` (the existing `RxBuffer`). `WriteAsync` appends to a `List<byte>` (`SentBytes`). `IsConnected` returns `true`. `OpenAsync`/`CloseAsync` are no-ops. Exposes `EnqueueRx(IEnumerable<byte>)` helper. |
| `Test/CallAndResponse.Test.Unit/Helpers/FakeTransceiver.cs` | Remove `ReceiveMessage` override. Remove `RxBuffer Queue<byte>` and `SentBytes List<byte>` fields — delegate to `FakeByteSource`. Construct `FakeByteSource` and pass to `Transceiver` base constructor. Preserve `EnqueueRx` and `SentBytes` as pass-through accessors to `FakeByteSource`. |
| `Test/CallAndResponse.Test.Unit/TransceiverTests.cs` | Verify all tests still pass with no changes required (the public `FakeTransceiver` API should be stable). If any test relied on the override's specific byte-delivery timing, note it as a behavioral fix rather than a regression. |

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass with no changes to `TransceiverTests.cs` test bodies. ✓
- `FakeTransceiver` has no `ReceiveMessage` override. ✓
- `FakeByteSource` is the sole byte-delivery primitive in test helpers. ✓
- `REF-011` in this ADR is satisfied: `FakeByteSource` is a recognized Tier 2 testing primitive. ✓

---

### Phase 7 — Add idle-timeout support to `ReceiveMessage` *(complete)*

**Goal**: Extend the accumulation loop to support idle-timeout framing, closing the gap identified in CTX-009 for push-oriented devices (barcode scanners, GPS NMEA, serial telemetry) where no explicit end-of-frame marker exists.

**Files changed**:

| File | Change |
|---|---|
| `Source/CallAndResponse/ITransceiver.cs` | Add `idleTimeout` overload: `Task<Memory<byte>> ReceiveMessage(Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectMessage, TimeSpan idleTimeout, CancellationToken token)`. |
| `Source/CallAndResponse/Transceiver.cs` | Implement the `idleTimeout` overload. On each iteration, wrap the `IByteSource.ReadByteAsync(token)` call inside `CancellationTokenSource.CreateLinkedTokenSource(token, idleDeadlineCts.Token)`. Reset `idleDeadlineCts` on each byte received. If the idle deadline fires before a byte arrives, treat the current buffer as a complete frame and return it (equivalent to `FrameDetectionResult.Complete(0, buffer.Count)`). |
| `Test/CallAndResponse.Test.Unit/TransceiverTests.cs` | Add tests for idle-timeout path: (a) message terminated by idle timeout when no `detectMessage` match fires, (b) message terminated by `detectMessage` before idle timeout, (c) cancellation token takes priority over idle timeout. |

**Acceptance criteria**:
- Build is clean. ✓
- All tests pass including the new idle-timeout tests. ✓
- `ReceiveMessage` without `idleTimeout` is unaffected in behavior. ✓
- No `System.Threading.Timer` or `Task.Delay` usage — idle timeout is implemented exclusively via linked `CancellationTokenSource`. ✓

---

## References

- **REF-001**: `Source/CallAndResponse/ITransceiver.cs` — current 12-method interface; target of DEC-006 slim-down
- **REF-002**: `Source/CallAndResponse/Transceiver.cs` — concrete `ReceiveMessage` accumulation loop; `virtual` pending Treehopper migration (DEC-002)
- **REF-003**: `Source/CallAndResponse/AsyncBuffer.cs` — internal `AsyncBuffer<T>` used by the accumulation loop (DEC-004)
- **REF-004**: `Source/CallAndResponse.Transport.Serial/SerialPortTransceiver.cs` — no `ReceiveMessage` override; delegates to `SerialByteSource` (Phase 4)
- **REF-005**: `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` — no `ReceiveMessage` override; delegates to `BleByteSource` (Phase 5)
- **REF-006**: `Source/CallAndResponse.Protocol.Modbus/ModbusRtuClient.cs` — accepts `Transceiver`; uses `FrameDetectionResult` via convenience methods (Phase 2)
- **REF-007**: `Source/CallAndResponse.Protocol.Stm32Bootloader/Stm32BootloaderClient.cs` — accepts `Transceiver`; uses `FrameDetectionResult` via convenience methods (Phase 2)
- **REF-008**: `Test/CallAndResponse.Test.Unit/Helpers/FakeTransceiver.cs` — delegates to `FakeByteSource`; no `ReceiveMessage` override
- **REF-009**: `docs/adr/adr-0003-serial-transport-revision.md` — Stage 1 complete via `SerialByteSource` (IMP-011)
- **REF-010**: `Source/CallAndResponse.Transport.Treehopper/TreehopperTransceiver.cs` — retains legacy `ReceiveMessage` override; pending `TreehopperByteSource` migration
- **REF-011**: `Test/CallAndResponse.Test.Unit/Helpers/FakeByteSource.cs` — Tier 2 test primitive; implements `IByteSource` with a `Queue<byte>` for byte delivery
- **REF-012**: `docs/adr/adr-0005-result-types-for-top-level-itransceiver-api.md` — `ITransceiver` slim-down is consistent with keeping transport abstractions as I/O primitives
- **REF-013**: `docs/adr/adr-0001-testing-strategy.md` — `FakeByteSource` is the Tier 2 testing primitive alongside `FakeTransceiver`
