---
title: "ADR-0018: Scope of the STM32 Bootloader Command Surface"
status: "Accepted"
date: "2026-09-02"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "stm32", "bootloader", "protocol", "public-surface"]
supersedes: ""
superseded_by: ""
---

# ADR-0018: Scope of the STM32 Bootloader Command Surface

## Status

**Accepted**

## Context

- **CTX-001**: `Stm32BootloaderClient` declared seven public methods whose entire body was
  `throw new NotImplementedException()`: `GetProtocolVersion` (AN3155 0x01), `EraseMemory` (0x43),
  `WriteProtect` (0x63), `WriteUnprotect` (0x73), `ReadoutProtect` (0x82), `ReadoutUnprotect` (0x92),
  and `GetChecksum` (0xA1).

- **CTX-002**: Those methods are indistinguishable, at the call site, from the six that work. They
  appear in IntelliSense, they compile, and they are discovered to be missing only when the call
  runs — which for this library means with a part wired to a serial port, mid-flash. That is the
  most expensive place to find out.

- **CTX-003**: The seven are not equally valuable. `ReadoutUnprotect` is the only route back for a
  part shipped at RDP level 1, which refuses both Read Memory and Write Memory; without it such a
  part cannot be recovered through this client at all. `EraseMemory` (0x43) is the only erase
  command on bootloaders below protocol 3.0, where Extended Erase does not exist. `GetChecksum`
  gives a verify path that does not require reading a whole image back over the wire.
  `GetProtocolVersion` is informational and partly redundant with `GetSupportedCommands`, which
  already returns the version byte.

- **CTX-004**: `WriteProtect`, `WriteUnprotect`, and `ReadoutProtect` are different in kind. They
  rewrite option bytes. Issued wrongly they leave a part write-locked, or at RDP level 1 where the
  only way out is a mass erase. A wire-format mistake in a read command returns a wrong number; a
  wire-format mistake in these returns a device somebody has to physically recover.

- **CTX-005**: This repository has no hardware in its test loop. CONTRIBUTING states that the suite
  runs entirely against in-memory pipes. A byte-exact test against a fake `IDuplexPipe` proves the
  frame matches the author's reading of AN3155; it does not prove the part agrees. For read and
  erase commands that gap is acceptable, because the failure mode is a NACK or a wrong value. For
  the option-byte commands it is not.

- **CTX-006**: AN3155 Rev 16 §3.13 (Get Checksum) has an internal inconsistency. The prose byte list
  on page 38 interleaves an extra "Wait for ACK" between the CRC polynomial bytes and their checksum
  byte, and again for the CRC initialization value, which would make those two frames shaped
  differently from the start-address and size frames. Figure 26 (host side) and Figure 27 (device
  side) both show four bytes plus one checksum byte per parameter frame, each answered by a single
  ACK — the same shape as every other 32-bit parameter in the protocol. The figures are self
  consistent and the prose is not, so the figures decide it.

- **CTX-007**: The existing `EraseMemory(uint address, ushort length, CancellationToken)` signature
  cannot express command 0x43. The command addresses flash by single-byte page code, not by address
  and length, and the mapping from an address range to page codes needs a per-device flash layout
  that this library does not have and (per the standing `TODO` about MCU-model-specific support)
  does not currently intend to acquire.

- **CTX-008**: The trailing byte the device appends to the Get Checksum result is described in
  §3.13 prose as a "complement byte (checksum)". ST uses that same phrase for the host-to-device
  size frame, whose byte list then defines it explicitly as `XOR (byte 8, byte 9, byte 10, byte 11)`.
  The phrase is therefore ST's loose synonym for an XOR checksum byte, not a bitwise complement.

