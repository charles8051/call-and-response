---
title: "ADR-0003: Revise Serial Transport Implementation for Receive Reliability"
status: "Superseded"
date: "2026-03-22"
authors: "Development Team"
tags: ["architecture", "decision", "serial", "transport"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0003: Revise Serial Transport Implementation for Receive Reliability

> **SUPERSEDED.** This record describes `SerialPortTransceiver` and `SerialByteSource`, neither of which exists.
> The serial transport is now `SerialDuplexPipe`, an `IDuplexPipe` over `RJCP.IO.Ports.SerialPortStream`.
> The receive-reliability problem this ADR set out to solve is solved; the mechanism described here is not
> the mechanism that solved it. See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

*Implementation status: Stage 1 complete. `SerialByteSource` was introduced as part of ADR-0007 Phase 4. It reads directly from `SerialPort.BaseStream` without polling `BytesToRead` and without the `CancellationTokenSource(10)` close/reopen pattern, satisfying the Stage 1 exit criteria. Stage 2 (RJCP.SerialPortStream evaluation) is not required.*

## Context

The current serial transport is implemented in `SerialPortTransceiver` using `System.IO.Ports.SerialPort` and `BaseStream.ReadAsync`.

Observed problem:
- In some scenarios, the serial implementation appears to drop the first one or two bytes of a received message.
- This is particularly damaging in a framed protocol library because losing the leading bytes corrupts unit identifiers, function codes, length fields, and CRC alignment.
- The issue is intermittent, which makes it difficult to diagnose and dangerous to ignore.

The current implementation has several characteristics that may contribute to unreliable behavior:
- It polls `BytesToRead` in a tight loop before attempting a read.
- It wraps each read in a `CancellationTokenSource(10)` and registers a callback that closes and reopens the serial port.
- It catches and logs all read exceptions, then continues.
- It depends directly on `System.IO.Ports.SerialPort`, making it difficult to compare alternate serial backends without changing production code.

These traits suggest two risks:
1. the current implementation may contain logic bugs independent of the underlying serial library, and
2. `System.IO.Ports` may itself be a poor fit for the reliability requirements of this library on some platforms or drivers.

There is an established alternative in the .NET ecosystem: `RJCP.SerialPortStream`, which is widely used as a more robust serial-port backend than `System.IO.Ports` in scenarios involving async I/O, driver variability, and buffering edge cases.

Key constraints:
- The transport libraries target .NET Standard 2.0 / 2.1.
- Existing consumers should not need to change their calling code to benefit from the fix.
- Serial behavior must remain transport-agnostic from the perspective of protocol clients.
- The fix should be evidence-driven; switching libraries without isolating the current logic could hide defects in the transport code itself.
- ADR-0001 already establishes that integration tests for hardware-dependent transports should be hardware-optional and excluded from CI by default.

## Decision

Revise the serial transport in two stages:

**Stage 1 — Investigate and harden the existing implementation** *(complete)*
- This stage was bounded: if the dropped-leading-bytes defect could be confirmed reproducible via a loopback test and eliminated by patching the receive loop, Stage 1 would be complete and Stage 2 would not be required.
- Exit criteria for Stage 1: zero leading-byte drops observed across at least 100 consecutive loopback frames of varying lengths using the patched implementation.
- The Stage 1 remediation was delivered via the `IByteSource` refactor in ADR-0007 Phase 4. A new `SerialByteSource` class wraps `System.IO.Ports.SerialPort` and reads directly from `BaseStream.ReadAsync` without polling `BytesToRead`. The `CancellationTokenSource(10)` close/reopen pattern was eliminated entirely rather than made configurable, because the `IByteSource` accumulation loop in `Transceiver` handles its own cancellation correctly.
- The existing public transport surface is preserved; `SerialPortTransceiver` remains the public type and delegates I/O to the internal `SerialByteSource`.

**Stage 2 — Introduce a backend seam and switch to `RJCP.SerialPortStream` if Stage 1 is insufficient** *(not required)*
- Stage 1 resolved the defect; Stage 2 was not needed.

## Consequences

### Positive

- **POS-001**: The decision focuses first on root-cause isolation instead of assuming the serial library alone is at fault.
- **POS-002**: Introducing a backend seam reduces coupling and makes future transport reliability work easier.
- **POS-003**: Existing consumers can keep using `SerialPortTransceiver` without public API churn.
- **POS-004**: If `RJCP.SerialPortStream` proves necessary, migration cost will be reduced by the Stage 1 refactor.
- **POS-005**: Serial receive reliability will improve confidence across all protocol stacks that depend on accurate leading bytes.

### Negative

- **NEG-001**: The work is larger than a one-line bug fix because it includes investigation, refactoring, and possibly a backend swap.
- **NEG-002**: Adding an internal abstraction introduces another maintenance surface in the serial transport package.
- **NEG-003**: Supporting or evaluating two backends may temporarily increase complexity during the transition period.
- **NEG-004**: Hardware and driver differences may make the defect difficult to reproduce consistently.
- **NEG-005**: If the issue is timing-sensitive, test coverage may need a mix of fake-transport tests and opt-in integration tests to provide confidence.

## Alternatives Considered

### Keep the current `System.IO.Ports` implementation unchanged

- **ALT-001**: **Description**: Accept the current implementation and treat the dropped first bytes as an environmental or driver-specific issue.
- **ALT-002**: **Rejection Reason**: The defect affects frame integrity and is too severe to leave unaddressed in a communication library.

### Switch immediately to `RJCP.SerialPortStream`

- **ALT-003**: **Description**: Replace `System.IO.Ports` immediately and assume the third-party backend resolves the problem.
- **ALT-004**: **Rejection Reason**: This may mask logic flaws in the existing receive loop and adds a dependency before the true cause is understood.

### Patch the current receive loop only

- **ALT-005**: **Description**: Modify `SerialPortTransceiver` in place without adding a backend abstraction.
- **ALT-006**: **Rejection Reason**: If `System.IO.Ports` remains unreliable after the patch, a later migration to `RJCP.SerialPortStream` will be more invasive and riskier.

### Expose a new public serial transceiver type for `RJCP.SerialPortStream`

- **ALT-007**: **Description**: Keep the current transceiver and add a second public serial transport implementation using `RJCP.SerialPortStream`.
- **ALT-008**: **Rejection Reason**: Duplicating public transport types would fragment the API and shift backend-selection complexity to consumers.

## Implementation Notes

- **IMP-001**: Begin by capturing a reproducible failing scenario, ideally with a loopback or virtual null-modem setup that can detect loss of the first one or two bytes.
- **IMP-002**: Add Tier 4 integration coverage per ADR-0001 for serial loopback behavior, especially short messages and incremental arrival.
- **IMP-003**: Review and likely remove the current close/reopen callback used during reads, as it is a high-risk behavior for losing buffered bytes.
- **IMP-004**: Prefer one continuous receive buffer per message rather than repeated read/reset cycles that can disturb driver state.
- **IMP-005**: Isolate serial operations behind an internal adapter interface **only if Stage 2 is required**. If Stage 1 successfully resolves the defect, the adapter adds YAGNI overhead and should not be introduced.
- **IMP-006**: Evaluate `RJCP.SerialPortStream` compatibility with both `netstandard2.0` and `netstandard2.1` before finalizing a migration.
- **IMP-007**: Preserve the existing `Transceiver` contract and `SerialPortTransceiver` public type unless a stronger compatibility reason emerges.
- **IMP-008**: Record the outcome of the investigation in a follow-up ADR update or implementation note once the backend choice is proven.
- **IMP-009**: The close/reopen `CancellationTokenSource(10)` pattern in the current receive loop is the primary high-risk behavior and the first target for removal. Any receive timeout value should be exposed via `SerialTransceiverOptions` to enable per-consumer tuning rather than being hardcoded.
- **IMP-010**: Cancellation behavior in the serial receive loop should follow standard `OperationCanceledException` patterns consistent with the rest of the transport stack. See ADR-0005 IMP-002 for the repository-wide cancellation contract.

## References

- **REF-001**: `docs/adr/adr-0001-testing-strategy.md`
- **REF-002**: `Source/CallAndResponse.Transport.Serial/SerialPortTransceiver.cs`
- **REF-003**: `Source/CallAndResponse.Transport.Serial/CallAndResponse.Transport.Serial.csproj`
- **REF-004**: `Source/CallAndResponse/Transceiver.cs`
- **REF-005**: `System.IO.Ports.SerialPort`
- **REF-006**: `RJCP.SerialPortStream`
