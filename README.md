<div align="center">

# agent-speed-ubuntu22

**Agent Speed for Ubuntu 22.04 x64 · GNOME / X11**

Live token gauges, animated vehicles, floating orbs, a tray indicator and usage history for local AI tools.

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

[MIT License](LICENSE)

</div>

This Ubuntu fork of [zoefix/agent-speed](https://github.com/zoefix/agent-speed) uses Avalonia and the upstream .NET measurement/presentation libraries. The application and Debian package are named **Agent Speed** / `agent-speed`.

## Install a Debian package

The current package revision is **1.4.1-8**. Build a package using the instructions below, or obtain a matching `.deb` and checksum from its distributor. Compiled packages are excluded from the Git source tree; Ubuntu packages have not yet been published in this fork's GitHub Releases.

From the directory containing the package and checksum:

```sh
sha256sum -c agent-speed_1.4.1-8_amd64.deb.sha256
sudo apt install ./agent-speed_1.4.1-8_amd64.deb
```

Search **Agent Speed** in the application menu, or run:

```sh
agent-speed --dashboard
```

The package includes the .NET runtime. APT installs missing system dependencies. Use **Ubuntu on Xorg** at login; the GNOME AppIndicators extension is needed for tray icons. To uninstall, run `sudo apt remove agent-speed`; user settings and history are retained.

## Ubuntu features

- **Live dashboard:** glowing gauges, 13 animated vehicle tiers, 1/2/5-minute charts, source speeds and agent/sub-agent activity. Switch between output speed and input-plus-output throughput.
- **Independent floating orbs:** five styles, up to eight windows, per-orb sources/readouts, size, opacity, click-through, screen placement, snapping, split/merge and undo. Nine title presets and custom titles are available.
- **Tray and startup:** numeric speed/cost indicators, optional traffic indicators, theme/effect controls, background startup and opt-in login startup.
- **Usage and model history:** daily charts, source/model breakdowns, date filters, weighted measured speed, input/cache/output tokens, execution modes, cost, traffic coverage and CSV export. Older values that cannot be recovered remain unknown.
- **Sources and costs:** Codex is enabled by default. Other supported parsers and log directories can be configured. API-equivalent costs support 38 currencies and subscription exclusions; public price/rate refresh is opt-in.
- **Optional network traffic:** basic TCP sampling, plus an explicitly authorized enhanced TCP/UDP helper. Coverage, attribution limits and requirements are explained in the [Linux guide](linux/README.md).
- **Optional reporting:** signed registration and reports, receiver-dependent profile/device features, diagnostics and an explicit Windows compatibility choice for receivers that reject Linux.
- **Debian updates:** manual packages or a configured HTTPS update feed with SHA-256/package checks. Automatic checks are off by default; no public Ubuntu update feed is configured.

The shared parsers cover Codex, Claude Code, Claude Desktop agent logs, DSH, OpenCode, Gemini CLI, omp, Antigravity, WorkBuddy, WorkBuddy AI, ZCode, Kimi Code CLI and Grok Build. Only enabled local sources are scanned. Parser availability does not establish that every tool/version has been tested on Ubuntu.

## What the speed means

Measured speed is **output tokens divided by the model response duration**, including time to first token. Parallel activity is combined by the shared engine. While a response is streaming, the app may show a recent measured-speed estimate marked `≈`; a completed response corrects it. Logs do not provide a timestamp for every visible token, so this is not an exact measurement of text appearing on screen.

Unknown values are shown as `—`; partial costs use `≥`. API-equivalent costs are estimates, not subscription bills. See [measurement semantics](spec/METRICS.md) and the [Linux guide](linux/README.md).

## Codex Radar compatibility

Open the reporting window from the tray, configure the HTTPS receiver, review sharing choices and explicitly enable reporting. For a receiver that does not accept `client.platform=Linux`, choose **Windows（兼容旧服务）** under **上报平台标识** and apply with **同意并开始上报**.

This advertises Windows 10 only to that reporting service. Device ID, signing key, nickname and sharing choices are retained, including across restarts. Reporting starts with new activity after enablement; older local history is not backfilled. Each computer needs its own configuration and consent.

## Build from source

Use Ubuntu 22.04 x64 with the **.NET 10 SDK** available on `PATH`. Install native packaging tools:

```sh
sudo apt update
sudo apt install -y clang-14 gcc make pkg-config libelf-dev zlib1g-dev linux-libc-dev python3 dpkg-dev desktop-file-utils

git clone https://github.com/wlixill/agent-speed-ubuntu22.git
cd agent-speed-ubuntu22

dotnet test windows/tests/AgentSpeed.Core.Tests -c Release
dotnet test windows/tests/AgentSpeed.Presentation.Tests -c Release
dotnet test linux/tests/AgentSpeed.Linux.Tests -c Release
bash linux/scripts/package-deb.sh
```

Outputs are `dist/agent-speed_1.4.1-8_amd64.deb`, its `.sha256` file and `dist/agent-speed-linux-release.json`. The package script builds the self-contained app and the optional traffic helper, with pinned libbpf source and licenses. A custom NuGet cache requires `NUGET_PACKAGES` to be set for license collection.

For development, use `dotnet run --project linux/src/AgentSpeed.Linux -- --dashboard`. To produce the portable directory/tarball, use `bash linux/scripts/publish.sh`. See the [Linux guide](linux/README.md) for isolated X11 smoke checks and package validation.

## Local data and network access

Settings live in `~/.config/agent-speed/settings.json`; usage history lives in `~/.local/share/agent-speed/history.sqlite`. XDG overrides and `--data-dir` are supported. Source logs are read without modification, and conversation content is not stored in the history database.

Application update checks, traffic collection, public price/rate refresh and reporting are off by default. Existing enabled preferences survive upgrades. Reporting sends the chosen metrics to the configured receiver; its credentials remain in the user's private data directory. Share source code and built packages separately from user data, credentials and diagnostic artifacts.

## Scope and upstream

The validated desktop target is **Ubuntu 22.04 x64 / GNOME / X11**. Wayland and ARM64 are not validated. Enhanced traffic requires compatible kernel BTF/kprobe/BPF support and system authentication; unsupported conditions retain the basic mode. There is no universal network master switch or full upstream feature parity yet.

| Path | Purpose |
| --- | --- |
| `linux/` | Ubuntu desktop app, tests, packaging and optional traffic helper |
| `windows/src/AgentSpeed.Core`, `windows/src/AgentSpeed.Presentation` | Shared measurement and presentation libraries reused by Ubuntu |
| `spec/` | Shared metrics, fixtures and protocol contracts |
| `macos/`, remaining `windows/` projects | Retained upstream platform implementations |

For macOS/Windows installers and original documentation, use [zoefix/agent-speed](https://github.com/zoefix/agent-speed). Original project by zoefix; this repository maintains the Ubuntu port. [MIT license](LICENSE); third-party notices are included in Debian builds.
