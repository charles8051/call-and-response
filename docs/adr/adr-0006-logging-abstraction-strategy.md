---
title: "ADR-0006: Logging Abstraction Strategy"
status: "Accepted"
date: "2026-06-01"
authors: "Repository maintainer"
tags: ["architecture", "decision", "logging", "dependencies"]
supersedes: ""
superseded_by: ""
---

# ADR-0006: Logging Abstraction Strategy

## Status

**Accepted**

*Implementation status: Completed. Serilog removed from all source, project, and documentation files. `Microsoft.Extensions.Logging.Abstractions` adopted throughout. All construction sites default to `NullLogger.Instance`. `ILogger<T>` constructor overloads and `UseLogger<TCategory>` builder extension added. See IMP-009 for current state.*

## Context

The library previously used Serilog as its logging dependency.

- **CTX-001**: `Serilog.ILogger` was the logger type used across `Transceiver`, `SerialPortTransceiver`, `BleNordicUartTransceiver`, and `ModbusRtuClient`.
- **CTX-002**: `Serilog.Core` and `Serilog.Sinks.Console` / `Serilog.Sinks.Debug` were NuGet dependencies of source library projects, not just the sandbox.
- **CTX-003**: Because Serilog was in library projects, any consumer of the library was forced to pull in Serilog transitively, even if they preferred a different logging framework.
- **CTX-004**: `Serilog.Sinks.Console` was a transitive supplier of `System.Threading.Channels` in the BLE transport project. Removing Serilog without adding an explicit `System.Threading.Channels` reference would have broken the BLE project build.
- **CTX-005**: The library targets .NET Standard 2.0, .NET Standard 2.1, and .NET 8. A logging dependency must be lightweight, portable, and available across all three TFMs.
- **CTX-006**: `Microsoft.Extensions.Logging.Abstractions` is the standard .NET logging abstraction. It is distributed by Microsoft, carries no third-party lock-in, and is already available across .NET Standard 2.0, .NET Standard 2.1, and .NET 8 without runtime constraints.
- **CTX-007**: Library consumers may inject logging from ASP.NET Core's DI container (`ILogger<T>` from `IServiceProvider`), from a manually created `ILoggerFactory`, or may prefer to suppress all library logging entirely. All three scenarios should work without friction.

## Decision

Replace Serilog with `Microsoft.Extensions.Logging.Abstractions` as the logging dependency for all library source projects.

- **DEC-001**: All library projects (`CallAndResponse`, `Transport.Serial`, `Transport.Ble`, `Transport.Treehopper`, `Protocol.Modbus`, `Protocol.Stm32Bootloader`) depend on `Microsoft.Extensions.Logging.Abstractions` only. No concrete logging sink dependency appears in any library project.
- **DEC-002**: The public API accepts `ILogger` (non-generic) at library and factory boundaries where the concrete category is not meaningful to the caller. This keeps cross-package extension simple.
- **DEC-003**: All concrete transport and protocol types expose an `ILogger<T>` constructor overload to support DI-resolved loggers that carry category information (`ILogger<SerialPortTransceiver>`, `ILogger<ModbusRtuClient>`, etc.).
- **DEC-004**: `NullLogger.Instance` is the correct default at every construction site that accepts an optional logger. Construction must never throw when no logger is provided.
- **DEC-005**: `TransceiverBuilder` exposes `UseLogger(ILogger)` for explicit logger injection and `UseLogger<TCategory>(ILogger<TCategory>)` for DI-resolved typed loggers. The typed overload is available so that loggers resolved from `IServiceProvider` can be passed through the builder without losing category information.
- **DEC-006**: The Serilog bootstrapping in `Test/CallAndResponse.Test.Sandbox` was replaced with `Microsoft.Extensions.Logging.Console` via `LoggerFactory.Create(...)`. Sandbox projects may take a concrete logging sink dependency; library source projects may not.
- **DEC-007**: Serilog log-level mappings were translated as follows: `Verbose → LogTrace`, `Debug → LogDebug`, `Information → LogInformation`, `Warning → LogWarning`, `Error → LogError`, `Fatal → LogCritical`.

## Consequences

### Positive

- **POS-001**: Library consumers are free to use any logging backend (Serilog, NLog, log4net, `Microsoft.Extensions.Logging.Console`, or none) without a forced transitive Serilog dependency.
- **POS-002**: `Microsoft.Extensions.Logging.Abstractions` is a zero-cost dependency in ASP.NET Core and .NET 8 host applications — it is already present in the framework.
- **POS-003**: `ILogger<T>` constructor overloads allow DI containers to resolve typed loggers automatically without any manual wiring.
- **POS-004**: `NullLogger.Instance` as the default means library types are directly instantiable in tests and quick scripts without configuring a logger.
- **POS-005**: Removing Serilog eliminates surprise transitive dependencies such as `System.Threading.Channels` being supplied by a sink package.

### Negative

- **NEG-001**: Consumers who were previously relying on Serilog being present as a transitive dependency of this library will need to add an explicit Serilog package reference. This is a one-time migration cost and the correct long-term state.
- **NEG-002**: `Microsoft.Extensions.Logging` structured logging syntax (`{PropertyName}` in message templates) differs from Serilog's destructuring operators (`{@Object}`, `{$Value}`). Any structured logging added in future must use the MEL message template convention.

## Alternatives Considered

### Keep Serilog as the library's logging dependency

