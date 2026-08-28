---
title: "ADR-0002: Expand Modbus RTU Support with Additional Function Codes"
status: "Proposed"
date: "2026-03-22"
authors: "Development Team"
tags: ["architecture", "decision", "modbus", "protocol"]
supersedes: ""
superseded_by: ""
---

# ADR-0002: Expand Modbus RTU Support with Additional Function Codes

## Status

**Proposed**

## Context

The current Modbus implementation is intentionally narrow:

- `IModbusClient` exposes only `ReadHoldingRegisters` and `WriteRegisters`.
- `ModbusRtuClient` implements only FC03 (`ReadHoldingRegisters`) and FC16 (`WriteMultipleRegisters`).
- `ModbusFunctionCode` already defines a substantially larger set of Modbus function codes, including FC01, FC02, FC04, FC05, FC06, FC15, FC20, FC21, FC22, FC23, and FC24.
- `ModbusRtuRequestBuilder` currently builds only the request shapes needed by FC03 and FC16.

This leaves a mismatch between the protocol surface implied by the enum and the protocol surface actually supported by the client. It also limits the library's usefulness for common Modbus devices that expose:

- discrete inputs and coils,
- input registers,
- single-register writes,
- single-coil writes,
- multi-coil writes.

The project already has automated unit-test infrastructure from ADR-0001. That enables safe expansion of protocol logic without requiring hardware-dependent tests for every iteration.

Key constraints:
- The production libraries target .NET Standard 2.0 / 2.1, so any API expansion must remain compatible with those TFMs.
- `ITransceiver` is the transport seam; new Modbus functionality should remain transport-agnostic.
- Modbus response framing differs by function code. Bit-oriented functions (coils, discrete inputs) do not map cleanly to the same payload model used for 16-bit register reads.
- Backward compatibility matters: existing FC03 and FC16 callers should not require migration.

## Decision

Expand `ModbusRtuClient` incrementally, prioritising the high-value function codes used by typical PLCs, sensor modules, and embedded devices.

The implementation will be phased as follows:

**Phase 1 — Common read/write operations**
- FC04 `ReadInputRegisters`
- FC06 `WriteSingleRegister`
- FC01 `ReadCoils`
- FC02 `ReadDiscreteInputs`
- FC05 `WriteSingleCoil`
- FC15 `WriteMultipleCoils`

**Phase 2 — Advanced register operations**
- FC22 `MaskWriteRegister`
- FC23 `ReadWriteMultipleRegisters`

**Phase 3 — Specialist / low-frequency operations**
- FC07 `ReadExceptionStatus`
- FC20 `ReadFileRecord`
- FC21 `WriteFileRecord`
- FC24 `ReadFifoQueue`

API design principles:

1. Add strongly named methods to `IModbusClient` and `ModbusRtuClient` for each supported operation rather than exposing a single generic raw-function API as the primary surface.
2. Introduce shared internal helpers for request construction and response validation so new function codes reuse CRC, unit-id validation, exception handling, and byte-count checks.
3. Keep register-oriented methods returning register-aligned data and coil-oriented methods returning bit-oriented results.
4. Preserve the existing FC03 and FC16 APIs unchanged.
5. Gate each new function code behind logic-only unit tests before considering any integration scenarios.

Data-shape decisions:
- Register reads return `Memory<byte>` containing the raw payload bytes in **Modbus wire order (big-endian)**. The library does not swap byte pairs automatically; callers requiring host-endian 16-bit values should use the existing `ModbusUtils.Flip16BitValues` helper. This is consistent with the behavior of the existing FC03 and FC16 implementations.
- Coil and discrete-input reads return `bool[]`, because callers typically want semantic bit values rather than packed bytes.
- Single-write methods return `Task` unless the protocol requires returning additional typed data.
- Composite operations such as FC23 may introduce dedicated result/request models when a raw byte array would be ambiguous.

## Consequences

### Positive

- **POS-001**: The Modbus layer becomes broadly useful for common device classes without requiring consumers to build raw frames themselves.
- **POS-002**: The public client surface better matches the already-declared `ModbusFunctionCode` enum, reducing confusion.
- **POS-003**: Strongly typed methods improve discoverability and reduce protocol misuse compared to ad hoc frame construction in application code.
- **POS-004**: A phased rollout allows the most common operations to ship first while keeping implementation risk controlled.
- **POS-005**: Reusable internal helpers should reduce duplication across function-code implementations and make CRC / framing validation more consistent.

