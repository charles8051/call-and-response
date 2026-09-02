---
title: "ADR-0019: Ship Serial Transports for Both System.IO.Ports and RJCP.SerialPortStream"
status: "Accepted"
date: "2026-09-02"
authors: "Repository maintainer"
tags: ["architecture", "decision", "serial", "transport", "packaging"]
supersedes: ""
superseded_by: ""
---

# ADR-0019: Ship Serial Transports for Both System.IO.Ports and RJCP.SerialPortStream

## Status

**Accepted**

*Implementation status: not implemented. No `.Bcl` or `.Rjcp` package exists yet; the serial transport
is still the single `CallAndResponse.Transport.Serial` package described in
[ADR-0015](adr-0015-duplex-pipe-transport-seam.md). This record fixes the design before the code is
written.*

## Context

- **CTX-001**: The library ships one serial transport, `SerialDuplexPipe`, backed by
  `RJCP.IO.Ports.SerialPortStream`. RJCP is a third-party dependency and needs a native `libnserial`
  build on Linux. Some consumers want a serial transport that adds nothing beyond a Microsoft-shipped
  package.

- **CTX-002**: A second package costs almost nothing structurally. Per
  [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) DEC-007 the transport seam is
  `System.IO.Pipelines.IDuplexPipe`, and `CallAndResponse.Transport.Serial` already references no
  project in this repository — only `RJCP.SerialPortStream` and `System.IO.Pipelines`. The package
  boundary is exactly the dependency boundary.

- **CTX-003**: [ADR-0003](adr-0003-serial-transport-revision.md) ALT-007 rejected a second public
  serial type on the grounds that it would "fragment the API and shift backend-selection complexity to
  consumers". That reasoning was about `SerialPortTransceiver` under the builder API of ADR-0004, where
  a transport type carried framing, lifecycle, and construction. Under `IDuplexPipe` a transport type is
  a constructor and two properties. There is no API left to fragment, and backend selection is a
  `PackageReference` rather than a call-site decision. ADR-0003 is superseded and this record does not
  revive it; it records that the specific objection no longer applies.

