<div align="center">

<img src="assets/icon/icon-1024.png" width="112" alt="Agent Speed 图标">

# agent-speed-ubuntu22

**面向 Ubuntu 22.04 x64 · GNOME / X11 的 Agent Speed**

实时 token 测速、发光表盘、载具动画、独立悬浮窗、托盘与用量历史。

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

![Ubuntu 22.04](https://img.shields.io/badge/Ubuntu-22.04-E95420?logo=ubuntu)
![Desktop](https://img.shields.io/badge/Desktop-GNOME%20%2F%20X11-blue)
![Architecture](https://img.shields.io/badge/Architecture-x64-blue)
[![License: MIT](https://img.shields.io/badge/License-MIT-green)](LICENSE)

</div>

本仓库是 [zoefix/agent-speed](https://github.com/zoefix/agent-speed) 的 Ubuntu 移植版，使用 Avalonia，复用原项目的 .NET 测速和展示核心。仓库名为 `agent-speed-ubuntu22`，应用名称为 **Agent Speed**，Debian 包及启动命令为 `agent-speed`。

## 安装 deb

当前包修订版为 **1.4.1-8**。可按下文从源码打包，或使用分发者提供的 `.deb` 与校验文件。编译产物不放入 Git 源码目录；本 fork 的 GitHub Releases 尚未发布 Ubuntu 安装包。

在安装包与校验文件所在目录执行：

```sh
sha256sum -c agent-speed_1.4.1-8_amd64.deb.sha256
sudo apt install ./agent-speed_1.4.1-8_amd64.deb
```

安装后，从应用菜单搜索 **Agent Speed** 启动，也可以运行：

```sh
agent-speed --dashboard
```

安装包自带 .NET 运行时，无需安装 SDK；APT 会解决缺少的系统依赖。登录时选择 **Ubuntu on Xorg**，托盘需要启用 GNOME AppIndicators 扩展。卸载使用 `sudo apt remove agent-speed`，用户设置和用量历史会保留。

## Ubuntu 版功能

- **实时仪表**：发光表盘、13 档载具动画、1／2／5 分钟曲线、各来源速度与 agent／子 agent 状态；支持输出速度和输入＋输出吞吐量。
- **独立悬浮窗**：LED、指针、液体、反应堆、极简五种样式，最多八窗；每窗可配置来源、读数、尺寸、透明度、穿透和位置，支持吸附、拆分、合并、撤销，以及九套称号与自定义称号。
- **托盘与启动**：数字速度／费用图标、可选上传／下载图标、主题和动画强度、后台启动及可选登录启动。
- **用量与模型历史**：每日图表、来源／模型明细、日期筛选、实测时间加权速度、输入／缓存／输出 token、执行模式、费用、流量覆盖标记和 CSV。无法恢复的旧值保留未知。
- **多来源与费用**：默认启用 Codex，可配置其他工具和日志路径。API 等值费用支持 38 种币种、订阅来源排除；公开价格和汇率更新需自行开启。
- **可选流量采集**：基础 TCP 采样，以及需系统认证的增强 TCP／UDP 辅助程序；覆盖范围、归属限制与内核要求见 [Linux 使用说明](linux/README.md)。
- **可选上报**：签名注册与报告、接收端支持时的头像／设备能力、请求诊断，以及需明确选择的 Windows 兼容标识。
- **deb 更新**：手动安装或配置 HTTPS 更新源，校验 SHA-256、包名、架构与版本。自动检查默认关闭，尚未配置公共 Ubuntu 更新服务。

共享解析器覆盖 Codex、Claude Code、Claude Desktop 的 agent 日志、DSH、OpenCode、Gemini CLI、omp、Antigravity、WorkBuddy、WorkBuddy AI、ZCode、Kimi Code CLI 与 Grok Build。只扫描已启用的本地来源；解析器可用不代表所有工具及版本都已在 Ubuntu 实机验证。

## 测速口径

实测速率按**输出 token ÷ 模型响应耗时**计算，包含首 token 等待，平行活动由共享核心汇总。生成中可能显示最近实测速率的参考估算，以 `≈` 标记，响应完成后再校正。现有日志不含每个可见 token 的时间戳，因此不能把估算当作屏幕逐字输出的精确速度。

未知值显示 `—`，部分已知费用显示 `≥`；费用为 API 等值估算，不代表订阅账单。完整定义见 [测速规范](spec/METRICS.md) 和 [Linux 使用说明](linux/README.md)。

## Codex Radar 上报

从托盘打开“上报、头像与设备”，配置 HTTPS 服务，查看分享选项并明确同意。若接收服务暂不接受 `client.platform=Linux`，可在“上报平台标识”中选择 **Windows（兼容旧服务）**，再点击“同意并开始上报”。

该选择仅对所选服务使用 Windows 10 标识；设备 ID、签名密钥、昵称及分享选项会保留，重启后继续采用。只上报开启后的新活动，不补传旧历史。每台机器都需要独立配置并同意，源码与安装包中不包含用户身份或密钥。

## 从源码构建

在 Ubuntu 22.04 x64 上准备好 **.NET 10 SDK**，确认 `dotnet` 位于 `PATH`，然后安装原生构建和打包工具：

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

生成 `dist/agent-speed_1.4.1-8_amd64.deb`、对应 `.sha256` 及 `dist/agent-speed-linux-release.json`。脚本会构建自带运行时的应用与可选流量辅助程序，包含固定版本的 libbpf 源码、校验和及许可证。使用自定义 NuGet 缓存时，应设置 `NUGET_PACKAGES`，供许可证收集使用。

开发运行：`dotnet run --project linux/src/AgentSpeed.Linux -- --dashboard`。便携目录和 tar.gz 使用 `bash linux/scripts/publish.sh` 生成。隔离 X11 界面验证及安装包检查见 [Linux 使用说明](linux/README.md)。

## 数据与网络

设置位于 `~/.config/agent-speed/settings.json`，用量历史位于 `~/.local/share/agent-speed/history.sqlite`；支持 XDG 路径覆盖和 `--data-dir`。程序只读来源日志，用量库不保存对话正文。

应用更新检查、流量采集、公开价格／汇率更新及上报默认关闭；升级保留用户已开启的选项。上报只向用户配置的服务发送已选择的指标，凭据保存在用户私有数据目录。分发源码和安装包时，应将个人数据、密钥与诊断产物留在本地。

## 平台范围与上游

已验证的桌面目标为 **Ubuntu 22.04 x64 / GNOME / X11**；Wayland 与 ARM64 尚未验证。增强流量采集依赖内核 BTF／kprobe／BPF 能力及系统认证，不可用时保留基础模式。目前尚无统一的全局网络总开关，也未达到原版全部功能对等。

| 目录 | 内容 |
| --- | --- |
| `linux/` | Ubuntu 应用、测试、打包脚本和可选流量辅助程序 |
| `windows/src/AgentSpeed.Core`、`windows/src/AgentSpeed.Presentation` | Ubuntu 复用的共享测速与展示核心 |
| `spec/` | 测速定义、样例与协议规范 |
| `macos/`、其余 `windows/` 工程 | 保留的上游平台实现 |

macOS／Windows 安装器与原始文档请访问 [zoefix/agent-speed](https://github.com/zoefix/agent-speed)。原项目由 zoefix 创作，本 fork 维护 Ubuntu 移植，遵循 [MIT 许可证](LICENSE)；第三方声明随 deb 打包。
