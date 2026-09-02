---
title: "ADR-0020: Package the Serial Backends Without a Type Forward"
status: "Accepted"
date: "2026-09-02"
authors: "Repository maintainer"
tags: ["architecture", "decision", "serial", "transport", "packaging"]
supersedes: "ADR-0019 (DEC-001 and DEC-002 only)"
superseded_by: ""
---

# ADR-0020: Package the Serial Backends Without a Type Forward

## Status

**Accepted**

*Implementation status: implemented. `CallAndResponse.Transport.Serial` ships `SerialDuplexPipe` over
RJCP, unchanged; `CallAndResponse.Transport.Serial.Bcl` ships `BclSerialDuplexPipe` over
`System.IO.Ports`.*

*Scope: this record replaces [ADR-0019](adr-0019-dual-serial-transport-backends.md) DEC-001 and DEC-002
and nothing else. Everything ADR-0019 says about why the two read pumps differ — the Windows
cancellation and timeout gaps, the rejected alternatives, the benign-exception predicate — stands, and
that record remains where the reasoning lives.*

## Context

- **CTX-001**: [ADR-0019](adr-0019-dual-serial-transport-backends.md) DEC-001 decided on two suffixed
  packages, `.Rjcp` and `.Bcl`, exposing `RjcpSerialDuplexPipe` and `BclSerialDuplexPipe`. DEC-002 kept
  `CallAndResponse.Transport.Serial` alive as a third package depending on `.Rjcp` and carrying
  `[assembly: TypeForwardedTo(typeof(RjcpSerialDuplexPipe))]`, so that existing code using
  `SerialDuplexPipe` would keep compiling and keep binding.

- **CTX-002**: That is not what `TypeForwardedTo` does. The attribute forwards a type *identity*: a
  reference to `CallAndResponse.Transport.Serial.SerialDuplexPipe` in the shim assembly resolves to a
  type of **that same namespace-qualified name** in the destination assembly. It cannot rename. There is
  no expression of the attribute that forwards `SerialDuplexPipe` to a type called
  `RjcpSerialDuplexPipe`, so DEC-001 and DEC-002 contradict each other: the rename in one defeats the
  compatibility promise of the other.

