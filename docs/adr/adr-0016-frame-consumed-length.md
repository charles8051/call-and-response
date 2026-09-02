---
title: "ADR-0016: Separate Payload Extent from Frame Extent in FrameDetectionResult"
status: "Accepted"
date: "2026-09-02"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "transceiver", "framing"]
supersedes: ""
superseded_by: ""
---

# ADR-0016: Separate Payload Extent from Frame Extent in FrameDetectionResult

## Status

**Accepted**

## Context

- **CTX-001**: `FrameDetectionResult` carried two numbers, `PayloadOffset` and `PayloadLength`, and
  `Transceiver.ReceiveMessage` advanced the `PipeReader` to `PayloadOffset + PayloadLength`. The type
  could describe what the caller receives, and nothing else. "What the frame occupied" and "what the
  caller wants back" were the same value by construction.

- **CTX-002**: Three of the built-in detectors return a payload that deliberately stops short of the
  delimiter they matched: `ReceiveUntilTerminator` and `ReceiveUntilTerminatorPattern` return
  `Complete(0, terminatorIndex)`, and `ReceiveUntilHeaderFooterMatch` returns
  `Complete(headerIndex + header.Length, payloadLength)`. Under CTX-001 the terminator, the pattern,
  and the footer were each left in the pipe.

- **CTX-003**: A leftover delimiter is not inert. `ReceiveUntilPerfectMatch` scans the whole
  accumulated buffer, so a stale byte satisfies the *next* command's detector before the device has
  answered, and `ReceiveExactly` starts its count on the stale byte. Every reply after that is shifted
  by a frame. Issue #7 records the AN3155 case: `GetSupportedCommands` followed by `GetId` parsed the
  device id as `0x0104` instead of `0x0413`.

- **CTX-004**: The two remaining detectors were already correct. `ReceiveUntilPerfectMatch` returns
  `Complete(matchIndex, matchBytes.Length)` and `ReceiveExactly` returns `Complete(0, n)`; in both the
  payload end *is* the frame end, so the derived advance was right by coincidence of shape rather than
  by accident of correctness.

- **CTX-005**: Delimiter stripping is the norm in wire protocols, not an edge case. Line terminators,
  footers, trailing checksums, and CRCs are all bytes a frame owns and a caller does not want. Any
  future detector for those shapes hits the same wall.

- **CTX-006**: The library's framing contract is public and stable: `FrameDetectionResult` appears in
  the signature of `ITransceiver.ReceiveMessage`, so any consumer can write a detector against it.
  A fix that recompiles differently, or that binds differently, is a real cost.

## Decision

Make the frame extent a first-class part of the detection result, distinct from the payload extent.

- **DEC-001**: `FrameDetectionResult` gains a third value, `ConsumedLength`: the number of bytes the
  detected frame occupies from the start of the accumulated buffer, including any delimiter that is
  not part of the payload.

- **DEC-002**: A new overload `Complete(int payloadOffset, int payloadLength, int consumedLength)`
  states the frame extent explicitly. The existing `Complete(int payloadOffset, int payloadLength)`
  is kept unchanged and derives `ConsumedLength` as `payloadOffset + payloadLength`, which is exactly
  what `ReceiveMessage` used to compute. Existing detectors — in this repository or in any consumer —
  keep their current behaviour with no edit.

- **DEC-003**: The consumed length is expressed as a *new overload*, not as an optional parameter on
  the existing method. An optional parameter would change the method's signature and break binary
  compatibility for already-compiled callers; an overload is both source- and binary-compatible.

- **DEC-004**: `Transceiver.ReceiveMessage` advances the reader to `result.ConsumedLength`. It no
  longer derives the advance from the payload. Bytes past the frame stay in the transport, as before.

- **DEC-005**: The three affected helpers pass the delimiter end:
  `ReceiveUntilTerminator` → `terminatorIndex + 1`, `ReceiveUntilTerminatorPattern` →
  `terminatorIndex + terminatorPattern.Length`, `ReceiveUntilHeaderFooterMatch` →
  `footerIndex + footer.Length`. Their returned payloads are unchanged.

- **DEC-006**: The three-argument overload validates: negative offsets or lengths are rejected, and so
  is a `consumedLength` shorter than `payloadOffset + payloadLength`, which would hand the caller
  bytes that are simultaneously left in the transport. The payload extent is computed as `long` in
  both overloads, because an `int` sum wraps and would let a short `consumedLength` pass that check.

- **DEC-007**: A payload extent that does not fit in an `int` is rejected by both overloads — in
  either direction, since the two-argument overload still accepts negative arguments and an
  underflowing sum would wrap to a large positive extent. This is the only guard added to the
  two-argument overload; it is not a behavioural regression, because such a call already failed —
  `ReceiveMessage` computed the same wrapped sum and threw from `ReadOnlySequence.GetPosition`. The
  change is that it now fails at the point of the mistake, with a parameter name, rather than deep in
  the receive loop. Beyond that the two-argument overload stays unvalidated so that nothing which
  compiles today starts throwing.

