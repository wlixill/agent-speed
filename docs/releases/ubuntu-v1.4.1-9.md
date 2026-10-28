# Agent Speed Ubuntu 22.04 x64 — 1.4.1-9

本 fork 的首个 Ubuntu Release，适用于 **Ubuntu 22.04 x64（amd64）/ GNOME / X11**。

- 包含发光表盘、13 档载具动画、实时曲线、独立悬浮窗、托盘，以及来源／模型用量历史和 CSV 导出。
- 默认启用 Codex，可在设置中配置其他支持的本地来源。
- 包含 .NET 运行时、应用菜单入口、图标和第三方许可证；无需安装 .NET SDK。
- 本次包修订同步新仓库地址与下载说明，运行功能沿用 1.4.1-8。
- 上报需要每台机器单独配置并同意；旧雷达服务可明确选择 Windows 兼容标识。安装包不包含个人身份、密钥或用量数据。

下载本页 Assets 中的 `agent-speed_1.4.1-9_amd64.deb` 和同名 `.sha256` 文件，放在同一目录后执行：

```sh
sha256sum -c agent-speed_1.4.1-9_amd64.deb.sha256
sudo apt install ./agent-speed_1.4.1-9_amd64.deb
```

安装后从应用菜单搜索 **Agent Speed**，或执行 `agent-speed --dashboard`。升级保留已有设置和历史。登录时选择 **Ubuntu on Xorg**，托盘需要启用 Ubuntu AppIndicators 扩展。

[中文安装与功能说明](https://github.com/wlixill/agent-speed-ubuntu22/blob/main/README.zh-CN.md) · [English README](https://github.com/wlixill/agent-speed-ubuntu22#readme)

This is the first Ubuntu release of this fork, targeting **Ubuntu 22.04 amd64 / GNOME / X11**. Download the `.deb` and its `.sha256` file, verify the checksum, and install with the commands above. The .NET runtime is included. Launch **Agent Speed** from the application menu. This packaging revision updates repository links and documentation while retaining the runtime features of 1.4.1-8. Wayland and ARM64 remain unvalidated.