### Negative

- **NEG-001**: The Modbus client API surface will grow materially, increasing maintenance burden.
- **NEG-002**: Supporting both register-oriented and bit-oriented operations introduces multiple result shapes (`Memory<byte>`, `bool[]`, and possibly request/response models).
- **NEG-003**: Some advanced function codes (for example FC20/FC21/FC24) are niche and may add complexity disproportionate to near-term user demand.
- **NEG-004**: The current request builder may need refactoring or decomposition as more frame shapes are added.
- **NEG-005**: More protocol paths increase the importance of disciplined unit-test coverage for exception responses, byte counts, and malformed frames.

## Alternatives Considered

### Keep the client limited to FC03 and FC16

- **ALT-001**: **Description**: Preserve the current narrow API and leave all additional Modbus operations to consumer code.
- **ALT-002**: **Rejection Reason**: This undermines the value of the protocol library and forces callers to duplicate framing, validation, and error handling.

### Add one generic raw Modbus transaction method only

- **ALT-003**: **Description**: Expose a low-level method such as `SendRequest(functionCode, address, payload)` and let callers interpret responses.
- **ALT-004**: **Rejection Reason**: While flexible, it shifts protocol complexity to consumers and weakens the library's type safety and ergonomics.

### Implement every declared function code immediately

- **ALT-005**: **Description**: Expand the client to cover all currently enumerated function codes in a single release.
- **ALT-006**: **Rejection Reason**: The scope is too large, mixes common and niche operations, and would make testing, review, and rollout riskier.

### Add only read-oriented function codes

- **ALT-007**: **Description**: Implement FC01, FC02, and FC04, but leave write operations to raw or external implementations.
- **ALT-008**: **Rejection Reason**: Common Modbus use cases require both reads and writes; omitting write functions would leave the API incomplete for device control scenarios.

## Implementation Notes

- **IMP-001**: Start with Phase 1, specifically FC04 and FC06 first, because they are the closest analogues to the current FC03 and FC16 logic.
- **IMP-002**: Introduce internal helpers in `ModbusRtuClient` for validating echoed addresses, quantities, byte counts, and exception responses by function-code family.
- **IMP-003**: Refactor `ModbusRtuRequestBuilder` only as needed; if frame shapes diverge too far, split per-operation builders rather than overloading a single builder indefinitely.
- **IMP-004**: Add Tier 1 and Tier 3 tests from ADR-0001 for every new function code before implementation is considered complete.
- **IMP-005**: Add CRC validation in `ValidateResponse` while expanding function-code support, since broader protocol coverage increases the risk of accepting malformed frames.
- **IMP-006**: Prefer additive public API changes; do not rename or break the existing FC03 / FC16 methods.
- **IMP-007**: If coil packing/unpacking logic is introduced, centralize it in a dedicated helper rather than scattering bit math across client methods.
- **IMP-008**: All new function code implementations must handle the Modbus exception response pattern uniformly: if the response function code equals the request function code OR-ed with `0x80`, the implementation must extract the Modbus exception code and throw a typed exception consistent with the existing error-handling contract. This behavior should be validated with Tier 3 (mock-based) unit tests for every new function code.
- **IMP-009**: Phase 3 (FC07, FC20, FC21, FC24) is speculative and should not be committed to in this ADR. If real consumer demand for these function codes emerges, a follow-on ADR should evaluate scope, API shape, and testing requirements independently. Phase 3 entries are retained here for completeness but are explicitly deferred.

## References

- **REF-001**: `docs/adr/adr-0001-testing-strategy.md`
- **REF-002**: `Source/CallAndResponse.Protocol.Modbus/IModbusClient.cs`
- **REF-003**: `Source/CallAndResponse.Protocol.Modbus/ModbusRtuClient.cs`
- **REF-004**: `Source/CallAndResponse.Protocol.Modbus/ModbusFunctionCode.cs`
- **REF-005**: `Source/CallAndResponse.Protocol.Modbus/ModbusRtuRequestBuilder.cs`
- **REF-006**: Modbus Application Protocol Specification V1.1b3
