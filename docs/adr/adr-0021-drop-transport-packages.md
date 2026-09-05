---
title: "ADR-0021: Drop the Serial and BLE Transport Packages"
status: "Proposed"
date: "2026-09-04"
authors: "Repository maintainer"
tags: ["architecture", "decision", "transport", "serial", "ble", "packaging", "scope"]
supersedes: ""
superseded_by: ""
---

# ADR-0021: Drop the Serial and BLE Transport Packages

## Status

**Proposed**

*Implementation status: not implemented. `CallAndResponse.Transport.Serial` and
`CallAndResponse.Transport.BleNordicUart` both still exist, and
[ADR-0019](adr-0019-dual-serial-transport-backends.md) is still listed as Accepted.*

## Context

- **CTX-001**: The library ships two transport projects.
  `CallAndResponse.Transport.Serial` is one of the four packable projects and is published to
  nuget.org on a `v*` tag. `CallAndResponse.Transport.BleNordicUart` sets `IsPackable=false` and has
  never been published; the README says to reference the project or copy the file.

- **CTX-002**: [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) DEC-007 set the bar for when a
  transport earns a package: when the adaptation is non-trivial — a background pump, a framing quirk,
  a vendor SDK that is not stream-shaped. `PipeReader.Create(stream)` needs no package at all.

- **CTX-003**: `SerialDuplexPipe` clears that bar. It is 132 lines: a background read pump over
  `RJCP.IO.Ports.SerialPortStream`, and the failure contract from #17 that distinguishes a clean stop
  from a dead port by passing the captured exception to `writer.Complete(failure)`, so a consumer's
  next read throws the real cause instead of reporting a truncated frame. It has 177 lines of test
  around it (`SerialDuplexPipeTests`, `FakeSerialStream`).

- **CTX-004**: `BleNordicUartPipe` does not clear it, and does not clear it by a wide margin. It is
  40 lines that construct two `Pipe`s and expose their four ends. Its only `PackageReference` is
  `System.IO.Pipelines`. There is no vendor SDK, no pump, and no adaptation — the caller still owns
  the BLE connection, the notification subscription, and the TX drain loop. `docs/ARCHITECTURE.md`'s
  package map lists its dependencies as "Core, Plugin.BLE", which is wrong: the project references
  neither.

