---
title: "ADR-0001: Automated Testing Strategy"
status: "Accepted"
date: "2026-03-22"
authors: "Development Team"
tags: ["testing", "architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0001: Automated Testing Strategy

> **Still accepted; the layout it describes has changed.** `Transceiver` is `sealed`, not an abstract base;
> there is no builder infrastructure; the BLE package is `Transport.BleNordicUart`. The testing strategy
> itself — tiered tests, a fake at the transport seam, no hardware in CI — is unchanged and in force.


## Status

**Accepted**

## Context

The `call-and-response` library is a layered communication stack consisting of:

- **Core** (`CallAndResponse`): `ITransceiver` interface, `Transceiver` abstract base class with default protocol-agnostic receive/send implementations, and builder infrastructure.
- **Transports** (`Transport.Serial`, `Transport.Ble`): Concrete `Transceiver` implementations backed by real hardware I/O (serial ports, BLE).
- **Protocol clients** (`Protocol.Modbus`, `Protocol.Stm32Bootloader`): Higher-level clients that depend only on `ITransceiver` and implement framing, CRC, and response validation.

The existing `CallAndResponse.Test.Sandbox` project is a manual integration sandbox, not an automated test suite. There are no automated tests. The risk of regressions is high as protocol logic and frame-building code is non-trivial. Hardware-dependent transports make full end-to-end testing in CI impractical.

Key constraints:
- Projects target .NET Standard 2.0 / 2.1 (libraries) and .NET 8 (sandbox).
- Serial and BLE transports require physical hardware or virtual port drivers.
- `Transceiver` default implementations are the single largest bloc of logic shared across all transports and protocol clients.

## Decision

Adopt a three-tier automated testing strategy, prioritised by value-to-effort ratio and hardware independence:

**Tier 1 — Pure logic unit tests** (no I/O, no mocking):
Test all code with zero external dependencies. Targets:
- `ModbusRtuRequestBuilder`: frame construction and CRC-16 correctness.
- `ModbusUtils.Flip16BitValues`: byte-pair swap correctness.
- `TransceiverBuilder` and transport-specific builder stages (e.g., `SerialTransceiverBuilderStage`): fluent API sequencing, option validation, and factory delegation are pure logic and should be tested at this tier.

**Tier 2 — Fake-transport unit tests** (in-memory `FakeTransceiver`):
`FakeTransceiver` is a concrete `Transceiver` subclass backed by a `FakeByteSource` — a separate internal class that holds a `Queue<byte>` for received data and a `List<byte>` for captured sent bytes. `FakeTransceiver` provides no `ReceiveMessage` override; pre-loaded bytes are delivered one at a time through `FakeByteSource.ReadByteAsync` into the real `Transceiver` accumulation loop (see ADR-0007). Use it to exercise every default implementation in the `Transceiver` base class without touching real hardware. Targets:
- All `ReceiveUntil*` and `SendReceive*` methods on `Transceiver`.

**Tier 3 — FakeTransceiver-based unit tests** (no mocking):
Test protocol clients in isolation using `FakeTransceiver`. Because `ModbusRtuClient` and `Stm32BootloaderClient` accept `Transceiver` directly rather than `ITransceiver` (see ADR-0007 DEC-006), NSubstitute mocks of `ITransceiver` are not applicable at this tier. Pre-enqueued response bytes flow through the real `Transceiver` convenience methods, so Tier 3 tests validate complete request/response round-trips with no I/O. Targets:
- `ModbusRtuClient`: frame construction, response parsing, error-code propagation.
- `Stm32BootloaderClient`: command framing, ACK/NACK handling, chunked read/write.

**Tier 4 — Integration tests** (hardware-optional, excluded from CI by default):
- `SerialPortTransceiver`: loopback via a virtual null-modem port pair.
- `BleNordicUartTransceiver`: manual only, requires BLE hardware; documented in the sandbox project.
- Gated with `[Trait("Category", "Integration")]`; CI pipeline runs `dotnet test --filter Category!=Integration`.

**Test stack:**

| Concern | Package |
|---|---|
| Framework | `xunit` |
| Mocking | `NSubstitute` |
| Assertions | `FluentAssertions` |

**Project layout:**

```
Test/
  CallAndResponse.Test.Unit/         ← Tiers 1, 2, 3 (new)
  CallAndResponse.Test.Integration/  ← Tier 4 (future)
  CallAndResponse.Test.Sandbox/      ← manual exploration (existing)
```

## Consequences

### Positive

- **POS-001**: `ITransceiver` is already the correct seam — no production code refactoring is required to begin testing.
- **POS-002**: `FakeTransceiver` tests give high confidence in the framing/detection logic shared across all transports and protocols.
- **POS-003**: Protocol client tests are completely hardware-free and suitable for CI from day one.
- **POS-004**: Tier 1 (pure logic) tests can be written immediately with no infrastructure overhead and provide the highest confidence-per-line-of-test-code.
- **POS-005**: Separating integration tests behind a trait keeps CI fast while still allowing hardware-in-the-loop testing locally.

### Negative

- **NEG-001**: `BleNordicUartTransceiver` contains non-trivial connection and notification logic that remains untested by automated means in the short term.
- **NEG-002**: `FakeByteSource` must faithfully reproduce the byte-delivery timing contract of a real transport; subtle differences in delivery order could mask bugs in the accumulation loop. In practice this risk is low because `FakeByteSource.ReadByteAsync` blocks until a byte is available, mirroring real transport behavior.
- **NEG-003**: `SerialPortTransceiver` integration tests require a virtual COM port driver (`com0com` on Windows / `socat` on Linux), adding developer setup friction.
- **NEG-004**: The strategy does not include property-based or fuzz testing. Protocol framing code (CRC validation, coil packing, frame length invariants) is a strong candidate for exhaustive random input coverage using a library such as FsCheck or CsCheck. This gap means edge-case frame corruption can only be found by authoring explicit examples rather than being discovered automatically.

## Alternatives Considered

### Single integration test suite against real hardware

- **ALT-001**: **Description**: Write all tests against real serial or BLE devices, treating the stack end-to-end.
- **ALT-002**: **Rejection Reason**: Tests cannot run in CI without hardware; feedback loop is too slow; flakiness from hardware timing makes the suite unreliable.

### Mock `ITransceiver` for all `Transceiver` base class tests

- **ALT-003**: **Description**: Skip `FakeTransceiver`; instead mock `ReceiveMessage` on `ITransceiver` directly to test the default implementations.
- **ALT-004**: **Rejection Reason**: Mocking `ReceiveMessage` with correct delegate-based behaviour is complex and fragile. A concrete `FakeTransceiver` is simpler, closer to real usage, and exercises the full call path including the `detectMessage` delegate interaction.

### MSTest or NUnit instead of xUnit

- **ALT-005**: **Description**: Use MSTest (built into Visual Studio) or NUnit as the test framework.
- **ALT-006**: **Rejection Reason**: xUnit is the most widely adopted .NET testing framework, has first-class support in `dotnet test`, and offers a clean data-driven model via `[Theory]`/`[InlineData]` without requiring test class attributes.

## Implementation Notes

- **IMP-001**: The `FakeTransceiver` is the cornerstone of Tier 2 tests. Its `ReceiveMessage` must drain the `RxBuffer` byte-by-byte, accumulating into a local buffer and invoking `detectMessage` after each byte to faithfully replicate how real transports accumulate data.
- **IMP-002**: Implement Tier 1 tests first (pure logic, zero dependencies), then Tier 2, then Tier 3 as new protocol commands are added.
- **IMP-003**: All test method names follow the pattern `MethodName_Scenario_ExpectedBehavior`.
- **IMP-004**: Integration tests are tagged `[Trait("Category", "Integration")]` and excluded from the default CI run via `dotnet test --filter Category!=Integration`. To run integration tests locally, a virtual COM port pair is required: use `com0com` on Windows or `socat` on Linux/macOS. BLE integration tests remain manual-only and are documented in `Test/CallAndResponse.Test.Sandbox`.
- **IMP-005**: The test project targets `.NET 8` to match the sandbox project and take advantage of the latest language features in test code, while the production libraries remain on .NET Standard.
- **IMP-006**: Builder API correctness is an explicit Tier 1 concern. `TransceiverBuilder` fluent sequencing, `SerialTransceiverBuilderStage` option validation, and `ITransceiverFactory` delegation are pure logic and must be covered by unit tests that do not require hardware or mocking. See `Test/CallAndResponse.Test.Unit/SerialTransceiverBuilderStageTests.cs` and `TypedLoggerSupportTests.cs` as canonical examples.

## References

- **REF-001**: `Source/CallAndResponse/Transceiver.cs` — abstract base class with all default implementations.
- **REF-002**: `Source/CallAndResponse/ITransceiver.cs` — primary seam for mocking and faking.
- **REF-003**: `Source/CallAndResponse.Protocol.Modbus/ModbusRtuRequestBuilder.cs` — Tier 1 target.
- **REF-004**: `Source/CallAndResponse.Protocol.Modbus/ModbusUtils.cs` — Tier 1 target.
- **REF-005**: [xUnit documentation](https://xunit.net/)
- **REF-006**: [NSubstitute documentation](https://nsubstitute.github.io/)
- **REF-007**: [FluentAssertions documentation](https://fluentassertions.com/)
