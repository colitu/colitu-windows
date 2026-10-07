# Colitu VPN for Windows

[![Build](https://img.shields.io/github/actions/workflow/status/colitu/windows/build.yml?branch=main&style=flat-square&label=build&labelColor=101014)](https://github.com/colitu/windows/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/colitu/windows?style=flat-square&labelColor=101014&color=7c6cff)](https://github.com/colitu/windows/releases/latest)
[![License](https://img.shields.io/badge/license-GPL--3.0-7c6cff?style=flat-square&labelColor=101014)](LICENSE)
[![Colitu Network](https://img.shields.io/endpoint?url=https://status.colitu.com/api/github-badge/network&style=flat-square)](https://status.colitu.com)

**English** · [Русский](README.ru.md)

The open-source Windows desktop client of [Colitu VPN](https://colitu.com). A
single native window handles the account, locations and connection through the
Colitu API; underneath, a core layer runs Xray and sing-box and manages the
system proxy, TUN adapter, routing and DNS.

| | |
|---|---|
| App | `Colitu VPN` (`ColituVPN.exe`), WPF on .NET 10 |
| Version | `2.6.1` (`src/Directory.Build.props`) |
| OS | Windows 10 / 11, x64 |
| Languages | Russian, English, Turkish |
| Website | <https://colitu.com> · [downloads](https://colitu.com/downloads/windows) |
| License | [GPL-3.0](LICENSE) |

> A Colitu account is required to connect. The app has no hard-coded servers:
> the server list and connection profiles are issued per device by the Colitu API.

## Features

- **Automatic protocol selection.** Hysteria2 runs on a bundled
  [sing-box](https://github.com/SagerNet/sing-box) core; VLESS Reality, Trojan
  and Shadowsocks run on [Xray](https://github.com/XTLS/Xray-core). Candidates
  are tried in order until traffic actually flows, and the result is reported
  back so the service can rank protocols per network.
- **Kill switch** built on the Windows Filtering Platform, the same technique
  the WireGuard client uses. While it is on, only the VPN cores, the app,
  loopback, the TUN adapter, the local network and DHCP can reach the network.
  Filters live in a dynamic WFP session, so a crash can never leave the PC
  permanently offline.
- **One window for everything:** sign-in and registration, e-mail
  verification, locations, plans and checkout, account and devices, and a
  support inbox with ticket replies.
- **Secure token storage.** Tokens are encrypted with Windows DPAPI
  (`CurrentUser`); refresh tokens are single-use and rotated on every refresh.
- **Verified auto-update.** `latest.json` is signed offline (ECDSA P-256) and
  the app only accepts a manifest that verifies against its built-in public key.
  The installer is downloaded over HTTPS from `colitu.com` into a folder only
  administrators can write, and runs only when its SHA-256 matches the signed
  manifest.
- **Private logs.** The VPN cores log at warning level; lines about DNS lookups
  and connections are dropped and other host names masked, so the sites a user
  visits are never written to disk or sent with a support request. Session,
  config and log folders are readable by administrators only.

## How it connects

1. `POST /auth/login` (or `/auth/register`) returns an access/refresh token pair.
2. `POST /devices/register` creates the device; its `id` is sent as the
   `X-Device-ID` header on later requests.
3. `GET /me`, `/me/entitlement`, `/devices`, `/me/usage` and `/servers` fill the UI.
4. Picking a location calls `PUT /me/preferences`, then `GET /config?protocol=auto`
   returns the primary profile and alternatives.
5. The profiles are converted to `vless://`, `trojan://`, `ss://` and
   `hysteria2://` links, imported into the core layer (`src/ServiceLib`) and tried in order.
6. The outcome is sent to `POST /client/protocol-observations`.

The app never computes prices, eligibility or device limits itself; the server
is always the source of truth.

## Project layout

```
src/
  ColituVPN/              WPF app (ColituVPN.exe)
    Services/Colitu*.cs   API client, auth, VPN, kill switch, billing, support, updates, localization
    Views/ColituMainWindow*   the Colitu shell, one partial file per page
    Styles/               Colitu theme and brand resources
  ServiceLib/             core layer: config generation, routing, DNS, core processes
  AmazTool/               helper that swaps files during an update
  *.Tests/                unit tests (xUnit)
  ColituVPN.sln
xray-dosyalari/           Xray runtime files for the installer (xray.exe is not in git)
singbox-dosyalari/        sing-box runtime files for Hysteria2 (sing-box.exe is not in git)
installer/ColituVPN.iss   Inno Setup script
scripts/build-installer.ps1
design/                   UI design references
```

## Building

Requirements: Windows, the .NET 10 SDK with Windows Desktop, and
[Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
git clone https://github.com/colitu/windows.git
cd windows\src
dotnet build ColituVPN.sln -c Release
dotnet test ColituVPN.sln -c Release
```

### Installer

The core executables are not stored in git. Before packaging, download them
from the official releases and check their hashes:

- `xray.exe` from [Xray-core releases](https://github.com/XTLS/Xray-core/releases)
  (`Xray-windows-64.zip`) into `xray-dosyalari/`; the expected version and
  SHA-256 are in [`xray-dosyalari/README.md`](xray-dosyalari/README.md).
- `sing-box.exe` from [sing-box v1.14.2](https://github.com/SagerNet/sing-box/releases/tag/v1.14.2)
  (`windows-amd64`) into `singbox-dosyalari/`.

Then run:

```powershell
pwsh .\scripts\build-installer.ps1 -Version 2.6.1
```

The output in `artifacts/installer/` is `ColituVPN-Setup-<version>-x64.exe`, a
fixed-name copy `ColituVPN-Setup-x64.exe`, and `latest.json` for the updater.
The script signs `latest.json` with the release key given by `-SigningKeyPath`
(or `COLITU_UPDATE_SIGNING_KEY`); the key is never part of this repository, and
a manifest without a valid signature is ignored by the app.
Signing the installer with a code-signing certificate avoids the SmartScreen
warning.

### Pointing at another API

Create `guiConfigs/colitu-api.json` next to the app (an `https://` address;
debug builds also read the `COLITU_API_BASE_URL` environment variable):

```json
{ "apiBaseUrl": "https://staging.example.com/api/v1" }
```

## Security

If you find a vulnerability, please do not open a public issue. Write to
**support@colitu.com** with the details and we will get back to you.

## License

Colitu VPN for Windows is distributed under the
[GNU General Public License v3.0](LICENSE). It includes open-source components
(Xray-core, sing-box, Wintun and others) that keep their own licenses; see
[NOTICE](NOTICE) for the full list.

The "Colitu" name and logo are trademarks of COLITU LIMITED and are not covered by the
GPL. If you redistribute a modified version, please use your own name and
branding.