- **ALT-001**: **Description**: Retain Serilog as the logger type across all library packages.
- **ALT-002**: **Rejection Reason**: Forces every library consumer to take a Serilog transitive dependency regardless of their preferred logging framework. A library should not impose a concrete logging implementation on its consumers.

### Use `Microsoft.Extensions.Logging` (non-abstractions) package

- **ALT-003**: **Description**: Depend on the full `Microsoft.Extensions.Logging` package rather than just `Abstractions`.
- **ALT-004**: **Rejection Reason**: The full package includes factory and provider implementations. Library code only needs the abstractions (`ILogger`, `ILoggerFactory`, `NullLogger`). Adding the full package introduces unnecessary overhead and pulls in additional Microsoft.Extensions dependencies.

### Accept no logger dependency (use `Action<string>` or `TextWriter`)

- **ALT-005**: **Description**: Remove the logger parameter entirely and accept a raw delegate or writer for diagnostic output.
- **ALT-006**: **Rejection Reason**: `ILogger` is the established .NET logging abstraction with structured logging, log-level filtering, and scope support. Using raw delegates would duplicate the abstraction poorly and produce a worse integration story for consumers.

### Use `Microsoft.Extensions.Logging.Abstractions` only in the core package and a simpler mechanism elsewhere

- **ALT-007**: **Description**: Keep a full Serilog or no-op logger in transport packages while using MEL abstractions only in the core.
- **ALT-008**: **Rejection Reason**: Inconsistent logging abstractions across packages in the same library stack would confuse consumers and require adapter code at package boundaries.

## Implementation Notes

- **IMP-001**: `Microsoft.Extensions.Logging.Abstractions` version `8.0.2` is the pinned version across all library projects. This version targets `netstandard2.0`, `netstandard2.1`, and `net8.0`.
- **IMP-002**: `System.Threading.Channels` version `8.0.0` is an explicit package reference in `CallAndResponse.Transport.Ble.csproj`. It was previously supplied transitively by `Serilog.Sinks.Console`; after Serilog removal, an explicit reference is required.
- **IMP-003**: `Microsoft.Extensions.Logging.Console` version `8.0.1` is a package reference in the sandbox project only. It must not appear in any library source project.
- **IMP-004**: Serilog log-level `Verbose` maps to `ILogger.LogTrace`. There is no direct `Verbose` concept in MEL; `Trace` is the lowest MEL level.
- **IMP-005**: The non-generic `ILogger` interface is the correct type at `ITransceiverFactory.Create(ILogger logger)` and `TransceiverBuilder.WithLogger(ILogger)` because these are cross-package boundaries where the concrete category is determined by the transport, not the caller.
- **IMP-006**: The `ILogger<T>` constructor overload on each concrete type (e.g., `SerialPortTransceiver(ILogger<SerialPortTransceiver> logger)`) immediately assigns to the internal `ILogger _logger` field. The generic type parameter is used only for DI resolution; all internal logging uses the non-generic `ILogger` field.
- **IMP-007**: `UseLogger<TCategory>(this TransceiverBuilder builder, ILogger<TCategory> logger)` is the typed builder extension. It accepts any `ILogger<T>` and passes it to `WithLogger(logger)`, allowing DI-resolved loggers to flow through the builder without explicit casting.
- **IMP-008**: Test projects may reference `Microsoft.Extensions.Logging.Abstractions` directly. They should use `NullLogger.Instance` or `NullLogger<T>.Instance` for unit tests that do not assert on log output.
- **IMP-009**: *Current state as of implementation*: Serilog removed from all 6 library source projects. MEL abstractions in use across `Transceiver`, `SerialPortTransceiver`, `BleNordicUartTransceiver`, `ModbusRtuClient`. `ILogger<T>` overloads present on all concrete types. `UseLogger<TCategory>` on `TransceiverBuilder`. Sandbox bootstrapped via `LoggerFactory.Create`. 93 unit tests passing. See `Test/CallAndResponse.Test.Unit/TypedLoggerSupportTests.cs` for DI-ergonomic construction coverage.

## References

- **REF-001**: `Source/CallAndResponse/CallAndResponse.csproj` — `Microsoft.Extensions.Logging.Abstractions 8.0.2`
- **REF-002**: `Source/CallAndResponse/TransceiverBuilder.cs` — `UseLogger`, `UseLogger<TCategory>`, `WithLogger`
- **REF-003**: `Source/CallAndResponse/Transceiver.cs` — `NullLogger.Instance` default
- **REF-004**: `Source/CallAndResponse.Transport.Serial/SerialPortTransceiver.cs` — `ILogger<SerialPortTransceiver>` constructor
- **REF-005**: `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` — `ILogger<BleNordicUartTransceiver>` constructor
- **REF-006**: `Source/CallAndResponse.Protocol.Modbus/ModbusRtuClient.cs` — `ILogger<ModbusRtuClient>` constructor
- **REF-007**: `Source/CallAndResponse.Transport.Ble/CallAndResponse.Transport.Ble.csproj` — `System.Threading.Channels 8.0.0` explicit reference
- **REF-008**: `Test/CallAndResponse.Test.Sandbox/Program.cs` — `LoggerFactory.Create` with `Microsoft.Extensions.Logging.Console`
- **REF-009**: `Test/CallAndResponse.Test.Unit/TypedLoggerSupportTests.cs` — DI-ergonomic typed logger test coverage
- **REF-010**: [Microsoft.Extensions.Logging.Abstractions NuGet](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions)
- **REF-011**: `docs/adr/adr-0004-unify-transceiver-builder-api.md` — IMP-009 for `ILogger<T>` builder support context