- **DEC-008**: `ReceiveMessage` validates a complete result against the accumulated buffer before it
  touches the reader: offsets and lengths must be non-negative, the payload must lie within the
  buffer, and `ConsumedLength` must be between the payload end and the buffer end inclusive. A result
  that fails throws `ArgumentException` naming `detectMessage`, and the reader is advanced to
  `buffer.Start` — consuming and examining nothing — so the bytes remain immediately readable and a
  caller that catches the exception can retry. A detector is caller-supplied code and
  `FrameDetectionResult` cannot know the buffer length, so this check can only live here.

## Consequences

### Positive

- **POS-001**: The framing bug is fixed at the layer that owns framing. Protocol clients need no
  change, and no protocol client has to drain the pipe before each command to stay correct.

- **POS-002**: The distinction between "what the caller wants" and "what the transport consumed" is
  now expressible. Trailing checksums, CRCs, and footers are describable by a detector directly
  instead of being smuggled through the payload and trimmed afterwards.

- **POS-003**: No consumer detector changes. `Complete(offset, length)` means precisely what it meant
  before, so recompilation and rebinding are both no-ops for existing code.

- **POS-004**: The trace log now reports the consumed length alongside offset and length, which is the
  number you need when diagnosing a frame-shift.

### Negative

- **NEG-001**: `FrameDetectionResult` now has two factory overloads where it had one, and a reader of
  the type has to understand why. The type is three numbers instead of two.

- **NEG-002**: Behaviour changes for anyone who was relying on the delimiter being left behind — for
  instance a caller that reads a line and then expects the `\n` in the next `ReceiveExactly`. That
  reliance is on a bug, and correcting it is the point of the change, but it is a behavioural break
  rather than a purely additive one.

- **NEG-003**: The two-argument overload validates only overflow while the three-argument one
  validates fully. The asymmetry is deliberate (DEC-006, DEC-007) and documented, but it is asymmetry.

- **NEG-004**: `ReceiveMessage` now carries a bounds check on every detected frame (DEC-008). It is a
  handful of integer comparisons per frame, but it is work the receive loop did not do before, and it
  changes the exception a malformed detector produces from `ArgumentOutOfRangeException` (raised by
  `Memory.Slice` or `ReadOnlySequence.GetPosition`) to `ArgumentException` naming `detectMessage`.

## Alternatives Considered

- **ALT-001 — Leave `FrameDetectionResult` alone; have each helper include the delimiter in the
  payload and slice it off after `ReceiveMessage` returns**: the second option in issue #7. **Rejection
  Reason**: it fixes the three helpers and leaves the contract unable to express the case, so every
  future detector — and every consumer-written one — meets the same wall and has to invent the same
  workaround. It also costs a copy per frame, and it moves framing responsibility out of the detector
  into the caller of the detector, which is the wrong direction for a library whose whole subject is
  framing.

- **ALT-002 — Add an optional `consumedLength` parameter to the existing `Complete`**: the literal
  shape sketched in the issue. **Rejection Reason**: adding a parameter, optional or not, changes the
  method signature. Source compatibility survives; binary compatibility does not, because a compiled
  caller still references the two-parameter method that no longer exists. An overload costs three
  extra lines and breaks nothing.

- **ALT-003 — Have `Transceiver` drain the pipe before each receive**: the workaround in the issue,
  promoted into the library. **Rejection Reason**: it discards data the library was not asked to
  discard, which is wrong for a transport that may legitimately carry a pipelined or unsolicited
  frame, and it papers over the framing defect rather than fixing it. Draining on a real serial line
  before a command sequence is still reasonable *transport* hygiene — it is just not this library's
  correctness mechanism.

- **ALT-004 — Return the consumed count out of band, e.g. a second out-parameter on
  `ReceiveMessage`**: **Rejection Reason**: the value is a property of the detected frame, so it
  belongs on the value the detector returns. Putting it anywhere else means detectors cannot report it.

## References

- **REF-001**: `Source/CallAndResponse/FrameDetectionResult.cs` — `ConsumedLength` and the two factories
- **REF-002**: `Source/CallAndResponse/Transceiver.cs` — the `AdvanceTo` in `ReceiveMessage`
- **REF-003**: `Source/CallAndResponse/TransceiverExtensions.cs` — the three delimiter-consuming helpers
- **REF-004**: `Test/CallAndResponse.Test.Unit/TransceiverTests.cs` — two-command sequences per helper
- **REF-005**: Issue #7 — "Framing helpers leave their delimiter in the pipe, so the next command
  matches a stale byte"
