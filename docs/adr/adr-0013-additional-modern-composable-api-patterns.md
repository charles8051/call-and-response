---
title: "ADR-0013: Additional Modern Composable API Patterns Beyond ADR-0012"
status: "Withdrawn"
date: "2026-03-23"
authors: "Repository maintainer"
tags: ["architecture", "decision", "api", "composition", "functional", "ergonomics", "streaming", "decorators", "capabilities", "options"]
supersedes: ""
superseded_by: "ADR-0015"
---

# ADR-0013: Additional Modern Composable API Patterns Beyond ADR-0012

> **WITHDRAWN.** Never implemented, and premised on ADR-0012, which was itself withdrawn. Kept for the survey of options.
> See [ADR-0015](adr-0015-duplex-pipe-transport-seam.md).


## Status

**Proposed**

## Implementation Planning Update

> **Added 2026-03-25.** This ADR covers a broad set of additive patterns. Not all of them are near-term commitments. Subsequent implementation planning has separated the patterns into two tiers: those that are **aligned with the near-term plan** and those that remain **exploratory / deferred**. The full decision analysis below is preserved as design exploration. Readers should treat this amendment as the authoritative guide to which patterns are active.
>
> The near-term composition plan this line pointed at has been removed; it described a two-repository
> roadmap premised on the `IByteSource` lending model. See
> [ADR-0015](adr-0015-duplex-pipe-transport-seam.md) for what was actually built.

### Near-term aligned patterns

These patterns are consistent with the near-term plan, reinforce the capability-interface and options-record direction, and do not depend on any deferred item being resolved first.

| Pattern | Near-term status | Note |
|---|---|---|
| DEC-001 — Capability/facet interfaces (`IHasRawFrames`, etc.) | **Planned direction** — adopt as the preferred answer to optional richness instead of widening `ITransceiver` | Supersedes the proposal to put `ReceiveFrames` directly on `ITransceiver` |
| DEC-002 — Options records over additive overloads | **Planned now** — apply immediately to any new configurable decorator or helper | Required before any retry decorator ships |
| DEC-006 — Observer-style diagnostics hooks | **Planned direction** — the preferred way to add diagnostic visibility without competing receive surfaces | Consistent with ADR-0008's rejection of `DataReceived` |
| DEC-007 — Paired throwing / non-throwing protocol methods | **Planned direction** — affirmed for protocol packages as a companion to result-union types | No structural change to `ITransceiver` |
| DEC-008 — Semantically concrete discriminated-union result names | **Planned direction** — naming guidance aligned with ADR-0012 DEC-004 | Protocol packages follow this guidance when adding result types |

### Exploratory / deferred patterns

These patterns are interesting long-term directions but are not near-term commitments. Each depends on either a deferred item from ADR-0012 being resolved first, or on semantic questions that remain open.

| Pattern | Status | Blocking condition |
|---|---|---|
| DEC-003 — Stream combinator extensions (`SelectFrames`, `ChooseFrames`) | **Deferred** — depends on `ReceiveFrames` shape being settled | Revisit once capability-interface approach for `IHasRawFrames` is validated |
| DEC-004 — Protocol transparency via `IProtocolClient<TTransport>` | **Deferred** — risks piercing abstraction boundaries; observer hooks are preferred for diagnostics | Revisit only if observer hooks prove insufficient for a concrete transparency need |
| DEC-005 — Session-scoped cancellation/liveness accessors | **Deferred** — depends on `BeginSession()` / `ITransceiverSession` being adopted | Moot while sessions are deferred in ADR-0012 |
| DEC-009 — Tiny functional pipeline helpers (`Pipe`) | **Deferred** — style flourish, not an architectural need; low priority | No blocking condition; revisit only if real call-site pressure emerges |

## Context

ADR-0012 established a strong direction for the `CallAndResponse` public surface:

- extension-method composition chains over byte sources
- `IAsyncEnumerable<T>` receive streams
- scoped session objects
- discriminated-union-style results at the protocol layer
- optional callback-scoped loan helpers

That ADR already captures the most important structural shift: the library should present a composition-oriented, lending-friendly surface instead of a lifecycle-owning transceiver abstraction.

However, once ADR-0012 is accepted as the baseline, several additional patterns become natural next steps. These patterns do not replace ADR-0012. They refine it and fill in gaps around:

1. optional capabilities on wrapped/decorated transports
2. configuration growth without overload explosion
3. stream transformation ergonomics above `ReceiveFrames`
4. protocol-client adaptation patterns
5. diagnostics and observability hooks
6. session-scoped cancellation and liveness propagation
7. higher-level non-throwing convenience APIs at the protocol layer

