---
title: "ADR-0009b: Logging and Diagnostics Strategy"
status: "Superseded"
date: "2026-06-01"
supersedes: ""
superseded_by: "ADR-0006"
---

# ADR-0009b: Logging and Diagnostics Strategy

> **SUPERSEDED by [ADR-0006](adr-0006-logging-abstraction-strategy.md).** Both records decide the same
> question and reach the same answer: `Microsoft.Extensions.Logging.Abstractions` only, no sink. ADR-0006
> is the accepted one and carries the log-level mapping and version constraints. This draft was never
> given frontmatter and duplicates the ADR-0009 number already taken by
> [device discovery](adr-0009-device-discovery-out-of-scope.md); it is filed as 0009b rather than
> renumbered, because renumbering a published record breaks inbound links.

## Status

**Superseded by ADR-0006**

## Context

This project is a .NET library (or library-oriented application). It must integrate cleanly with whatever logging infrastructure the host application already uses rather than imposing its own provider or sink choices.

Logging decisions made early and applied consistently prevent a painful retrofit as the codebase grows. Without agreed conventions, teams produce inconsistent patterns across subsystems, miss diagnostic context in the layers that need it most, and risk introducing allocation pressure in performance-sensitive code paths.

This ADR establishes the logging, metrics, and diagnostics conventions for the project.

## Decision

### 1. Primary logging abstraction: `ILogger<T>`

All libraries in this project will use `Microsoft.Extensions.Logging.ILogger<T>` as the sole logging abstraction. This is the .NET standard, integrates with dependency injection, and allows consumers to plug in any provider they choose — Serilog, NLog, console, or nothing at all.

The project will not depend on any specific logging provider. It ships `ILogger` usage only.

Accept `ILogger<T>` through constructor injection. When a logger is optional (e.g., in types that can be newed up directly), accept `ILogger<T>?` and fall back to `NullLogger<T>.Instance`:

```csharp
public sealed class MyComponent
{
    private readonly ILogger<MyComponent> _logger;

    public MyComponent(ILogger<MyComponent>? logger = null)
    {
        _logger = logger ?? NullLogger<MyComponent>.Instance;
    }
}
```

### 2. Structured logging — always

All log calls will use semantic message templates with structured parameters rather than string concatenation or interpolation. This ensures log entries are machine-parseable regardless of which provider the consumer configures.

```csharp
// Good — structured template
logger.LogInformation("Opened resource {ResourceName} with {ItemCount} items", name, count);

// Bad — string interpolation (allocates even when level is disabled)
logger.LogInformation($"Opened resource {name} with {count} items");
```

Use PascalCase for template parameter names. They become property names in structured log sinks.

### 3. Log level conventions

Follow consistent log level assignments across all subsystems:

| Level | Usage | Examples |
|-------|-------|----------|
| **Trace** | Per-item hot-path diagnostics, normally disabled in production | Per-record processing, per-iteration timing, inner-loop decisions |
| **Debug** | Lifecycle transitions, internal state changes, decision points | Component activated/deactivated, queue depth changes, configuration applied |
| **Information** | Session-level or operation-level events visible in normal operation | Session opened, operation completed, significant state reached |
| **Warning** | Degraded but recoverable states | Retries, fallback activation, resource contention, dropped work items |
| **Error** | Operation failures that the caller should know about | Failed I/O, unhandled exceptions in worker loops, invalid external input |
| **Critical** | Unrecoverable failures that compromise the process | Missing required dependencies, fatal initialization errors, data corruption |

Rules of thumb:

- If you'd want to see it in a production dashboard during normal operation, it's **Information**.
- If you'd only enable it while actively debugging a specific issue, it's **Debug** or **Trace**.
- If it means something went wrong but the system recovered, it's **Warning**.
- If it means the current operation failed, it's **Error**.

### 4. Hot-path logging with `[LoggerMessage]` source generation

Logging in performance-sensitive code paths must use the `[LoggerMessage]` source generator. This avoids allocating message strings, boxing value-type arguments, or evaluating expressions when the target level is disabled.

