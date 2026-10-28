# agent-speed-ubuntu22

**Ubuntu 22.04 x64 · GNOME / X11 版 Agent Speed**

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

本專案是 [zoefix/agent-speed](https://github.com/zoefix/agent-speed) 的 Ubuntu 移植版，提供即時 token 測速、發光儀表、載具動畫、獨立懸浮窗、托盤圖示與用量歷史。完整功能、資料位置與平台限制請參閱 [Linux 使用說明](linux/README.md) 或 [English README](README.md)。

## 安裝

目前 deb 修訂版為 **1.4.1-8**。可依下方指令從原始碼建置，或使用分發者提供的 deb 與校驗檔。本 fork 的 GitHub Releases 尚未發佈 Ubuntu 安裝包；編譯產物不放入 Git 原始碼目錄。

```sh
sha256sum -c agent-speed_1.4.1-8_amd64.deb.sha256
sudo apt install ./agent-speed_1.4.1-8_amd64.deb
agent-speed --dashboard
```

也可從應用程式選單搜尋 **Agent Speed**。安裝包自帶 .NET 執行階段；登入時選擇 **Ubuntu on Xorg**，托盤需要 GNOME AppIndicators 擴充。解除安裝使用 `sudo apt remove agent-speed`，設定與歷史保留。

## 功能與限制

- 五種儀表樣式、最多八個獨立懸浮窗、數字托盤、即時曲線、主題與動畫設定。
- 來源／模型／執行模式歷史、輸入／快取／輸出 token、加權平均速度、費用、流量與 CSV 匯出。
- 預設只啟用 Codex，其他共享解析器與日誌路徑可自行設定；不代表每種工具都已在 Ubuntu 實機驗證。
- 費用為 API 等值估算；價格／匯率更新、流量、上報及應用更新檢查預設關閉。更新使用自行設定的 HTTPS 清單或手動 deb。
- `≈` 表示生成中的參考速度估算，完成後依實測資料校正；未知值為 `—`，不能當作螢幕逐字輸出的精確速度。
- 已驗證目標為 Ubuntu 22.04 x64／GNOME／X11；Wayland、ARM64 及原版完整功能對等尚未驗證或完成。

## 上報相容性

在上報視窗設定 HTTPS 接收端並明確同意分享。若服務不接受 Linux，可在「上报平台标识」選擇 **Windows（兼容旧服务）**，按「同意并开始上报」套用。此選擇只變更該服務看到的平台，保留裝置 ID、金鑰、暱稱及分享設定。只上報啟用後的新活動，不補傳舊歷史；每台電腦須各自設定。

## 從原始碼建置

需要 Ubuntu 22.04 x64 與位於 `PATH` 的 **.NET 10 SDK**：

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

產物為 `dist/agent-speed_1.4.1-8_amd64.deb`、SHA-256 校驗檔與更新清單。使用 `bash linux/scripts/publish.sh` 可產生自帶執行階段的便攜目錄與 tar.gz。倉庫名稱為 `agent-speed-ubuntu22`，Debian 包與啟動指令為 `agent-speed`。

原始日誌以唯讀方式開啟，歷史庫不保存對話正文。使用者資料、上報憑據與診斷產物應保留在本機。原專案由 zoefix 創作，採用 [MIT 授權](LICENSE)；macOS／Windows 安裝方式請參考[上游倉庫](https://github.com/zoefix/agent-speed)。