- **CTX-009**: The Get Checksum size parameter is a count of 32-bit words, not a byte count. AN3155
  Rev 16 says so in three places that agree: the §3.13 prose ("the size of the memory area that is
  expressed in 32-bit words (4 bytes) number"), the p.38 byte list ("Bytes 8 to 11: Memory area size
  (number of 32-bit words)"), and both figures ("number of 32-bit words (4 bytes) with checksum (1
  byte)"). The unit is easy to misread — the document's separate statement that "the memory area size
  must be a multiple of 32 bits (4 bytes)" describes the region, not the encoding of this field — so
  the parameter is named for its unit and the distinction is stated on the parameter's own
  documentation.

- **CTX-010**: Removing a public method changes the assembly's metadata, not just its source
  contract. A binary compiled against the previous package that merely *contains* a call to the
  removed method fails to JIT its enclosing method with `MissingMethodException`, even on a branch
  that never executes. That is a worse and less legible failure than the `NotImplementedException`
  the old method would have thrown.

## Decision

Implement the four commands whose failure modes are recoverable, and make the three option-byte
commands non-callable rather than shipping them unverified.

- **DEC-001**: `ReadoutUnprotect` (0x92) is implemented. Host sends `0x92 0x6D` and waits for ACK,
  then waits for a second ACK which the device sends only after the mass erase completes. Its XML
  documentation states, in the summary, that the command mass erases the flash — that erase is the
  mechanism by which RDP level 1 is left, not an avoidable side effect — and that most parts issue a
  system reset afterwards.

- **DEC-002**: `EraseMemory` (0x43) is implemented as two methods.
  `EraseMemory(IEnumerable<byte> pageNumbers, CancellationToken)` sends `0x43 0xBC`, waits for ACK,
  then sends `N` (page count minus one), the page codes, and the XOR of all of them, and waits for a
  second ACK. `EraseAllMemory(CancellationToken)` sends `0x43 0xBC`, waits for ACK, then sends the
  reserved global-erase request `0xFF 0x00` and waits for a second ACK. At most 255 pages fit in one
  command because `N = 255` is reserved for global erase; larger lists are rejected with
  `ArgumentException` rather than silently truncated.

- **DEC-003**: `GetChecksum` (0xA1) is implemented as
  `GetChecksum(uint address, uint numWords, uint crcPolynomial, uint crcInitialValue, CancellationToken)`,
  returning the `uint` the device computed. The four parameter frames are sent in the order AN3155
  gives — start address, size in 32-bit words, CRC polynomial, CRC initialization value — each as
  four big-endian bytes plus their XOR, each answered by one ACK, per CTX-006. The polynomial and
  seed default to the STM32 CRC unit's reset values (`0x04C11DB7` and `0xFFFFFFFF`), exposed as
  `DefaultCrcPolynomial` and `DefaultCrcInitialValue`. The size parameter is a **word count, not a
  byte count**, per CTX-009; it is named `numWords` and sent unscaled.

- **DEC-004**: `GetProtocolVersion` (0x01) is implemented, returning a new `Stm32VersionInfo` holding
  the version byte and the two legacy option bytes. Host sends `0x01 0xFE` and reads exactly five
  bytes: `ACK, version, option 1, option 2, ACK`. The wire format is unambiguous in both the prose
  and the figures, so the command's low value is not a reason to leave it throwing.

- **DEC-005**: `WriteProtect` (0x63), `WriteUnprotect` (0x73), and `ReadoutProtect` (0x82) are marked
  `[Obsolete("…", true)]`, which makes calling them a compile error rather than a runtime surprise.
  Each carries an XML `<remarks>` naming the specific hazard. They are not removed: the declaration
  plus the error message is a better signpost than a missing member, and it records that the gap is
  deliberate.

- **DEC-006**: Newly implemented commands validate the device's status byte. A NACK, or any byte that
  is neither ACK nor NACK, raises `InvalidOperationException` naming the command. They read the
  status byte with `ReceiveExactly(1)` rather than scanning for an ACK with `ReceiveUntilPerfectMatch`,
  so a NACK fails immediately instead of blocking until the caller's token cancels.

- **DEC-007**: The `GetChecksum` result frame's trailing byte is validated as the XOR of the four CRC
  bytes, per CTX-008, and a mismatch raises `InvalidOperationException`. A verify command that can
  silently return a corrupted value is worse than no verify command.

- **DEC-008**: The three deferred commands are implemented only once they can be exercised against
  real silicon. Nothing about this record blocks that; it records why they were not shipped on a
  reading of the application note alone.

- **DEC-009**: `EraseMemory(uint address, ushort length, CancellationToken)` is kept as a declaration
  marked `[Obsolete(…, true)]` rather than deleted, per CTX-010. Source callers get the same compile
  error and migration message they would get from a deletion, while the method entry point survives
  for already-compiled binaries. It is the same treatment DEC-005 gives the protection commands, for
  the same reason: an error-level `[Obsolete]` communicates more than an absence.

- **DEC-010**: Destructive commands document that cancellation does not roll back the operation.
  Once `EraseMemory`'s page frame, `EraseAllMemory`'s `0xFF 0x00`, or `ReadoutUnprotect`'s command
  frame has been accepted, the device proceeds whatever the host does, so an
  `OperationCanceledException` means the outcome is unknown rather than that nothing happened. This
  is stated on each command's `token` parameter, where a caller writing the cancellation handler is
  looking, rather than only in the remarks.

## Consequences

### Positive

- **POS-001**: No method on `Stm32BootloaderClient` can now be called successfully from source and
  then throw `NotImplementedException` at runtime. Every remaining gap is a compile error with a
  message explaining it.

- **POS-002**: A part at RDP level 1 is recoverable through this client, which it previously was not.

- **POS-003**: Bootloaders below protocol 3.0 have an erase path, which they previously did not.

- **POS-004**: The new commands fail fast and specifically. `InvalidOperationException` naming the
  command and the offending byte replaces both `NotImplementedException` and, for NACK, an
  indefinite wait.

- **POS-005**: The most dangerous three commands cannot be invoked by a caller who assumed they
  worked because they compiled.

### Negative

- **NEG-001**: `EraseMemory(uint address, ushort length, CancellationToken)` becomes a compile error
  (CS0619). It only ever threw, so nothing that worked stops working, but code that compiled against
  it no longer compiles. Callers move to `EraseMemory(pageNumbers)` or `EraseAllMemory()`, which
  requires them to know their device's flash page layout — a burden the old signature only appeared
  to lift. Per DEC-009 the declaration is retained, so binary compatibility is preserved; the cost is
  a dead method kept alive in the type.

- **NEG-002**: `GetProtocolVersion` returns `Task<Stm32VersionInfo>` rather than `Task`, and
  `GetChecksum` returns `Task<uint>` and takes four new parameters. Both are breaking signature
  changes to methods that previously only threw.

- **NEG-003**: Calling `WriteProtect`, `WriteUnprotect`, or `ReadoutProtect` is now a compile error
  (CS0619). Any code that referenced them — necessarily code that could only have thrown — stops
  building.

- **NEG-004**: Three commands remain unimplemented. The library's AN3155 coverage is honest about
  itself but still partial, and a consumer who needs write protection must drive the bootloader
  themselves for that one command.

- **NEG-005**: The implemented frames are verified against a fake `IDuplexPipe`, not against silicon.
  The tests prove the bytes match this reading of AN3155 Rev 16; they cannot prove a given part's
  bootloader agrees, and the Get Checksum polynomial and seed frames rest on the figures over the
  prose (CTX-006).

## Alternatives Considered

### Implement all seven commands

- **ALT-001**: **Description**: Write all seven against AN3155 with byte-exact tests, treating the
  three option-byte commands like any other and relying on the application note plus review.
- **ALT-002**: **Rejection Reason**: The verification available here — a fake pipe and a careful read
  of a document with a known internal inconsistency (CTX-006) — is not proportionate to the failure
  mode. `WriteProtect` takes a sector list whose encoding is device specific; getting it wrong locks
  sectors. `ReadoutProtect` moves the part to RDP level 1, from which the only exit erases the flash.
  Shipping those on an untested reading converts a compile error into a bricked board. The three are
  deferred until there is hardware to test them on, which is a schedule decision, not a permanent one.

### Remove the unimplemented methods entirely

- **ALT-003**: **Description**: Delete all seven declarations. The public surface then contains only
  commands that work, and the missing ones are simply absent.
- **ALT-004**: **Rejection Reason**: For the four now implemented this was moot. For the remaining
  three, deletion loses information. A missing member says nothing; `[Obsolete(…, true)]` with a
  reason tells the caller the command exists in the protocol, is known to be missing here, and why —
  and it keeps the enum member, the command byte, and the hazard documented in one place. Deletion
  also invites a future contributor to re-add a stub without knowing the history.

### Leave the stubs and document the gap in the README

- **ALT-005**: **Description**: Keep `NotImplementedException` and list the unimplemented commands in
  prose so callers can check before calling.
- **ALT-006**: **Rejection Reason**: This is the status quo plus a document nobody reads at the call
  site. The complaint is that the compiler and IntelliSense both say the command is available; a
  README does not change what either says.

### Keep the old `EraseMemory(address, length)` signature and derive page codes

- **ALT-007**: **Description**: Preserve the existing signature by mapping an address range onto page
  codes internally.
- **ALT-008**: **Rejection Reason**: That mapping needs a per-device flash layout table — page sizes
  differ across and within families, and some parts have unequal sector sizes. Building one is the
  MCU-model-specific support the file's standing `TODO` contemplates and is a much larger change than
  this one. Guessing a uniform page size would produce a signature that looks precise and erases the
  wrong flash.

### Validate ACKs with `ReceiveUntilPerfectMatch`, matching `Go` and `WriteMemory`

- **ALT-009**: **Description**: Use `SendReceivePerfectMatch(frame, [ACK])` for the new commands, as
  the existing `Go`, `WriteMemory`, and `ExtendedEraseMemoryPages` do.
- **ALT-010**: **Rejection Reason**: That call scans the accumulated buffer for an ACK and keeps
  reading until it finds one, so a NACK is not a failure but a wait that ends when the caller's token
  cancels. For `ReadoutUnprotect` and `EraseAllMemory`, whose tokens must already be sized for a mass
  erase, that wait is long and the resulting `OperationCanceledException` says nothing about the
  device having refused. Reading exactly one byte and inspecting it reports the refusal immediately.
  The existing methods are not changed here; that is a separate concern.

## Implementation Notes

- **IMP-001**: `Source/CallAndResponse.Protocol.Stm32Bootloader/Stm32BootloaderClient.cs` — the four
  implementations, the `[Obsolete]` attributes on the three deferred commands and on the retained
  `EraseMemory(uint, ushort, CancellationToken)` declaration, the
  `DefaultCrcPolynomial` and `DefaultCrcInitialValue` constants, the `Stm32VersionInfo` type, and the
  private `SendAndExpectAck`, `ExpectAck`, `EnsureAck`, and `BigEndianWithChecksum` helpers.

- **IMP-002**: `EnsureAck` takes a `ReadOnlySpan<byte>` and is called with a span argument from
  `async` methods. A `ref struct` *local* in an async method requires C# 13; passing one as an
  argument does not, so the helpers avoid span locals. `GetChecksum` copies the four CRC bytes out
  with `ToArray()` for the same reason.

- **IMP-003**: `Test/CallAndResponse.Test.Unit/Stm32BootloaderClientTests.cs` — byte-exact wire tests
  for each implemented command, covering the command frame, every parameter frame and its checksum,
  each ACK in the handshake, the NACK path at each handshake point, and the argument validation.

- **IMP-004**: `docs/ARCHITECTURE.md` — the STM32 bootloader command list is extended with the newly
  implemented commands and a note naming the three that are deliberately non-callable.

- **IMP-005**: The `Stm32BootloaderCommand` enum is unchanged. All seven command bytes were already
  defined there and remain defined, including the three deferred ones.

## References

- **REF-001**: `Source/CallAndResponse.Protocol.Stm32Bootloader/Stm32BootloaderClient.cs`
- **REF-002**: `Test/CallAndResponse.Test.Unit/Stm32BootloaderClientTests.cs`
- **REF-003**: `docs/ARCHITECTURE.md` — STM32 Bootloader section
- **REF-004**: AN3155 Rev 16, *USART protocol used in the STM32 bootloader* — §3.2 Get Version,
  §3.7 Erase Memory, §3.9 Write Protect, §3.10 Write Unprotect, §3.11 Readout Protect,
  §3.12 Readout Unprotect, §3.13 Get Checksum
- **REF-005**: `docs/adr/adr-0001-testing-strategy.md` — the in-memory testing posture that CTX-005
  describes the limits of
- **REF-006**: GitHub issue #10 — the report this record answers
