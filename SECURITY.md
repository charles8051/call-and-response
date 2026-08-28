# Security Policy

## Supported versions

Only the latest published version of each package receives fixes. There are no long-term support
branches.

| Package | Supported |
|---|---|
| `CallAndResponse` | Latest release |
| `CallAndResponse.Protocol.Modbus` | Latest release |
| `CallAndResponse.Protocol.Stm32Bootloader` | Latest release |
| `CallAndResponse.Transport.Serial` | Latest release |

## Reporting a vulnerability

Report privately through GitHub's
[private vulnerability reporting](https://github.com/charles8051/call-and-response/security/advisories/new).
Please do not open a public issue for a security problem.

Include what the issue is, which package and version, and how to reproduce it. A failing test or a
byte sequence that triggers the behaviour is the most useful thing you can send.

This is a single-maintainer project, so expect an initial response in days rather than hours.

## Threat model

The library parses bytes arriving from a device you chose to connect to. It does not authenticate the
peer, encrypt traffic, or validate that a device is what it claims to be. If your transport crosses a
trust boundary, that protection belongs in the transport, not here.

What counts as a vulnerability in this library:

- A frame-detection or parsing path that can be driven to an unbounded allocation, an infinite loop, or
  a crash by a malformed or hostile response.
- An out-of-bounds read or a buffer being handed to a caller with the wrong slice.
- A protocol client accepting a response it should have rejected — wrong unit identifier, wrong function
  code, bad CRC.

What does not:

- A device on the other end of the wire behaving maliciously in ways the protocol permits. Modbus RTU
  and the STM32 bootloader protocol have no authentication by design.
- Denial of service through a transport you own and configured, such as a serial port that never
  answers. Use a `CancellationToken`; every asynchronous method takes one.