```csharp
public sealed partial class MyHotPathComponent
{
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Processed item {ItemIndex} in {ElapsedMs:F2}ms")]
    private static partial void LogItemProcessed(
        ILogger logger, long itemIndex, double elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Buffer underrun #{UnderrunCount}: source starved. Free={FreeBuffers}, Queued={QueuedBuffers}")]
    private static partial void LogUnderrun(
        ILogger logger, long underrunCount, int freeBuffers, int queuedBuffers);
}
```

Requirements for `[LoggerMessage]` usage:

- The containing class must be `partial`.
- Log methods are `private static partial void`.
- The first parameter is always `ILogger logger`.
- Exception parameters (if needed) go last: `Exception ex`.
- Use format specifiers in the message template for numeric precision (e.g., `{ElapsedMs:F2}`).

For non-hot-path code (startup, shutdown, configuration, error handling), standard `ILogger` extension methods with structured templates are acceptable:

```csharp
_logger.LogInformation("Session opened: {DisplayName}", displayName);
```

### 5. Log method naming conventions

Source-generated log methods should follow a consistent naming pattern:

| Pattern | Usage |
|---------|-------|
| `Log{Event}` | General events: `LogStarted`, `LogStopped`, `LogDisposed` |
| `Log{Subject}{Event}` | Scoped events: `LogDeviceOpenFailed`, `LogSourceRestarted` |
| `Log{Severity}{Subject}` | When severity is the distinguishing factor: `LogWorkerFaulted`, `LogDisposeTimeout` |
| `LogPeriodic{Subject}` | Periodic status snapshots: `LogPeriodicStatus` |

Group log methods together at the bottom of the class, separated by a comment banner:

```csharp
// ── Source-generated log methods ─────────────────────────────────────

[LoggerMessage(Level = LogLevel.Debug,
    Message = "Component started with {BufferCount} buffers")]
private static partial void LogStarted(ILogger logger, int bufferCount);

[LoggerMessage(Level = LogLevel.Information,
    Message = "Component stopped. Items={ItemsProcessed}, errors={ErrorCount}, duration={DurationSec:F2}s")]
private static partial void LogStopped(
    ILogger logger, long itemsProcessed, long errorCount, double durationSec);
```

### 6. Quantitative telemetry with `System.Diagnostics.Metrics`

Complement `ILogger` with `System.Diagnostics.Metrics` for quantitative performance telemetry. Metrics are low-overhead by design and compatible with OpenTelemetry exporters, giving consumers opt-in observability without the library taking a dependency on any telemetry SDK.

#### Meter naming

Each project or subsystem gets one `Meter` with a dot-separated namespace:

```csharp
private static readonly Meter ComponentMeter = new("MyProject.Subsystem", "1.0.0");
```

#### Instrument naming

Use lowercase dot-separated names following the OpenTelemetry semantic convention style:

```csharp
private static readonly Counter<long> ItemsProcessedCounter =
    ComponentMeter.CreateCounter<long>(
        "myproject.subsystem.items_processed",
        description: "Total items processed.");

private static readonly Histogram<double> ProcessLatencyHistogram =
    ComponentMeter.CreateHistogram<double>(
        "myproject.subsystem.process_latency_ms",
        description: "Time spent processing each item.");
```

#### When to use Metrics vs. Logging

| Signal | Use |
|--------|-----|
| **Counter** | Monotonically increasing totals: items processed, errors encountered, retries performed |
| **Histogram** | Distribution of values: latency, queue depth, batch size |
| **ILogger (Trace/Debug)** | Per-item context with human-readable detail for active debugging |
| **ILogger (Information+)** | Discrete events with structured context: "session opened", "operation failed" |

Use both together when appropriate — a counter tracks that an underrun happened, while a warning log provides the surrounding context.

### 7. Periodic status logging

For long-running processing loops, emit periodic status logs at **Debug** level that summarize cumulative progress. Gate these on a modulo check to avoid flooding:

```csharp
if (itemsProcessed % 500 == 0)
{
    LogPeriodicStatus(_logger, itemsProcessed, elapsed.TotalSeconds, errorCount, queueDepth);
}
```

This gives operators a heartbeat view of processing health without requiring Trace-level verbosity.

### 8. Lifecycle event logging

Log lifecycle transitions consistently:

| Event | Level | What to include |
|-------|-------|-----------------|
| Component activated/started | Debug | Configuration summary (buffer sizes, capacities, options) |
| Component deactivated/stopped | Information | Cumulative stats (items processed, errors, duration) |
| Component disposed | Debug | Lifetime stats if different from stop |
| State transitions | Debug | From-state and to-state |
| Operation started (open, seek) | Information | Target/destination and key parameters |
| Worker faulted | Error | Worker name + exception |

Stop logs are promoted to **Information** because they carry the session summary that operators need without enabling Debug.

### 9. Diagnostics listener extension seam (optional)

For libraries that need to expose diagnostic events programmatically to consumers, define a focused callback interface separate from `ILogger`. This keeps the extension seam clean and purpose-built, without requiring consumers to intercept or filter log streams to observe behavior programmatically.

This is optional — not every project needs it. Consider adding it when:

- consumers need to react to events in code (not just observe logs)
- you want a stable diagnostic contract independent of log message text
- you need to expose periodic performance snapshots as structured data

## Consequences

### Positive

- Logging patterns are consistent from the first implementation phase instead of being retrofitted
- Consumers control provider choice and filtering without the library imposing opinions
- Hot-path guards prevent logging from introducing allocation pressure or latency in tight loops
- Metrics provide quantitative observability that complements human-readable log output
- Structured logging makes production diagnostics practical across all subsystems

### Negative

- Every subsystem must follow the log level conventions and hot-path guard discipline from the start
- Source-generated logging via `[LoggerMessage]` adds ceremony to hot-path code (partial class, static method, specific parameter ordering)
- Metrics instrumentation requires thought about which measurements are meaningful before the implementation is fully built

## Alternatives considered

### EventSource and ETW only

Rejected because EventSource is Windows-centric in practice and significantly harder for consumers to integrate compared to `ILogger`. It also lacks the ecosystem of structured logging providers that `ILogger` enables.

### Custom logging abstraction

Rejected because `ILogger` is the established .NET standard. Introducing a project-specific logging interface would force consumers to write adapters and would duplicate work the ecosystem has already solved.

### Defer logging decisions to a polish phase

Rejected because retrofitting structured logging across multiple implementation phases is painful, produces inconsistent conventions, and risks missing diagnostic context in the subsystems that need it most.

### ActivitySource and distributed tracing in v1

Deferred as premature for most library projects. Distributed tracing is designed for service-to-service request correlation. If consumer demand appears later, `ActivitySource` support can be added without disrupting the `ILogger` and `Metrics` foundations.

## Quick Reference

### Decision checklist for new code

1. **Is this a hot path?** (tight loop, per-item processing) → Use `[LoggerMessage]` source generation
2. **Is this a lifecycle event?** (start, stop, state change) → Use structured `ILogger` with appropriate level
3. **Is this a countable thing?** (items processed, errors, retries) → Add a `Counter<long>`
4. **Is this a measurable distribution?** (latency, queue depth) → Add a `Histogram<double>`
5. **Would an operator want to see this in normal production?** → `Information` level
6. **Would a developer only care during active debugging?** → `Debug` or `Trace` level

### Template

```csharp
public sealed partial class MyComponent
{
    private static readonly Meter MyMeter = new("MyProject.MySubsystem", "1.0.0");
    private static readonly Counter<long> ItemsCounter =
        MyMeter.CreateCounter<long>("myproject.subsystem.items_total");
    private static readonly Histogram<double> LatencyHistogram =
        MyMeter.CreateHistogram<double>("myproject.subsystem.latency_ms");

    private readonly ILogger<MyComponent> _logger;

    public MyComponent(ILogger<MyComponent>? logger = null)
    {
        _logger = logger ?? NullLogger<MyComponent>.Instance;
    }

    public void DoWork()
    {
        var sw = Stopwatch.StartNew();

        // ... processing ...

        sw.Stop();
        ItemsCounter.Add(1);
        LatencyHistogram.Record(sw.Elapsed.TotalMilliseconds);
        LogItemProcessed(_logger, itemIndex, sw.Elapsed.TotalMilliseconds);
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "Processed item {ItemIndex} in {ElapsedMs:F2}ms")]
    private static partial void LogItemProcessed(
        ILogger logger, long itemIndex, double elapsedMs);
}
```
