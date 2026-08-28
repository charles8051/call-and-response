---
title: "ADR-0009: Device Discovery is Out of Scope for CallAndResponse Transports"
status: "Accepted"
date: "2026-06-10"
authors: "Repository maintainer"
tags: ["architecture", "decision", "discovery", "transports", "scope", "dependencies"]
supersedes: ""
superseded_by: ""
---

# ADR-0009: Device Discovery is Out of Scope for CallAndResponse Transports

## Status

**Accepted**

## Context

Several transport packages currently contain device discovery logic — code that locates the physical hardware before a connection is opened — alongside the I/O framing code that is the library's actual concern.

- **CTX-001**: `Source/CallAndResponse.Transport.Serial/SerialPortUtils.cs` contains a Windows-only WMI/CIM query (`CIM_SerialController`) that maps USB VID/PID pairs to COM port names. It requires `System.Management` as a package dependency and is inherently non-portable.
- **CTX-002**: An existing `TODO` comment in `SerialPortUtils.cs` and `TransceiverBuilderExtensions.cs` explicitly acknowledges the limitation: *"Figure out how to accommodate VID PID pairs on all platforms. CIM queries will only work on Windows."*
- **CTX-003**: `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` contains three categories of discovery code: bonded-device enumeration (iterating `adapter.BondedDevices` and inspecting their GATT service list), active scanning (a `Scan()` method that starts a BLE scan and parses raw 128-bit UUID advertisement records byte-by-byte to match the Nordic UART Service), and a `ScanConnectDevice(Guid)` method that scans for a specific device GUID. Together these account for roughly 100 lines of code that have nothing to do with byte framing.
- **CTX-004**: The no-argument `BleNordicUartTransceiver()` constructor is only meaningful when the transceiver is expected to discover a device itself. Without built-in discovery it is semantically empty.
- **CTX-005**: `Source/CallAndResponse.Transport.Treehopper/TreehopperTransceiver.cs` contains a static `Create()` factory method that calls `TreehopperManager.GetFirstDeviceAsync()` — another instance of discovery logic embedded in a transport.
- **CTX-006**: Each transport invents its own discovery mechanism using whichever platform API its third-party dependency exposes. There is no common pattern, no shared abstraction, and no cross-platform guarantee.
- **CTX-007**: Cross-platform device enumeration is a solved problem with existing libraries, backed by SetupAPI/cfgmgr32 on Windows, udev on Linux, and IOKit on macOS. The clean split is *discovery, not interaction*: one library tells you what is connected, and a protocol library like CallAndResponse talks to it once you have a handle.
- **CTX-008**: Every category of discovery that CallAndResponse transports attempted independently — USB VID/PID lookup, serial port enumeration, Bluetooth device scanning — is covered by dedicated discovery libraries with a single API surface across platforms. Reimplementing it here duplicates that work three platforms deep.
- **CTX-009**: The library targets `netstandard2.0` and `netstandard2.1` for broad reach. Baking cross-platform discovery into those TFMs would require either restricting platform support, accepting platform-specific guards throughout, or pulling in platform-specific dependencies that undermine the portability goal.
- **CTX-010**: Device discovery and device communication are fundamentally different concerns with different lifecycles. Discovery is a one-time lookup; communication is an ongoing I/O session. Conflating them produces transports whose `Open()` implementations perform both roles, which complicates testing, retry logic, and separation of application phases.

## Decision

Device discovery is explicitly out of scope for CallAndResponse and all of its transport packages. Transport implementations accept an already-resolved device identifier and perform I/O only.