- **CTX-005**: Periphery has ported both pipes. `Periphery.Serial` (over
  `System.IO.Ports.SerialPort`, with a shared `SerialReadPump`) and `Periphery.Serial.Rjcp` (over
  `SerialPortStream`) landed in [#168][ref-168], whose commit subject is "port the BCL/RJCP duplex
  pipes from call-and-response". Both are in `Periphery.slnx`, both are packable, and both have test
  projects.

- **CTX-006**: Periphery states its reason in `Periphery.Bootloader.Stm32.Serial.csproj`: it builds
  its port and pipe over `Periphery.Serial` "rather than CallAndResponse.Transport.Serial, which was
  the only RJCP-pulling package this flasher touched". The performance claim behind that is recorded
  in Periphery ADR-0062's amendment, dated 2026-09-02: "the backend choice costs 3x on a
  request-response protocol", and for the flashing workloads that repository serves, RJCP "is the
  wrong default by 3x".

- **CTX-006a**: What this record has and has not checked about that number. It has confirmed the
  claim is written down in a checked-in decision record with a date, rather than only in a csproj
  comment. It has **not** reproduced the benchmark, does not know the workload's exact shape, and
  cannot say whether the gap holds for framings other than AN3155. The number is treated here as
  Periphery's recorded finding about its own workload, which is enough to explain why that consumer
  left. It is not treated as a general statement about the two backends, and DEC-005 does not repeat
  it to users.

- **CTX-007**: That measurement is the one [ADR-0019](adr-0019-dual-serial-transport-backends.md)
  asked for and never got. ADR-0019 IMP-001 said to confirm the synchronous-read behaviour by
  measurement before shipping, and noted the claim rested on documentation and reference source
  rather than a run. ADR-0019 remains Accepted and unimplemented, and its entire content — two
  backends, a shared pump, selection by package reference — is what Periphery has now built and
  measured.

- **CTX-008**: The seam held, which is the part worth noticing. Periphery references
  `CallAndResponse` and `CallAndResponse.Protocol.Stm32Bootloader` at `2.0.0-alpha.6` and supplies
  its own transport. A consumer taking the core and the protocol while bringing its own
  `IDuplexPipe` is exactly the arrangement ADR-0015 was designed to allow.

- **CTX-009**: The reason for the fork is lifecycle. Periphery needs to own opening, closing, and
  discovering ports; [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) and
  [ADR-0009](adr-0009-device-discovery-out-of-scope.md) put both outside this library on purpose. A
  transport package is where that boundary rubs, because a type wrapping an already-open port is one
  short step from wanting to open it. The pressure recurs for every consumer that needs more than
  this library will give, and BLE is expected to go the same way.

- **CTX-010**: `v2.0.0` has not shipped stable. The published versions are `v2.0.0-alpha.5`, `.6`,
  and `.7`. Dropping a package from future releases costs a stable consumer nothing, because there
  is no stable consumer to cost.

- **CTX-010a**: The replacement is not installable today. `Periphery.Serial` and
  `Periphery.Serial.Rjcp` are packable and in `Periphery.slnx`, but neither is on nuget.org:
  Periphery's most recent tag, `v4.1.0-alpha.2`, is dated 2026-08-30 and the port landed in
  `32cfe6b` on 2026-09-03, so no release has packed them yet. Periphery's publish workflow packs
  every packable `src/` project, so its next `v*` tag ships them. Until then, the only way to consume
  them is a project reference against a Periphery checkout: `charles8051/periphery`,
  `src/Periphery.Serial/Periphery.Serial.csproj` and
  `src/Periphery.Serial.Rjcp/Periphery.Serial.Rjcp.csproj`. `Periphery.Serial.Rjcp` is the drop-in
  for `SerialDuplexPipe`, being the same `SerialPortStream`-backed pump; `Periphery.Serial` is the
  `System.IO.Ports` backend and the shared pump the wrapper depends on.

- **CTX-011**: `Examples/Example.Transport.Serial` and `Examples/Example.Transport.Ble` are the only
  runnable on-ramp in the repository, and both exist to demonstrate a transport. The README's
  quick-start opens a `SerialPortStream` and wraps it in `SerialDuplexPipe`.

## Decision

- **DEC-001**: Delete `CallAndResponse.Transport.Serial`, its tests, and
  `Examples/Example.Transport.Serial`. Remove it from `CallAndResponse.slnx` and from the packable
  set that `publish.yml` pushes to nuget.org.

- **DEC-002**: Delete `CallAndResponse.Transport.BleNordicUart` and
  `Examples/Example.Transport.Ble`. It has no package consumers, because it was never packed. That
  is narrower than having no consumers at all: CTX-001 notes the README tells people to reference the
  project or copy the file, and neither leaves a trace this repository can see. The cost is bounded
  by CTX-004 — the file is 40 lines that pair two `Pipe`s, so a copy-and-paste consumer already owns
  its copy and a project-reference consumer can take one from the git history.

- **DEC-003**: Do not unlist the published `CallAndResponse.Transport.Serial` versions. They keep
  working for anyone already referencing them. Retracting a version that a consumer references trades
  a tidiness problem for a real breakage, and the package is the only remaining record of what
  shipped. Stopping publication is the whole of the change.

- **DEC-004**: Withdraw ADR-0019 rather than superseding it. Its reasoning was not wrong and was not
  replaced by a better design here; it was answered elsewhere, by Periphery ADR-0062 and #168, with
  a measurement this repository never took. A withdrawn record with a banner pointing at where the
  work went is more useful than a superseded one pointing at a design this repository will not build.

- **DEC-005**: The README's quick-start points at `Periphery.Serial.Rjcp` for a serial pipe, and
  keeps `PipeReader.Create(stream)` as the no-package path for everything stream-shaped. The library
  documents the seam and names a transport that implements it, rather than shipping one.

- **DEC-005a**: The serial removal does not land until that pointer resolves. Per CTX-010a the
  replacement is currently source-only, and deleting a published package while its successor is
  unreleased leaves a consumer with neither. The removal is gated on **both** `Periphery.Serial` and
  `Periphery.Serial.Rjcp` being on nuget.org and resolvable, and the README must name a version for
  the one it points at. Both, because `Periphery.Serial.Rjcp` carries a project reference to
  `Periphery.Serial`, so it packs with a dependency on it: the wrapper alone restores to a missing
  dependency, which is a worse failure than the package simply not existing. The BLE removal is not
  gated, because DEC-002's package was never published and has no successor to wait for.

- **DEC-006**: `FakeSerialStream` goes with `SerialDuplexPipeTests`. It exists to fake a serial
  stream for the pump, and nothing else uses it.

- **DEC-007**: Correct the `Plugin.BLE` row in `docs/ARCHITECTURE.md`'s package map as part of the
  removal rather than leaving a wrong line to be deleted quietly. It has been wrong for long enough
  that someone may have believed it.

- **DEC-008**: This library ships framing and protocol logic and nothing else. Transports are named
  in the documentation and shipped by whoever owns the hardware lifecycle. That is the scope rule
  ADR-0011, ADR-0014, and ADR-0015 were converging on, stated outright.

## Consequences

### Positive

- **POS-001**: The library's stated thesis and its package list finally agree. "Pure framing and
  protocol logic, never owns lifecycle" is hard to hold while shipping the one kind of package that
  keeps being asked to own lifecycle.

- **POS-002**: No duplicated serial pump. The pump, its cancellation handling, and its failure
  contract exist once, in the repository that measured them and has both backends.

- **POS-003**: ADR-0019 stops being a standing commitment. It promised two serial pumps with
  different cancellation semantics and different disposal guarantees, and its own NEG-001 called that
  a maintenance cost. Withdrawing it removes work this repository was on the hook for.

- **POS-004**: The remaining packages have no third-party dependency at all. `RJCP.SerialPortStream`
  and its native `libnserial` requirement on Linux leave with the transport that pulled them.

- **POS-005**: One fewer package to version, publish, and explain. Three packable projects rather
  than four.

### Negative

- **NEG-001**: The library loses its runnable on-ramp. A "bring your own `IDuplexPipe`" library with
  no worked example is harder to adopt, and DEC-005's pointer at another package is weaker than an
  example that builds in this repository.

- **NEG-002**: Real work is discarded. `SerialDuplexPipe`'s failure contract came out of #17 and its
  tests were written deliberately. Periphery reimplemented rather than referenced it, so the
  knowledge survives, but it survives as a second implementation rather than a shared one.

- **NEG-003**: A published package stops receiving updates without being deprecated. Anyone
  referencing `CallAndResponse.Transport.Serial` sees no signal beyond it never changing again.
  DEC-003 accepts that as better than the alternative, but it is a real cost and the README should
  say where the successor lives.

- **NEG-004a**: The serial half of this record cannot be executed on its own schedule. DEC-005a
  gates it on another repository cutting a release, and that repository has no reason to hurry on
  this account. Until then the record is a decision the repository is holding rather than one it has
  acted on, which is a state ADRs are bad at representing.

- **NEG-004**: The decision rests on one consumer. Periphery is the only visible one, and a library
  scoped to its only known caller is a library that has stopped anticipating. The counter is that
  the caller did not ask for a change — it left, which is stronger evidence than a request.

- **NEG-005**: If BLE does not in fact move elsewhere, this deletes the only BLE support that exists
  rather than relocating it. CTX-004 makes that cheap to reverse — the file is 40 lines of
  `new Pipe()` — but cheap to reverse is not the same as free.

## Alternatives Considered

- **ALT-001 — Keep both, withdraw only ADR-0019**: **Rejection Reason**: it keeps the package whose
  only known consumer explicitly declined it, and keeps the scope ambiguity of CTX-009 without
  keeping any consumer. Withdrawing the plan to expand while retaining the thing nobody uses is the
  worst split of the two decisions.

- **ALT-002 — Keep Serial, drop only BLE**: **Rejection Reason**: defensible, and the strongest
  alternative. Serial clears ADR-0015's bar on its merits, and NEG-001 is real. It loses on CTX-006:
  the one consumer measured the backend this package cannot offer as three times faster, so keeping
  it means shipping the slower of two implementations of a component that already exists elsewhere.
  **The condition that flips this record to ALT-002**: a consumer of the RJCP backend that is not
  Periphery, or an intent to make this library's examples first-class again.

- **ALT-003 — Keep them and implement ADR-0019 here**: **Rejection Reason**: it is the same work
  Periphery has already done and measured, in a repository with no consumer for it, and ADR-0019's
  own NEG-001 through NEG-006 priced it honestly at two pumps, a benign-exception predicate that is
  a standing correctness surface, and a weaker disposal guarantee on one backend.

- **ALT-004 — Move the projects into Periphery rather than deleting them**: **Rejection Reason**:
  #168 already did the moving, by porting rather than transplanting, and the ported version knows
  things this one does not — the `StreamPipeReader` cancellation behaviour that produces
  `OperationCanceledException` on every idle timeout, documented at length in
  `Periphery.Serial.Rjcp/SerialDuplexPipe.cs`. There is nothing left to move.

- **ALT-005 — Deprecate the published package on nuget.org**: **Rejection Reason**: deprecation
  warns every consumer at restore, including those who are content. DEC-003's quieter form — stop
  publishing, name the successor in the README — is enough for a package at `2.0.0-alpha`. Revisit
  if a stable `2.x` ever ships one.

## Implementation Notes

- **IMP-001**: Delete in one change, not two. A commit that removes the serial package while the
  README still tells people to install it is worse than either end state.

- **IMP-001a**: Before the serial removal lands, confirm **both** packages resolve, per DEC-005a:

  ```bash
  curl -s https://api.nuget.org/v3-flatcontainer/periphery.serial/index.json
  curl -s https://api.nuget.org/v3-flatcontainer/periphery.serial.rjcp/index.json
  ```

  As of 2026-09-04 both return `BlobNotFound`. Checking the index is necessary and not sufficient —
  finish with a real restore of `Periphery.Serial.Rjcp` in a scratch project, which is the only thing
  that proves the transitive `Periphery.Serial` dependency resolves at the version the wrapper packed
  against. The BLE removal can proceed independently.

- **IMP-002**: Touch points beyond the two project directories: `CallAndResponse.slnx`,
  `.github/workflows/publish.yml`'s packable set, `README.md` (quick-start, package table, repository
  layout), `docs/ARCHITECTURE.md` (layer diagram, package map, the whole Transport Implementations
  section), `CONTRIBUTING.md`, and `SECURITY.md`. Each names a transport today.

