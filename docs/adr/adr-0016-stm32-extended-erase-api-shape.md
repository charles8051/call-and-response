---
title: "ADR-0016: Split the STM32 Extended Erase API by AN3155 Erase Form"
status: "Accepted"
date: "2026-09-02"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "stm32", "protocol"]
supersedes: ""
superseded_by: ""
---

# ADR-0016: Split the STM32 Extended Erase API by AN3155 Erase Form

## Status

**Accepted**

## Context

- **CTX-001**: `Stm32BootloaderClient.ExtendedEraseMemoryPages(ushort numPages, ...)` was the only way
  to issue the AN3155 Extended Erase command (0x44). It always emitted an explicit page list.

- **CTX-002**: AN3155 section 3.7 defines three erase forms behind one command byte, distinguished by
  the first half-word of the payload:
  - `0xFFFF` — mass erase,
  - `0xFFFE` — bank 1 erase,
  - `0xFFFD` — bank 2 erase,
  - any other value `N` — erase `N + 1` pages, whose numbers follow as half-words.

  The three special codes are sent as the half-word plus its checksum and carry **no** page list.

- **CTX-003**: The single-method shape cannot express the special codes. Passing `0xFFFF` fell into the
  page loop and produced 65537 half-words — roughly 128 KB on the wire and not a valid frame.

- **CTX-004**: The `numPages` parameter was in fact the AN3155 half-word `N`, so it erased pages
  `0..numPages` inclusive. A caller wanting four pages had to pass `3`. The name said otherwise.

- **CTX-005**: The erased range always began at page 0, so a bootloader-preserving flash layout — erase
  the application region, leave the bootloader alone — could not be expressed.

- **CTX-006**: The three forms differ in payload shape, not only in argument values. A single method
  with a sentinel argument would have to document that some arguments silently suppress the page list.

- **CTX-007**: Every other public method on `Stm32BootloaderClient` (`Ping`, `GetId`, `ReadMemory`,
  `WriteMemory`, `Go`) returns a `Task` without an `Async` name suffix.

## Decision

- **DEC-001**: Model each AN3155 erase form as its own method:

  ```csharp
  public Task ExtendedEraseMass(CancellationToken token = default);
  public Task ExtendedEraseBank(int bank, CancellationToken token = default);
  public Task ExtendedErasePages(IReadOnlyList<ushort> pages, CancellationToken token = default);
  ```

- **DEC-002**: `ExtendedErasePages` takes the page numbers the caller wants erased. The AN3155
  half-word `N = pages.Count - 1` is computed internally and never appears in the public surface. The
  list is sent verbatim, so a window above page 0 is expressed by listing those pages.

- **DEC-003**: `ExtendedEraseBank` takes `int bank` and accepts only `1` or `2`, throwing
  `ArgumentOutOfRangeException` otherwise.

- **DEC-004**: `ExtendedErasePages` rejects a null or empty list, and rejects more than `0xFFFD` pages,
  because half-words `0xFFFD..0xFFFF` are reserved for the special codes and cannot be a page count.

- **DEC-005**: The new methods follow the file's existing no-`Async`-suffix convention rather than the
  `…Async` names sketched in the issue, per the "match the surrounding file" rule in `CONTRIBUTING.md`.

- **DEC-006**: `ExtendedEraseMemoryPages` is kept as an `[Obsolete]` shim that delegates to
  `ExtendedErasePages` with pages `0..numPages`. Its bytes on the wire are unchanged.

## Consequences

### Positive

- **POS-001**: Mass erase and bank erase are expressible at all, with the exact AN3155 payload —
  half-word plus checksum, no page list.

- **POS-002**: A page window that does not start at zero is expressible, which is what a
  bootloader-preserving layout needs.

- **POS-003**: The off-by-one is gone from the public surface: a caller asking for three pages passes
  three page numbers.

- **POS-004**: Each method has one payload shape, so there is no argument value that silently changes
  what gets sent.

- **POS-005**: No breaking change. Existing callers keep compiling and keep sending identical bytes;
  they get a deprecation warning pointing at the replacement.

### Negative

- **NEG-001**: Four erase-related methods where there was one. The command byte and checksum logic are
  shared privately, but the public surface is wider.

- **NEG-002**: The obsolete shim keeps the misleading `numPages` semantics alive until it is removed in
  a future major version.

- **NEG-003**: `ExtendedErasePages` allocates a list of the caller's page numbers. For a large erase
  this is a bigger allocation than the old contiguous loop, though still bounded by the frame the
  protocol requires anyway.

## Alternatives Considered

### Keep one method and overload the argument with the special codes

- **ALT-001**: **Description**: Retain `ExtendedEraseMemoryPages(ushort)` and special-case `0xFFFF`,
  `0xFFFE`, and `0xFFFD` inside it to suppress the page list.

- **ALT-002**: **Rejection Reason**: The payload shape would then depend on the argument value in a way
  the signature does not show, and the method would still be unable to erase a window above page 0.

### Take a start page and a count instead of a page list

- **ALT-003**: **Description**: `ExtendedErasePages(ushort startPage, ushort count, ...)`.

- **ALT-004**: **Rejection Reason**: AN3155 sends an arbitrary list of page numbers, not a range. A
  range API would be strictly less expressive than the wire format for no gain; a caller with a
  contiguous range can build one with `Enumerable.Range`.

### Introduce an enum for the bank argument

- **ALT-005**: **Description**: `ExtendedEraseBank(Stm32FlashBank bank, ...)` with a two-value enum.

- **ALT-006**: **Rejection Reason**: AN3155 names the banks 1 and 2, and the validated `int` keeps the
  new public type count at zero. Revisit if further bank-scoped commands appear.

### Remove `ExtendedEraseMemoryPages` outright

- **ALT-007**: **Description**: Delete the old method now, since the library is pre-1.0.

- **ALT-008**: **Rejection Reason**: It is a published package method with a straightforward
  replacement. `[Obsolete]` costs one shim and gives downstream callers a compiler-guided migration.

## Implementation Notes

- **IMP-001**: The checksum is the XOR of every payload byte. The existing helper computes this as
  `~ComputeChecksum(payload)`, where `ComputeChecksum` seeds with `0xFF`; the double inversion cancels.
  That expression is reused unchanged so all erase forms share one checksum path.

- **IMP-002**: Half-words go on the wire big-endian, as elsewhere in the client.

## References

- **REF-001**: `Source/CallAndResponse.Protocol.Stm32Bootloader/Stm32BootloaderClient.cs`
- **REF-002**: `Test/CallAndResponse.Test.Unit/Stm32BootloaderClientTests.cs`
- **REF-003**: ST AN3155, *USART protocol used in the STM32 bootloader*, section 3.7 (Extended Erase
  Memory command)