- **DEC-001**: No transport package may contain code whose purpose is to enumerate, scan, or locate devices. This includes WMI queries, GATT service scans, advertisement record parsing, and OS-level device manager queries of any kind.
- **DEC-002**: Transport constructors and factory options accept an already-resolved identifier appropriate to the transport medium. For serial: a `PortName` string. For BLE: a device `Guid`. For Treehopper: a handle obtained externally. The transport's responsibility begins at connection establishment and ends at teardown.
- **DEC-003**: `SerialPortUtils.cs` is removed from `CallAndResponse.Transport.Serial`. The `System.Management` package reference is removed from `CallAndResponse.Transport.Serial.csproj`.
- **DEC-004**: The bonded-device enumeration loop, the `Scan()` method, and the `ScanConnectDevice(Guid)` method are removed from `BleNordicUartTransceiver`. The no-argument `BleNordicUartTransceiver()` constructor is removed. The `Guid`-accepting constructor remains; it is the correct public API.
- **DEC-005**: The static `Create()` discovery factory on `TreehopperTransceiver` is removed. Callers obtain a device handle externally and pass it to the transceiver constructor.
- **DEC-006**: Consumers are responsible for device discovery using whatever mechanism suits their platform and application — a dedicated cross-platform discovery library, `System.IO.Ports.SerialPort.GetPortNames()`, WMI, udev, or a hard-coded port name.
- **DEC-007**: Optional future bridging packages may compose a discovery library with CallAndResponse transport construction as a convenience layer. Such packages are separate NuGet packages with an explicit dependency on that library; they are never part of the core transport packages.

## Consequences

### Positive

- **POS-001**: Transport packages are simplified. Each transport is reduced to its single responsibility: open a connection to a known endpoint, send bytes, and receive bytes.
- **POS-002**: `System.Management` is removed as a dependency of `CallAndResponse.Transport.Serial`, eliminating a Windows-only package reference from a portable library.
- **POS-003**: The serial transport becomes genuinely cross-platform. There is no longer any Windows-only code path in library source.
- **POS-004**: Roughly 100 lines of BLE scanning and advertisement-parsing code are removed from `BleNordicUartTransceiver`, reducing the package's complexity and its exposure to Plugin.BLE API changes in the discovery path.
- **POS-005**: Discovery is now handled by a library purpose-built for it, with tested, cross-platform implementations for Windows, Linux, and macOS — capabilities that would take significant effort to replicate inside CallAndResponse transport packages.
- **POS-006**: The boundary between "find the device" and "talk to the device" is explicit and testable. Transports can be constructed with a known identifier in unit tests without mocking a discovery subsystem.
- **POS-007**: The library's stated scope is clear and easier to communicate: CallAndResponse frames bytes and implements protocols. Discovery is someone else's job.

### Negative

- **NEG-001**: Consumers who relied on `SerialPortUtils.FindPortNameById()` or `GetCp210xComPort()` must now perform VID/PID-to-port-name resolution themselves. This is a breaking removal.
- **NEG-002**: The no-argument `BleNordicUartTransceiver()` constructor is removed. Consumers who used it to rely on bonded-device auto-discovery must now obtain a device GUID themselves — from Plugin.BLE or a discovery library — before constructing the transport. This is a breaking removal.
- **NEG-003**: The `TreehopperTransceiver.Create()` factory is removed. Consumers must obtain a Treehopper device handle externally. This is a breaking removal.
- **NEG-004**: Consumers who need cross-platform serial port VID/PID lookup must implement their own resolution or use platform-specific approaches. The simplest migration path for the serial transport is to pass a known port name.

## Alternatives Considered

### Keep discovery in transports and improve cross-platform coverage

- **ALT-001**: **Description**: Retain discovery in each transport package and incrementally add Linux and macOS implementations to match `SerialPortUtils.cs` Windows behavior. Add similar cross-platform paths to BLE discovery.
- **ALT-002**: **Rejection Reason**: This recreates, inside CallAndResponse, what dedicated discovery libraries already provide. The cost is high (three platform backends per transport), the result is duplicated code, and the library would need to take on maintenance of OS-level P/Invoke for SetupAPI, udev, and IOKit. That is not this library's domain.

### Create discovery sub-packages within the CallAndResponse namespace