- **CTX-004**: The two backends cannot share a read pump. On Windows,
  `SerialPort.BaseStream.ReadAsync` honours neither cancellation nor timeout:

  - It ignores the `CancellationToken`. This is [dotnet/runtime#30850][ref-30850], open since 2019 with
    milestone *Future*; the Unix implementation honours the token and the Windows one does not.
  - `ReadTimeout` is documented as not affecting `BeginRead` on the `BaseStream`, and `ReadAsync` is
    implemented over `BeginRead`, so the carve-out extends to it.

  A read issued this way returns only when bytes arrive or the port closes. RJCP's `ReadAsync` waits on
  its own in-memory buffer and honours the token, which is why the existing pump can pass the token
  straight through and await the pump task on disposal.

- **CTX-005**: The synchronous path does respect the timeout. Setting `ReadTimeout` calls
  `SetCommTimeouts` with `ReadIntervalTimeout` and `ReadTotalTimeoutMultiplier` at `MAXDWORD` and
  `ReadTotalTimeoutConstant` at the requested value — the Win32 idiom for "return whatever is buffered,
  and if nothing is buffered wait up to N milliseconds". The timeout is enforced by the driver.

- **CTX-006**: The event-driven and polled alternatives are both unsound. `DataReceived` is documented
  as not guaranteed to be raised for every byte and as deliverable out of order, with field reports of
  dropped bytes at 115200 ([dotnet/runtime#106631][ref-106631]). `BytesToRead` queries
  `ClearCommError`, which clears the error flags as a side effect, and races the read that follows it.

- **CTX-007**: Timer resolution rules out a polled pump independently. `Task.Delay`, `Thread.Sleep`,
  `SemaphoreSlim.WaitAsync(timeout)`, and `System.Threading.Timer` resolve at roughly 15.6ms on Windows,
  and since Windows 10 version 2004 `timeBeginPeriod` is scoped per process, so a raised tick cannot be
  borrowed from another application. `COMMTIMEOUTS` is driver-enforced at millisecond granularity, and
  `CancellationToken` cancellation runs a synchronous callback with no timer involved. Only a polled
  design pays the tick.

- **CTX-008**: [PR #17][ref-pr17] moved the existing pump most of the way to a shared shape before this
  record was written. `RunPumpAsync` now takes a `Stream` rather than a `SerialPortStream`, an internal
  `SerialDuplexPipe(Stream)` constructor exists as a test seam, and the transport project grants
  `InternalsVisibleTo` to `CallAndResponse.Test.Unit`. The backend-specific surface is already down to
  the public constructor and the read call.

- **CTX-009**: The same PR established a failure contract the second backend has to honour. A pump that
  stops because the port died captures the exception and passes it to `writer.Complete(failure)`, so the
  consumer sees the real cause instead of an end of stream indistinguishable from a clean close. Only
  cancellation carrying the pump's own token — matched as `ex.CancellationToken == token`, not as
  `token.IsCancellationRequested` — counts as a deliberate shutdown and completes cleanly.

## Decision

- **DEC-001**: Ship two serial transport packages.
  `CallAndResponse.Transport.Serial.Rjcp` provides `RjcpSerialDuplexPipe` over
  `RJCP.IO.Ports.SerialPortStream`. `CallAndResponse.Transport.Serial.Bcl` provides
  `BclSerialDuplexPipe` over `System.IO.Ports.SerialPort`. Both use the namespace
  `CallAndResponse.Transport.Serial` with distinct type names, so a consumer may reference both.

- **DEC-002**: `CallAndResponse.Transport.Serial` continues to exist as a package depending on
  `.Rjcp`, carrying `[assembly: TypeForwardedTo(typeof(RjcpSerialDuplexPipe))]`. Existing
  `SerialDuplexPipe` code keeps compiling and keeps its current behaviour. Neither backend is renamed
  out from under a consumer.

- **DEC-003**: The BCL read pump is synchronous `BaseStream.Read` on a dedicated long-running thread.
  It does not use `ReadAsync`, `DataReceived`, `BytesToRead`, or a delay-based poll, for the reasons in
  CTX-004, CTX-006, and CTX-007.

- **DEC-004**: The BCL pump sets `ReadTimeout` explicitly and treats the timeout as its loop tick,
  checking the cancellation token at the top of each iteration. It catches both `TimeoutException` and
  `IOException` with HResult `0x800705B4`; .NET 7 changed which one a timed-out read throws
  ([dotnet/runtime#80079][ref-80079]). The default tick is 250ms. Nothing awaits the pump, so a longer
  tick costs no latency — `Read` still returns on the first byte available — and a shorter one only
  multiplies caught exceptions.

- **DEC-004a**: A timed-out read is a loop tick, not a port failure. The BCL pump must catch the two
  timeout forms *before* the general handler that CTX-009 requires, or every tick on an idle port would
  be completed onto the pipe as a transport failure. This is the sharpest hazard in porting the existing
  pump: the RJCP pump has no benign exception in its read path, and the BCL pump has one arriving four
  times a second.

- **DEC-004b**: The BCL pump keeps the CTX-009 failure contract with a different shutdown path. Its read
  never throws `OperationCanceledException`, because the token is checked at the loop guard rather than
  passed into the read, so a deliberate shutdown exits the loop and completes with `null`. Every
  non-timeout exception is captured and handed to `writer.Complete(failure)`. The
  `ex.CancellationToken == token` filter is specific to the RJCP pump and has no counterpart here.

- **DEC-005**: The BCL pump never runs with `SerialPort.InfiniteTimeout`, which is the framework
  default and must therefore be overridden. Under an infinite timeout a blocked `Read` never observes
  cancellation, so a disposed pipe leaves a thread parked on the port handle. If the caller then builds
  a second pipe over the same open port, the abandoned pump wakes and consumes bytes belonging to the
  new one, corrupting framing intermittently. A finite tick makes the pump's lifetime a function of its
  own token rather than of when the caller closes the port.

- **DEC-006**: `BclSerialDuplexPipe.DisposeAsync` cancels the token and then joins the pump with a
  bounded wait, swallowing the timeout rather than blocking on an in-flight read. Consumer-visible
  cancellation is token-speed because consumers await the `Pipe`, not the read.
  `RjcpSerialDuplexPipe.DisposeAsync` keeps awaiting its pump unconditionally, which is safe because
  RJCP honours the token.

- **DEC-007**: Closing or reopening the port to unblock a read is not used, extending
  [ADR-0003](adr-0003-serial-transport-revision.md) IMP-003. Neither is P/Invoking `CancelIoEx` on a
  handle obtained by reflection over `SerialStream`'s private fields.

- **DEC-008**: The caller continues to own the port and its lifecycle. Disposing either pipe stops its
  pump and does not close, dispose, or reconfigure the port, other than the `ReadTimeout` the BCL pipe
  sets for its own use.

- **DEC-009**: The shared body — the internal `Pipe`, `PipeWriter.Create(stream)` for `Output`, the
  copy/advance/flush loop, and the `writer.Complete(failure)` contract — lives in one file linked into
  both projects. Per CTX-008 the current pump already operates on `Stream`, so this is extraction rather
  than rewriting. Only the read call, the benign-exception filter, and the dispose join differ.

## Consequences

### Positive

- **POS-001**: A serial transport becomes available with no third-party or native dependency, which
  matters most on Linux where RJCP needs `libnserial`.

- **POS-002**: Existing consumers are unaffected. The type-forward means the package they already
  reference resolves to the same implementation.

- **POS-003**: The backends stay honestly separate rather than hidden behind a runtime switch. A
  consumer chooses by package reference and can read exactly one pump.

- **POS-004**: The constraints that shape the BCL pump are written down here rather than rediscovered.
  Each of `ReadAsync`, `DataReceived`, `BytesToRead`, and a polled delay looks reasonable and is wrong
  for a specific documented reason.

### Negative

- **NEG-001**: Two serial pumps to maintain, with different cancellation semantics and different
  disposal guarantees. DEC-009 limits but does not remove the duplication.

- **NEG-002**: The BCL pipe consumes a dedicated OS thread that is blocked rather than idle, and
  produces roughly four caught exceptions a second per open port while no traffic arrives.

- **NEG-003**: `BclSerialDuplexPipe.DisposeAsync` cannot promise the pump has stopped, only that it
  will stop within one tick. That is a weaker contract than the RJCP pipe offers and must be documented
  on the type.

- **NEG-004**: The BCL backend inherits the reliability reputation of `System.IO.Ports`, including the
  open dropped-byte reports in CTX-006. Offering it invites bug reports this repository cannot fix.

- **NEG-005**: Three packages where there was one, and a fourth name (`SerialDuplexPipe`) that survives
  only as a forward. The package list gets harder to explain.

## Alternatives Considered

- **ALT-001 — One package referencing both backends**: **Rejection Reason**: every consumer would carry
  both dependencies, including RJCP's native Linux component, to use one of them. The dependency is the
  entire reason the second backend exists.

- **ALT-002 — Rename the existing package to `.Rjcp` and give `.Bcl` the plain `Serial` name**:
  **Rejection Reason**: `CallAndResponse.Transport.Serial` means RJCP today. Repointing it at a
  different backend is a silent behavioural swap for anyone who upgrades without reading release notes.

- **ALT-003 — `DataReceived` signalling a semaphore, draining via `BytesToRead`**: **Rejection Reason**:
  builds the pump on the two members with the worst reliability record in the API (CTX-006). A backstop
  timeout makes a missed event a latency problem rather than a hang, but `BytesToRead`'s side effect on
  the error flags and its race with the following read remain.

- **ALT-004 — `BaseStream.ReadAsync` with a short `ReadTimeout`**: **Rejection Reason**: `ReadTimeout`
  does not apply to `BeginRead`, and `ReadAsync` is built on it. The timeout would silently have no
  effect, producing a pump that appears cancellable and is not.

- **ALT-005 — Poll `BytesToRead` on a short delay**: **Rejection Reason**: CTX-006 and CTX-007. The
  poll interval cannot go below the ~15.6ms timer tick, so it adds arrival latency to every message
  while still not cancelling faster than a driver timeout would.

- **ALT-006 — `CancelIoEx` on the handle, reached by reflection**: **Rejection Reason**: it genuinely
  cancels the read, and it depends on a private field name across runtime versions. An aborted
  overlapped read can also report a partial transfer, which is byte loss in a framing library.

- **ALT-007 — Close the port to unblock the pending read**: **Rejection Reason**: the port belongs to
  the caller, and this is the close/reopen pattern ADR-0003 IMP-003 identified as the highest-risk
  behaviour in the original receive loop.

- **ALT-008 — Leave `ReadTimeout` at `InfiniteTimeout` and simply never await the pump**:
  **Rejection Reason**: DEC-005. Disposal would leak a thread still holding a read on the port handle,
  which steals bytes from any later pipe built over the same port.

## Implementation Notes

- **IMP-001**: Confirm by measurement before shipping that synchronous `BaseStream.Read` honours
  `ReadTimeout` as CTX-005 describes. A loopback test timing `Read` on an idle port settles it. The
  claim currently rests on documentation and reference source, not on a run.

- **IMP-002**: The cancellation gap is Windows-specific; the Unix `SerialStream` honours the token
  (CTX-004). Measure on Linux before deciding whether the synchronous pump is needed there or whether
  the BCL pipe should take the async path per platform. Prefer one pump for both until the measurement
  justifies two.

- **IMP-003**: Expose the tick as a constructor option with a 250ms default rather than hardcoding it.
  Reject `SerialPort.InfiniteTimeout` at construction with an explanatory message; per DEC-005 it is not
  a valid configuration for this pipe.

- **IMP-004**: The BCL pipe mutates `ReadTimeout` on a port the caller owns. Document that on the type,
  and consider restoring the previous value on disposal.

- **IMP-005**: `FakeSerialStream` and the internal `Stream` constructor from CTX-008 already give the
  BCL pump hardware-free unit coverage. It needs a synchronous counterpart, since the BCL pump calls
  `Read` rather than `ReadAsync` and `FakeSerialStream.Read` currently throws `NotSupportedException`.
  Cover DEC-004a explicitly: a fake whose `Read` throws `TimeoutException`, and one whose `Read` throws
  `IOException` with HResult `0x800705B4`, must both leave the pipe open rather than faulting it.

- **IMP-005a**: Add Tier 4 loopback coverage per [ADR-0001](adr-0001-testing-strategy.md) for both
  backends over the same test body, including the DEC-005 scenario: dispose a pipe, build a second over
  the same open port, and assert no bytes are lost to the first pump.

- **IMP-006**: Update `docs/ARCHITECTURE.md` — the package map, the transport section, and the layer
  diagram — when the packages exist, not before. Add the new projects to `CallAndResponse.slnx` and to
  the publish workflow's packable set.

## References

- **REF-001**: [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) — the `IDuplexPipe` seam and the
  rule for when a transport earns a package
- **REF-002**: [ADR-0003](adr-0003-serial-transport-revision.md) — ALT-007 and IMP-003
- **REF-003**: `Source/CallAndResponse.Transport.Serial/SerialDuplexPipe.cs` — the existing RJCP pump
- **REF-003a**: [PR #17][ref-pr17] — the `Stream` seam and the `writer.Complete(failure)` contract in
  CTX-008 and CTX-009
- **REF-004**: [SerialPort.ReadTimeout][ref-readtimeout] — "This property does not affect the
  `BeginRead` method of the stream returned by the `BaseStream` property."
- **REF-005**: [SerialPort.DataReceived][ref-datareceived] — not guaranteed to be raised for every byte
- **REF-006**: [dotnet/runtime#30850][ref-30850] — SerialStream does not support cancellation on Windows
- **REF-007**: [dotnet/runtime#80079][ref-80079] — .NET 7 timeout exception change
- **REF-008**: [dotnet/runtime#106631][ref-106631] — dropped bytes on receive
- **REF-009**: [If you *must* use .NET System.IO.Ports.SerialPort][ref-sparx] — `BytesToRead` and
  `DataReceived` critique
- **REF-010**: [Windows Timer Resolution: The Great Rule Change][ref-timer] — per-process
  `timeBeginPeriod` since Windows 10 version 2004

[ref-pr17]: https://github.com/charles8051/call-and-response/pull/17
[ref-readtimeout]: https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.serialport.readtimeout
[ref-datareceived]: https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.serialport.datareceived
[ref-30850]: https://github.com/dotnet/runtime/issues/30850
[ref-80079]: https://github.com/dotnet/runtime/issues/80079
[ref-106631]: https://github.com/dotnet/runtime/issues/106631
[ref-sparx]: https://sparxeng.com/blog/software/must-use-net-system-io-ports-serialport
[ref-timer]: https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/
