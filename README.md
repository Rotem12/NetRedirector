# NetRedirector

NetRedirector is a small Windows tray utility that forwards traffic from one local endpoint to another with minimal buffering. It is useful for moving RTP/video multicast, UDP telemetry, TCP streams, or serial data between adapters, ports, and devices.

## Supported endpoints

- UDP source and destination, including IPv4 multicast.
- TCP client source/destination with automatic reconnects.
- TCP server source, accepting multiple clients and feeding one redirect.
- TCP server destination, serving the latest connected client.
- Serial source/destination using raw 8-N-1 bytes with a selectable baud rate.

UDP datagrams stay datagrams. TCP and serial data are forwarded as raw byte chunks; they do not add framing or modify payloads.

## Multicast and multiple interfaces

The interface list is deliberately explicit. Leave it blank to use every active non-loopback IPv4 adapter, or check one or more adapters. A multicast source joins the group separately on every checked adapter. A multicast destination sends each datagram separately through every checked adapter. This supports machines with multiple physical NICs, VPNs, and capture/egress networks.

The list displays the local IPv4 address used for the membership or egress operation. Refresh it after connecting or disconnecting an adapter.

## Use

1. Run `NetRedirector.exe`; it starts in the notification area.
2. Left-click the tray icon to open the compact settings window.
3. Select the source and target protocol, address/port, or serial port.
4. For multicast, enter the group such as `239.10.10.10` and choose the required interfaces.
5. Click **Start redirect**. The activity pane shows endpoint state and byte/packet counters.

Settings are saved under `%APPDATA%\NetRedirector\settings.json` when a redirect is started. Closing the window keeps the app in the tray; use the tray menu's **Exit** command to quit.

## Build and test

```powershell
dotnet restore .\src\NetRedirector\NetRedirector.csproj
dotnet build .\src\NetRedirector\NetRedirector.csproj -c Release --no-restore
dotnet run --project .\tests\NetRedirector.Tests\NetRedirector.Tests.csproj -c Debug
```

The executable test runner exercises UDP→UDP, TCP client→UDP, TCP server→UDP, UDP→TCP server, and multicast→UDP transfers on the local machine. Serial transport requires two available COM endpoints or a hardware/virtual serial pair and is therefore compile-checked but not fabricated by the automated test runner.

For a portable framework-dependent release build:

```powershell
dotnet publish .\src\NetRedirector\NetRedirector.csproj -c Release -r win-x64 --self-contained false
```

## Repository layout

- `src/NetRedirector.Core` — protocol adapters and forwarding service.
- `src/NetRedirector` — WinForms tray application.
- `tests/NetRedirector.Tests` — executable loopback and multicast transfer checks.