- **ALT-003**: **Description**: Extract discovery code into dedicated packages such as `CallAndResponse.Transport.Serial.Discovery` that consumers can opt into. Keep transport packages free of discovery but host the logic in the same repository.
- **ALT-004**: **Rejection Reason**: This still requires maintaining cross-platform discovery code in this repository. The maintenance burden remains and grows every time a new platform or device category needs support.

### Introduce an `IDeviceDiscovery` abstraction in the core package

- **ALT-005**: **Description**: Define an `IDeviceDiscovery` interface in `CallAndResponse` that transport packages consume. Discovery implementations could then be swapped in for testing or different platforms.
- **ALT-006**: **Rejection Reason**: Adding a discovery abstraction to the core would pull discovery into the library's conceptual model and public surface area. It would also need to be designed, documented, implemented, and maintained, when the same abstraction already exists externally at no cost to this library.

### Make discovery injectable but keep a default built-in implementation

- **ALT-007**: **Description**: Accept an optional `IDeviceDiscovery` or similar delegate in transport constructors, falling back to the current built-in discovery behavior when none is provided.
- **ALT-008**: **Rejection Reason**: Defaulting to built-in discovery implies the library is responsible for ensuring that default is correct and cross-platform, which is the exact responsibility being declined here. There is no safe default that works on all target platforms.

## Implementation Notes

- **IMP-001**: `SerialPortUtils.cs` and `CIMSerialControllerInfo` are deleted from `Source/CallAndResponse.Transport.Serial/`.
- **IMP-002**: The `<PackageReference Include="System.Management" .../>` entry is removed from `Source/CallAndResponse.Transport.Serial/CallAndResponse.Transport.Serial.csproj`.
- **IMP-003**: The bonded-device enumeration block, the `Scan()` method, and the `ScanConnectDevice(Guid)` method are removed from `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs`. The `OpenCore` implementation retains only the direct `ConnectToKnownDeviceAsync` path.
- **IMP-004**: The no-argument `BleNordicUartTransceiver()` constructor is removed. The public constructors are those that accept a `Guid` (with and without an `ILogger`).
- **IMP-005**: The `Create()` factory method is removed from `TreehopperTransceiver`. The constructor that accepts a device handle remains.
- **IMP-006**: `ARCHITECTURE.md` is updated to remove `SerialPortUtils (WMI VID/PID → COM port lookup)` from the `SerialPortTransceiver` description, remove `System.Management` from the package dependency table, and add a note under the Transport layer explaining that device discovery is out of scope and delegated to external tools.
- **IMP-007**: `README.md` is updated to remove any mention of `SerialPortUtils`, `FindPortNameById`, or bonded-device auto-discovery from examples and package descriptions.
- **IMP-008**: Consumers who need VID/PID-to-port resolution supply the port name directly, or resolve it with a discovery library of their choice before constructing the transport.
- **IMP-009**: The `ValidateOptions` check in `SerialTransceiverBuilderStage` currently enforces that `PortName` is non-null, which is the correct behavior after this change. No modification to that validation is required.

## References

- **REF-001**: `Source/CallAndResponse.Transport.Serial/SerialPortUtils.cs` — deleted per DEC-003
- **REF-002**: `Source/CallAndResponse.Transport.Serial/CallAndResponse.Transport.Serial.csproj` — `System.Management` reference removed per DEC-003
- **REF-003**: `Source/CallAndResponse.Transport.Serial/TransceiverBuilderExtensions.cs` — `TODO` comment confirming the Windows-only limitation of VID/PID lookup
- **REF-004**: `Source/CallAndResponse.Transport.Ble/BleNordicUartTransceiver.cs` — discovery methods removed per DEC-004
- **REF-005**: `Source/CallAndResponse.Transport.Treehopper/TreehopperTransceiver.cs` — `Create()` factory removed per DEC-005
- **REF-006**: `docs/ARCHITECTURE.md` — package dependency table and transport descriptions updated per IMP-006
- **REF-007**: `README.md` — updated per IMP-007

- **REF-009**: `docs/adr/adr-0003-serial-transport-revision.md` — serial transport reliability work, which this ADR complements by also simplifying the transport's responsibilities