- **CTX-003**: The goals underneath those two decisions were separable and both still worth having.
  Existing consumers should not have to change source or rebind (DEC-002's purpose). Consumers who
  cannot take RJCP should have a serial transport (DEC-001's purpose). Only the mechanism was wrong.

- **CTX-004**: The `IDuplexPipe` seam of [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) makes the
  choice of backend a `PackageReference` rather than a call-site decision. Nothing in the library or in
  a protocol client names a transport type, so the cost of two differently-named transport types is
  borne entirely at the two lines where an application constructs one.

## Decision

- **DEC-001**: `CallAndResponse.Transport.Serial` remains the RJCP transport and keeps exposing
  `SerialDuplexPipe`. It is not renamed, not deprecated, and not turned into a shim. Existing consumers
  are untouched at source and at binary level, which was the whole point of ADR-0019 DEC-002.

- **DEC-002**: No `.Rjcp` package is created and no type forward ships. The three-package arrangement of
  ADR-0019 DEC-001 and DEC-002 is abandoned, not deferred.

- **DEC-003**: A new serial backend gets a suffixed package and a correspondingly prefixed type.
  `CallAndResponse.Transport.Serial.Bcl` exposes `BclSerialDuplexPipe`. Both may be referenced at once;
  the type names do not collide, and both keep the namespace `CallAndResponse.Transport.Serial`.

- **DEC-004**: The general rule, for serial and for any future transport with more than one backend: the
  incumbent keeps the unsuffixed package and the unadorned type name, and every backend added later
  takes a suffix. Renaming an incumbent to gain symmetry is not worth a breaking change, and — per
  CTX-002 — cannot be made non-breaking by a type forward anyway.

- **DEC-005**: The resulting naming asymmetry is accepted rather than mitigated. `SerialDuplexPipe`
  means RJCP and `BclSerialDuplexPipe` means `System.IO.Ports`. ADR-0019 ALT-002 had already accepted
  exactly this trade — that `CallAndResponse.Transport.Serial` means RJCP today and repointing it would
  be a silent behavioural swap — so this decision applies that reasoning to the type name as well as to
  the package name.

## Consequences

### Positive

- **POS-001**: One package instead of three, and no shim assembly to publish, version, or explain.
  ADR-0019 NEG-005 is retired rather than accepted.

- **POS-002**: Compatibility is achieved by not breaking anything, which is stronger than achieving it
  with a forward. There is no rebinding step, no assembly-identity subtlety, and nothing that behaves
  differently between a fresh build and an upgrade of an existing one.

- **POS-003**: DEC-004 gives the next backend an answer that does not require rediscovering CTX-002.

### Negative

- **NEG-001**: The two type names are asymmetric, so neither name tells a reader that the other exists.
  `docs/ARCHITECTURE.md` carries the pairing; the type names do not.

- **NEG-002**: `CallAndResponse.Transport.Serial` is a less accurate package name now that it is one of
  two serial transports. It was already inaccurate in the same way before ADR-0019, and correcting it
  costs more than it returns.

- **NEG-003**: ADR-0019 is left partly superseded rather than cleanly replaced, which is a slightly
  harder shape for a reader than the wholesale supersession in ADR-0015. The alternative was restating
  ADR-0019's cited pump reasoning here to justify replacing it entirely.

## Alternatives Considered

- **ALT-001 — Name the RJCP type `SerialDuplexPipe` inside a `.Rjcp` package and forward to it**:
  the one arrangement that satisfies `TypeForwardedTo`, since the name is preserved.
  **Rejection Reason**: it buys nothing. The shim exists so consumers keep resolving `SerialDuplexPipe`;
  if the type has to keep that name anyway, the extra package and the forward add two moving parts and
  change nothing a consumer can observe.

- **ALT-002 — Ship an `[Obsolete] SerialDuplexPipe` in `.Rjcp` that wraps `RjcpSerialDuplexPipe`**:
  **Rejection Reason**: not binary compatible — a wrapper is a different type, so existing compiled
  assemblies would still fail to bind. It also cannot derive from the sealed pipe, so it would have to
  re-implement `IDuplexPipe` and `IAsyncDisposable` by delegation, duplicating the disposal contract
  that ADR-0019 DEC-006 spent effort getting right.

- **ALT-003 — Deprecate `CallAndResponse.Transport.Serial` and require a source change**:
  **Rejection Reason**: a breaking change bought only naming symmetry. ADR-0015 NEG-001 accepted a hard
  break because the seam itself changed; nothing comparable is at stake here.

- **ALT-004 — One package selecting a backend at runtime**: **Rejection Reason**: already rejected as
  ADR-0019 ALT-001. Every consumer would carry both dependencies, including RJCP's native Linux
  component, to use one of them.

## Implementation Notes

- **IMP-001**: Add the banner to ADR-0019 rather than editing its DEC-001 and DEC-002 in place. The
  wrong decision and the reason it was wrong are the useful part of the record.

- **IMP-002**: `docs/ARCHITECTURE.md` is the only place the two backends are presented as a pair, per
  NEG-001. Keep the `SerialDuplexPipe` and `BclSerialDuplexPipe` sections adjacent and keep both naming
  the other.

- **IMP-003**: When a third backend appears, apply DEC-004 and do not revisit the asymmetry. If the
  asymmetry ever does become the dominant cost, the correct move is a new major version that renames
  everything at once, not a forward.

## References

- **REF-001**: [ADR-0019](adr-0019-dual-serial-transport-backends.md) — the backend decision this
  amends, and where the read-pump reasoning lives
- **REF-002**: [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) — the `IDuplexPipe` seam that makes
  backend choice a package reference
- **REF-003**: [TypeForwardedToAttribute][ref-forward] — type forwarding preserves the
  namespace-qualified name; it does not rename
- **REF-004**: `Source/CallAndResponse.Transport.Serial.Bcl/BclSerialDuplexPipe.cs`
- **REF-005**: `Source/Shared/SerialReadPump.cs` — the shared read loop, per ADR-0019 DEC-009

[ref-forward]: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.typeforwardedtoattribute
