# Contributing

Thanks for your interest. This is a small library maintained by one person, so please open an issue
before starting anything substantial — it saves you from building something that does not fit.

## Building

```
dotnet build CallAndResponse.slnx
dotnet test CallAndResponse.slnx
```

All projects target `net8.0`, but you need the **9.0.200 SDK or later** to build: the `.slnx` solution
format is not understood by older SDKs. CI uses 10.0.
No hardware is needed — the test suite runs entirely against in-memory pipes.

## What fits

The library is **framing and protocol logic**. It never opens, closes, connects, or disconnects
anything, and `ITransceiver` has no lifecycle members. See
[ADR-0011](docs/adr/adr-0011-remove-lifecycle-ownership-from-transceiver.md) and
[ADR-0015](docs/adr/adr-0015-duplex-pipe-transport-seam.md). A change that gives the library ownership
of a resource is unlikely to be accepted.

Device discovery is deliberately out of scope. See
[ADR-0009](docs/adr/adr-0009-device-discovery-out-of-scope.md).

**New protocols** are welcome as separate packages that depend only on the core package and accept
`ITransceiver` by constructor injection.

**New transports** usually need no code here at all. The transport seam is
`System.IO.Pipelines.IDuplexPipe`, so anything reachable through `PipeReader.Create(stream)` already
works. A dedicated package is worth it only when the adaptation is non-trivial — a background pump, a
framing quirk, a vendor SDK that is not stream-shaped. `SerialDuplexPipe` is the worked example.

## Tests

New behaviour needs a test. The suite uses xUnit with `FluentAssertions` and `NSubstitute`.

`FluentAssertions` is pinned to `6.12.2` deliberately: version 8 moved to a paid commercial licence.
Please do not bump it.

Prefer testing against a fake `IDuplexPipe` over mocking `ITransceiver` — the framing logic is the part
worth covering, and it lives below that interface.

## Architecture decisions

Non-trivial design changes get an ADR in [`docs/adr/`](docs/adr/README.md). Follow the existing format:
frontmatter, numbered `CTX`/`DEC`/`POS`/`NEG`/`ALT` items, and an explicit status. If a change
supersedes an earlier record, say so in both files.

Write the ADR when the decision is made, not months later. ADR-0015 is what happens otherwise.

## Pull requests

- One logical change per PR.
- Keep the existing code style; there is no formatter config, so match the surrounding file.
- Make sure `dotnet test CallAndResponse.slnx` passes before opening the PR.
- Note any breaking public API change explicitly in the description.

## Releasing

Releases are cut by pushing a `v*` tag. `MinVer` derives the package version from that tag, so an
untagged build produces a `0.0.0-alpha.0`-shaped version rather than a release one.

There is currently no publish automation. The nuget.org workflow was removed pending a rebuild on
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), which replaces the
long-lived API key with short-lived OIDC credentials. Until that lands, packing and publishing is manual
and maintainer-only.

## Licence

By contributing you agree that your contributions are licensed under the [MIT Licence](LICENSE).
