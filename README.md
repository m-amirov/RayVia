# Rayvia

Rayvia is a Windows proxy client built around Xray.

## Current version

`0.1.0`

The first version focuses on the basic desktop flow:

- add a subscription;
- select a server;
- connect through System Proxy;
- choose routing mode;
- add custom routing rules;
- inspect the local log;
- receive application updates from GitHub Releases.

Supported subscription entries:

- VLESS, including REALITY;
- VMess;
- Trojan;
- Shadowsocks.

## Routing

Three modes are available.

**Smart** — Russian IP ranges and sites are sent directly. Other traffic uses the selected proxy server.

**Proxy all** — traffic handled by the Windows system proxy uses the selected server.

**Direct all** — traffic handled by Rayvia is sent directly.

Custom rules are evaluated before the selected mode. Rules may contain a domain, `full:` / `domain:` / `regexp:` / `geosite:` expression, IP address, CIDR or `geoip:` expression.

## Xray core

Rayvia does not bundle Xray. On the first connection it downloads `Xray-windows-64.zip` from the latest official XTLS/Xray-core GitHub release and stores the required files in the user's local application data directory.

## Updates

On startup Rayvia checks the latest release in this repository. If a newer version exists and `Rayvia-Setup-x64.exe` is attached to the release, the installer is downloaded automatically. The application then shows an **Install** action.

Automatic checks can be disabled in Settings.

## Build

Requirements: .NET 10 SDK and Windows.

```powershell
dotnet build src/Rayvia/Rayvia.csproj -c Release
```

Self-contained application:

```powershell
dotnet publish src/Rayvia/Rayvia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Release

The `Release` GitHub Actions workflow builds the self-contained application, creates an Inno Setup installer and publishes `Rayvia-Setup-x64.exe` and `SHA256SUMS.txt`.

## Data

Settings: `%APPDATA%\Rayvia`

Xray and downloaded updates: `%LOCALAPPDATA%\Rayvia`

Subscription URLs can contain credentials. Do not publish `settings.json`.

## Not in 0.1.0

TUN mode, per-application routing, latency tests, server groups and live connections are planned after the initial System Proxy build is stable.
