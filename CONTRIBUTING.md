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
- Every change a consumer can see gets an entry under `## Unreleased` in
  [CHANGELOG.md](CHANGELOG.md).
- A **breaking** change — a removed or changed signature, or a behaviour change at the same
  signature — additionally gets an entry under `## Unreleased` in
  [docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md): what changed, who it affects, and
  what to write instead. Note it in the PR description too, but the doc is what a consumer
  reads.

## Releasing

Releases are cut by pushing a `v*` tag. `MinVer` derives the package version from that tag, so an
untagged build produces a `0.0.0-alpha.0`-shaped version rather than a release one.

In the release commit, retitle the `## Unreleased` section of
[CHANGELOG.md](CHANGELOG.md) to `## 2.0.0-alpha.8 - <date>`, and the one in
[docs/BREAKING-CHANGES.md](docs/BREAKING-CHANGES.md) to
`` ## `v2.0.0-alpha.8` — since `v2.0.0-alpha.7` ``.

Every release gets a `CHANGELOG.md` section, including one that changes nothing a caller can
see. `docs/BREAKING-CHANGES.md` gets one only when something broke — which is why the
changelog is the doc the gate checks by name.

Tag **annotated**, with the release notes as the message body. The workflow creates no GitHub
Release, so the run summary is where those notes are read.

The `release-notes` job fails the release when either doc still says `Unreleased`, when
`CHANGELOG.md`'s newest heading is not the version being tagged, or when the tag is
lightweight or its message is empty. `publish` needs that job, so a refused tag builds and
publishes nothing. Fix it, delete the tag, and tag again:

```bash
git tag -d v2.0.0-alpha.8 && git push origin :refs/tags/v2.0.0-alpha.8
```

`.github/workflows/publish.yml` builds, tests, packs the four library projects, and pushes them to
nuget.org. It authenticates with
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) — GitHub issues a
short-lived OIDC token, nuget.org exchanges it for an API key valid for one hour. No long-lived key is
stored in this repository.

Only the maintainer can cut a release. The job runs in the `nuget.org` environment, and the nuget.org
policy is bound to this repository and to the filename `publish.yml`, so renaming that file stops
publishing until the policy is updated.

## Licence

By contributing you agree that your contributions are licensed under the [MIT Licence](LICENSE).