- **IMP-003**: Check `Test/CallAndResponse.Test.Unit/CallAndResponse.Test.Unit.csproj` for a project
  reference to the serial transport, and ADR-0001's tier description for a loopback tier that only
  exists because of it.

- **IMP-004**: Add the withdrawal banner to ADR-0019 and move it to the superseded-and-withdrawn
  table in `docs/adr/README.md`, pointing at this record and at Periphery ADR-0062. Per DEC-004 the
  banner should say the work moved rather than that the design was wrong. The index also carries a
  prose paragraph saying "ADR-0019 is accepted but not yet implemented", and the note this record
  added below it; both have to change in the same commit or the index contradicts its own table.

- **IMP-005**: Do not repeat the 3x figure in the README. Per CTX-006a it is Periphery's recorded
  finding about its own AN3155 workload, not a benchmark this repository ran or a general claim about
  the two backends. The README needs to name a replacement package, not argue a performance case.

- **IMP-006**: Periphery ADR-0062's own `status_note` says "there is no `Periphery.Serial` package",
  which predates #168 and is now stale. Worth telling that repository, since this record leans on
  those packages existing.

## References

- **REF-001**: [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) — DEC-007, when a transport earns
  a package
- **REF-002**: [ADR-0019](adr-0019-dual-serial-transport-backends.md) — the dual-backend plan this
  record withdraws, and IMP-001's unfulfilled request for a measurement
- **REF-003**: [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md) and
  [ADR-0009](adr-0009-device-discovery-out-of-scope.md) — the lifecycle and discovery boundary of
  CTX-009
- **REF-004**: [#168][ref-168] — "port the BCL/RJCP duplex pipes from call-and-response"
- **REF-005**: Periphery `docs/adr/0062-periphery-serial-backend-provider.md` — the backend-provider
  model, and the amendment carrying the 3x measurement
- **REF-006**: Periphery `src/Periphery.Bootloader.Stm32.Serial/Periphery.Bootloader.Stm32.Serial.csproj`
  — the stated reason for not referencing `CallAndResponse.Transport.Serial`
- **REF-007**: `Source/CallAndResponse.Transport.Serial/SerialDuplexPipe.cs` and
  `Source/CallAndResponse.Transport.BleNordicUart/BleNordicUartPipe.cs` — the code CTX-003 and
  CTX-004 weigh

[ref-168]: https://github.com/charles8051/periphery/pull/168