The purpose of this ADR is to document these additional patterns, identify which are worth adopting, and provide concrete API-shape examples for future discussion.

## Decision

Adopt a second-layer set of additive modern API patterns that build on ADR-0012 rather than competing with it.

### DEC-001: Add capability/facet interfaces for optional transport features

Do not continue to widen `ITransceiver` indefinitely. Instead, model optional features as narrow capability interfaces that decorators, sessions, or specialized wrappers may implement.

Example:

```csharp
public interface IHasRawFrames
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveFrames(
        Func<ReadOnlyMemory<byte>, FrameDetectionResult> detectFrame,
        CancellationToken cancellationToken = default);
}

public interface IHasConnectionState
{
    bool IsConnected { get; }
    event EventHandler? Disconnected;
}

public interface IHasDiagnostics
{
    ITransceiverDiagnostics Diagnostics { get; }
}
```

Usage:

```csharp
var transceiver = byteSource
    .AsTransceiver()
    .WithLogging(logger);

if (transceiver is IHasRawFrames rawFrames)
{
    await foreach (var frame in rawFrames.ReceiveFrames(detectFrame, ct))
    {
        Console.WriteLine($"Frame length = {frame.Length}");
    }
}
```

This keeps the base transport contract narrow while still allowing richer wrappers to expose richer behavior.

### DEC-002: Prefer options records over additive overload growth

When decorators and stream helpers gain more knobs, move from primitive overloads to small immutable options records.

Example:

```csharp
public sealed record RetryOptions(
    int MaxAttempts = 3,
    TimeSpan? Delay = null,
    Func<Exception, bool>? ShouldRetry = null);

public sealed record FrameStreamOptions(
    bool CompletePendingFrameOnEndOfStream = false,
    int? MaxFrameBytes = null,
    bool TreatDisconnectAsEndOfSequence = true);
```

Usage:

```csharp
var resilient = byteSource
    .AsTransceiver()
    .WithRetry(new RetryOptions(
        MaxAttempts: 5,
        Delay: TimeSpan.FromMilliseconds(100),
        ShouldRetry: ex => ex is TimeoutException));
```

And:

```csharp
await foreach (var frame in transceiver.ReceiveFrames(
    detectFrame,
    new FrameStreamOptions(MaxFrameBytes: 1024),
    ct))
{
    ...
}
```

This pattern is more versionable, more self-documenting, and easier to extend than a family of overloads.

### DEC-003: Add lightweight stream combinator extensions above `ReceiveFrames`

If `ReceiveFrames` becomes a first-class primitive, the library should also expose a small set of opinionated, dependency-free combinators that make frame pipelines ergonomic without requiring Rx.

Example:

```csharp
public static class FrameStreamExtensions
{
    public static async IAsyncEnumerable<T> SelectFrames<T>(
        this IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        Func<ReadOnlyMemory<byte>, T> selector)
    {
        await foreach (var frame in frames)
        {
            yield return selector(frame);
        }
    }

    public static async IAsyncEnumerable<T> ChooseFrames<T>(
        this IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        Func<ReadOnlyMemory<byte>, T?> selector)
        where T : class
    {
        await foreach (var frame in frames)
        {
            var value = selector(frame);
            if (value is not null)
            {
                yield return value;
            }
        }
    }
}
```

Usage:

```csharp
await foreach (var hex in transceiver
    .ReceiveFrames(detectFrame, ct)
    .SelectFrames(frame => Convert.ToHexString(frame.Span)))
{
    Console.WriteLine(hex);
}
```

And:

```csharp
await foreach (var packet in transceiver
    .ReceiveFrames(detectFrame, ct)
    .ChooseFrames(TryParsePacket))
{
    Process(packet);
}
```

This keeps the library expression-oriented and pipeline-friendly.

### DEC-004: Add protocol-adapter helpers that preserve composition transparency

Protocol clients should feel like views over a transport session, not owners of a transport resource. To reinforce that shape, protocol adapters may expose their underlying transport in a transparent but non-owning way.

Example:

```csharp
public interface IProtocolClient<out TTransport>
{
    TTransport Transport { get; }
}
```

Example protocol client:

```csharp
public interface IModbusClient : IProtocolClient<ITransceiver>
{
    Task<ReadHoldingRegistersResult> TryReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);
}
```

Usage:

```csharp
var client = byteSource
    .AsTransceiver()
    .WithRetry(new RetryOptions(MaxAttempts: 3))
    .AsModbusClient();

Console.WriteLine(client.Transport.GetType().Name);
```

This improves diagnostics and preserves the intuition that protocol clients are adapters, not transport owners.

### DEC-005: Add session-scoped cancellation/liveness accessors

If `BeginSession()` is the primary scoped-lifetime primitive, the session should expose a canonical cancellation/liveness token so nested adapters do not need to thread duplicate tokens independently.

Example:

```csharp
public interface ITransceiverSession : ITransceiver, IAsyncDisposable
{
    CancellationToken SessionCancellation { get; }
}
```

Usage:

```csharp
await using var session = byteSource.BeginSession(connectionToken);

var receiveTask = Task.Run(async () =>
{
    await foreach (var frame in session.ReceiveFrames(detectFrame, session.SessionCancellation))
    {
        Console.WriteLine($"Received {frame.Length} bytes");
    }
});
```

This helps decorators, adapters, and internal loops converge on one session lifetime source.

### DEC-006: Add observer-style diagnostics hooks as an alternative to event proliferation

ADR-0012 correctly avoided `DataReceived` as a competing consumption path. That decision should stand. However, there is still value in a non-invasive diagnostics observer pattern for logging, metrics, and tests.

Example:

```csharp
public interface ITransportObserver
{
    void OnSend(ReadOnlyMemory<byte> payload);
    void OnReceive(ReadOnlyMemory<byte> payload);
    void OnRetry(Exception exception, int attempt);
    void OnTransportFault(Exception exception);
}
```

Decorator helper:

```csharp
public static ITransceiver WithObserver(
    this ITransceiver transceiver,
    ITransportObserver observer);
```

Usage:

```csharp
var observed = byteSource
    .AsTransceiver()
    .WithObserver(new ConsoleTransportObserver())
    .WithRetry(new RetryOptions(MaxAttempts: 3));
```

This gives diagnostics a first-class home without turning protocol consumption into an event-driven API.

### DEC-007: Standardize paired throwing and non-throwing protocol methods

Keep `ITransceiver` exception-oriented, as ADR-0012 already argues. But at the protocol layer, encourage paired APIs so callers can choose either exception-first ergonomics or explicit result modeling.

Example:

```csharp
public interface IModbusClient
{
    Task<ReadHoldingRegistersResponse> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);

    Task<ReadHoldingRegistersResult> TryReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);
}
```

Usage:

```csharp
var result = await client.TryReadHoldingRegistersAsync(1, 0, 10, ct);

var text = result switch
{
    ReadHoldingRegistersSuccess success => $"Received {success.Registers.Length} registers",
    ReadHoldingRegistersTimeout => "Timed out",
    ReadHoldingRegistersDisconnected => "Disconnected",
    ReadHoldingRegistersProtocolError error => $"Protocol error: {error.Code}",
    _ => "Unknown result"
};
```

This keeps high-level application code flexible without weakening the transport boundary.

### DEC-008: Shape discriminated-union results for excellent pattern matching

When adopting record hierarchies at the protocol layer, prefer semantically concrete case names rather than excessively generic binary result wrappers.

Example:

```csharp
public abstract record ReadHoldingRegistersResult;

public sealed record ReadHoldingRegistersSuccess(ushort[] Registers)
    : ReadHoldingRegistersResult;

public sealed record ReadHoldingRegistersTimeout
    : ReadHoldingRegistersResult;

public sealed record ReadHoldingRegistersDisconnected
    : ReadHoldingRegistersResult;

public sealed record ReadHoldingRegistersProtocolError(byte ExceptionCode)
    : ReadHoldingRegistersResult;

public sealed record ReadHoldingRegistersTransportFailure(Exception Cause)
    : ReadHoldingRegistersResult;
```

Usage:

```csharp
return result switch
{
    ReadHoldingRegistersSuccess(var registers) => registers.Length.ToString(),
    ReadHoldingRegistersTimeout => "timeout",
    ReadHoldingRegistersDisconnected => "disconnected",
    ReadHoldingRegistersProtocolError(var code) => $"protocol error {code}",
    ReadHoldingRegistersTransportFailure(var ex) => ex.Message,
    _ => "unknown"
};
```

This improves call-site readability and makes the protocol domain obvious.

### DEC-009: Add tiny functional pipeline helpers where they improve readability

Do not turn the library into a functional-programming framework, but allow a few carefully chosen pipeline helpers to make composition chains easier to read when branching or custom mapping is involved.

Example:

```csharp
public static class PipelineExtensions
{
    public static TResult Pipe<TSource, TResult>(
        this TSource source,
        Func<TSource, TResult> selector)
        => selector(source);
}
```

Usage:

```csharp
var client = byteSource
    .AsTransceiver()
    .WithRetry(new RetryOptions(MaxAttempts: 3))
    .Pipe(t => configuration.EnableLogging ? t.WithLogging(logger) : t)
    .AsModbusClient();
```

This is intentionally small and should remain optional.

## Rationale

### 1. Capability interfaces scale better than ever-wider base abstractions

ADR-0012 already moved the design toward composition. Capability interfaces continue that direction by keeping `ITransceiver` focused while allowing richer wrappers to declare richer contracts.

### 2. Options records age better than overload families

As decorators and stream helpers grow, overloads become noisy and difficult to document. Small immutable option records make APIs easier to evolve.

### 3. Streaming deserves a small standard library

Once `ReceiveFrames` exists, callers will immediately want to map, filter, and decode frame streams. Providing a tiny, dependency-free combinator layer keeps the ecosystem cohesive and ergonomic.

### 4. Protocol adapters should remain transparent views

The more composition-oriented the library becomes, the more important it is that wrappers and protocol clients remain visibly non-owning. Surfacing the underlying transport is a pragmatic way to preserve that mental model.

### 5. Diagnostics need a home that does not distort the consumption model

Observer hooks provide visibility without adding competing receive paths or requiring heavyweight reactive dependencies.

### 6. Protocol layers benefit from both explicit and exception-first styles

Some callers want domain-result exhaustiveness; others want a concise throwing path. Supporting both intentionally makes protocol APIs more broadly useful.

## Consequences

### Positive

- **POS-001**: The library becomes easier to extend without broadening core interfaces too aggressively.
- **POS-002**: Decorators and wrappers become more discoverable and more type-safe.
- **POS-003**: Async-stream-based receive pipelines become easier to read and author.
- **POS-004**: Protocol clients gain clearer conventions for results, diagnostics, and session-lifetime integration.
- **POS-005**: Advanced users can inspect and compose wrappers without losing transparency.

### Negative

- **NEG-001**: More patterns mean more documentation burden and more naming discipline is required.
- **NEG-002**: Capability interfaces can become fragmented if introduced carelessly.
- **NEG-003**: Observer APIs must be clearly documented as diagnostics-only, not consumption APIs.
- **NEG-004**: Paired throwing/non-throwing protocol methods increase surface area.

## Alternatives Considered

### Keep ADR-0012 as the sole modernization ADR

- **ALT-001**: Stop at extension chains, sessions, streams, and result unions.
- **ALT-002**: Rejected because ADR-0012 establishes the direction but leaves several natural follow-on patterns undocumented.

### Introduce all optional features directly on `ITransceiver`

- **ALT-003**: Add diagnostics, connection state, streaming controls, and more directly to the base interface.
- **ALT-004**: Rejected because this would recreate the interface-bloat problem composition is meant to avoid.

### Adopt Rx/`IObservable<T>` for advanced streaming transformations

- **ALT-005**: Standardize advanced stream composition on reactive extensions.
- **ALT-006**: Rejected because the library has explicitly favored lighter-weight, dependency-averse designs and `IAsyncEnumerable<T>` remains the better core primitive.

## Implementation Notes

- **IMP-001**: Capability interfaces should be additive and narrow; they should not duplicate the whole transport contract.
- **IMP-002**: Option records should be preferred for new decorators and advanced stream APIs, but existing simple overloads may remain for convenience.
- **IMP-003**: Frame combinators should live in a small extension class and remain free of third-party dependencies.
- **IMP-004**: Observer hooks should be implemented as decorators over `ITransceiver`, consistent with ADR-0012.
- **IMP-005**: Session-scoped cancellation should flow from the same lifetime boundary introduced by `BeginSession()`.
- **IMP-006**: Protocol result naming should favor domain-specific concrete cases over generic `Success`/`Failure` wrappers when practical.

## References

- [ADR-0005](adr-0005-result-types-for-top-level-itransceiver-api.md)
- [ADR-0007](adr-0007-byte-source-abstraction-and-transceiver-layering.md)
- [ADR-0008](adr-0008-transceiver-lifecycle-observability.md)
- [ADR-0010](adr-0010-ibytesource-public-bridge-and-delegate-composition.md)
- [ADR-0011](adr-0011-remove-lifecycle-ownership-from-transceiver.md)
- [ADR-0012](adr-0012-composition-oriented-api-shapes.md)
