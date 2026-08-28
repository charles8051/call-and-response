---
title: "ADR-0004: Unify Transceiver Builder API"
status: "Superseded"
date: "2026-03-22"
authors: "Repository maintainer; library consumers"
tags: ["architecture", "decision", "api", "builder", "transceiver"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0004: Unify Transceiver Builder API

> **SUPERSEDED.** `TransceiverBuilder`, `SerialTransceiverBuilderStage`, and the `UseSerial(...).Build()` pattern were all
> removed. Transports are now constructed directly and handed to `new Transceiver(pipe)`. There is no
> builder API to unify. See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Accepted**

*Implementation status: Serial transport fully aligned — `TransceiverBuilder` state is private, `SerialTransceiverBuilderStage` implements the minimal staged pattern, legacy `UseSerial(Action<...>)` preserved. BLE and Treehopper transports are aligned in intent (DEC-007) but have not yet been migrated to `UseBle()`/`UseTreehopper()` staged patterns. See IMP-002 for open work.*

## Context

The repository currently exposes multiple creation patterns for transceivers that overlap in purpose but differ in shape and lifecycle.

- **CTX-001**: `Source/CallAndResponse/TransceiverBuilder.cs` defines a public record-based fluent builder that carries logger and factory state and finishes with `Build()`.
- **CTX-002**: `Source/CallAndResponse.Transport.Serial/TransceiverBuilderExtensions.cs` uses the builder pattern via `UseSerial(...).Build()`.
- **CTX-003**: `Source/CallAndResponse.Transport.Ble/TransceiverBuilderExtensions.cs` bypasses `Build()` and exposes `CreateBleTransceiver()` that returns `ITransceiver` directly.
- **CTX-004**: `Source/CallAndResponse.Transport.Treehopper/TreehopperTransceiver.cs` bypasses the builder entirely with a static async `Create()` method.
- **CTX-005**: `docs/ARCHITECTURE.md` documents `TransceiverBuilder` as the standard creation path, but `Test/CallAndResponse.Test.Sandbox/Program.cs` currently instantiates BLE directly.
- **CTX-006**: `TransceiverBuilder` currently exposes public underscored members (`_logger`, `_transceiverFactory`), leaking internal construction mechanics into the public API surface.
- **CTX-007**: The workspace targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8, so the chosen API shape should remain simple and portable across package boundaries.

## Decision

Adopt a single canonical transceiver creation pattern based on `TransceiverBuilder`, and align all transport packages to it.

- **DEC-001**: `TransceiverBuilder` becomes the canonical public entrypoint for fluent transceiver configuration.
- **DEC-002**: Each transport package will expose a `UseXxx(...)` extension method that returns `TransceiverBuilder` and participates in the same fluent pipeline.
- **DEC-003**: `Build()` will be the standard finalization step for all builder-driven transport creation.
- **DEC-004**: Builder state used to construct transceivers will be hidden from the public API; public underscored fields or properties will be removed or made non-public.
- **DEC-005**: Transport configuration will be normalized around transport-specific options types such as `SerialTransceiverOptions`, `BleTransceiverOptions`, and `TreehopperTransceiverOptions`.
- **DEC-006**: Transport instantiation will be standardized behind a single internal mechanism, preferably a private creation delegate or a non-leaking factory abstraction.
- **DEC-007**: Existing inconsistent entrypoints such as `CreateBleTransceiver()` and `TreehopperTransceiver.Create()` may remain temporarily for compatibility, but they will be treated as legacy paths and scheduled for deprecation.
- **DEC-011**: If the builder evolves toward a staged API, it should do so only at the transport boundary so compile-time guidance improves without turning the public API into a deeply generic state machine.
- **DEC-012**: The preferred staged shape is minimal and transport-local, for example `UseSerial()` unlocking `WithSerialPortOptions(...)` and `Build()`, rather than a broad multi-stage design spanning all builder concerns.

The intended consumer experience is:

- **DEC-008**: `new TransceiverBuilder().UseLogger(logger).UseSerial(...).Build()`
- **DEC-009**: `new TransceiverBuilder().UseLogger(logger).UseBle(...).Build()`
- **DEC-010**: `new TransceiverBuilder().UseLogger(logger).UseTreehopper(...).Build()`

## Consequences

### Positive

- **POS-001**: The API surface becomes consistent across transport packages and easier for consumers to learn.
- **POS-002**: Documentation, samples, and test coverage can converge on one canonical creation flow.
- **POS-003**: Hiding builder internals reduces accidental coupling to implementation details and makes future refactoring safer.
- **POS-004**: Standardized options types improve discoverability and transport-to-transport symmetry.
- **POS-005**: A single creation model better supports extension packages that want to plug into the same fluent pattern.
- **POS-006**: A minimal staged transport step can improve discoverability by exposing transport-specific configuration only after the transport has been selected.
- **POS-007**: Compile-time sequencing can prevent invalid combinations such as configuring serial-only options before choosing the serial transport.

### Negative

- **NEG-001**: Existing consumers using direct BLE or Treehopper creation paths may require migration or deprecation handling.
- **NEG-002**: Some transports have transport-specific lifecycle needs, so fitting them behind one model may require adapter code.
- **NEG-003**: Introducing a normalized builder contract may temporarily increase maintenance while legacy entrypoints remain supported.
- **NEG-004**: If async-only creation requirements expand in the future, the synchronous `Build()` shape may need complementary design work.
- **NEG-005**: A staged builder introduces extra interfaces or types, so an overly ambitious design would increase API and maintenance complexity beyond what this ADR is trying to standardize.

## Alternatives Considered

### Keep the Mixed Creation Patterns

- **ALT-001**: **Description**: Preserve the current split between builder-based serial creation, direct BLE creation, and static Treehopper factories.
- **ALT-002**: **Rejection Reason**: This keeps the public API inconsistent, weakens documentation quality, and prevents a single obvious consumer path.

### Prefer Concrete Constructors Only

- **ALT-003**: **Description**: Remove the builder and instruct consumers to instantiate transport-specific transceivers directly.
- **ALT-004**: **Rejection Reason**: This would reduce fluent discoverability, push transport details into application code, and make cross-package extensibility weaker.

### Keep the Builder but Only Rename Members

- **ALT-005**: **Description**: Retain the current record-based builder and public shape, but clean up method names without deeper alignment.
- **ALT-006**: **Rejection Reason**: Naming-only changes do not address the core inconsistency that different transports bypass the builder entirely.

### Use a Deeply Staged Builder Everywhere

- **ALT-007**: **Description**: Convert the entire builder flow into a strict staged API where each fluent step returns a different interface and unlocks subsequent operations.
- **ALT-008**: **Rejection Reason**: While this would maximize compile-time guidance, it adds substantial type complexity and works against the ADR's preference for a simple, portable, cross-package builder surface.

## Implementation Notes

- **IMP-001**: Refactor `TransceiverBuilder` so construction state is private or internal and no longer exposed as public underscored members. *Completed: `_logger` and `_transceiverFactory` are now private, exposed only through `WithLogger()` and `WithTransceiverFactory()` methods.*
- **IMP-002**: Add transport extensions `UseBle(...)` and `UseTreehopper(...)` that match the existing `UseSerial(...)` pattern. *Open: BLE still uses `CreateBleTransceiver()` and Treehopper still uses a static async `Create()`. Both should be migrated to builder-conformant `UseBle()`/`UseTreehopper()` staged patterns once the staged serial shape proves stable.*
- **IMP-003**: Builder internals use `ITransceiverFactory` as the non-leaking factory abstraction. `ITransceiverFactory` defines a single `Create(ILogger logger)` method; each transport package provides a concrete implementation injected via `WithTransceiverFactory()`. The `Func<ILogger, ITransceiver>` delegate style was considered but rejected in favor of the named interface for testability and substitutability.
- **IMP-004**: Mark `CreateBleTransceiver()` and `TreehopperTransceiver.Create()` as legacy entrypoints once equivalent builder-based entrypoints exist.
- **IMP-005**: Update `docs/ARCHITECTURE.md` and sandbox/sample usage so documentation reflects the canonical path. *Completed: ARCHITECTURE.md and README.md both reflect the staged serial flow.*
- **IMP-006**: Add unit tests that verify builder-based creation behavior and validate that each transport extension participates in the same fluent flow. *Completed: `SerialTransceiverBuilderStageTests.cs` and `TypedLoggerSupportTests.cs` cover staged builder and factory delegation.*
- **IMP-007**: Review whether a future `BuildAsync()` is needed for transports with async discovery requirements, but do not block unification on that follow-up.
- **IMP-008**: If staged configuration is introduced, keep it narrowly scoped to transport selection and transport options so `UseSerial().WithSerialPortOptions(...).Build()` remains understandable across .NET Standard and .NET 8 consumers. *Completed: `SerialTransceiverBuilderStage` is the reference implementation of this constraint.*
- **IMP-009**: `ILogger<T>` constructor overloads are supported on all concrete transport types (`SerialPortTransceiver`, `BleNordicUartTransceiver`, `BleNordicUartTransceiver<T>`, `ModbusRtuClient`). The builder exposes `UseLogger<TCategory>(ILogger<TCategory>)` as a typed overload to preserve type information through DI-resolved loggers. `NullLogger.Instance` is the correct default at all construction sites — never throw when no logger is provided.

## References

- **REF-001**: `docs/ARCHITECTURE.md`
- **REF-002**: `Source/CallAndResponse/TransceiverBuilder.cs`
- **REF-003**: `Source/CallAndResponse.Transport.Serial/TransceiverBuilderExtensions.cs`
- **REF-004**: `Source/CallAndResponse.Transport.Ble/TransceiverBuilderExtensions.cs`
- **REF-005**: `Source/CallAndResponse.Transport.Treehopper/TreehopperTransceiver.cs`
- **REF-006**: `Test/CallAndResponse.Test.Sandbox/Program.cs`
