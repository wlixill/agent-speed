# agent-speed-ubuntu22

**Ubuntu 22.04 x64 · GNOME / X11 向け Agent Speed**

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · [日本語](README.ja.md)

[zoefix/agent-speed](https://github.com/zoefix/agent-speed) の Ubuntu 移植版です。リアルタイムのトークン速度、発光ゲージ、乗り物アニメーション、独立したフローティングオーブ、トレイ、使用履歴を提供します。詳しい機能と制約は [English README](README.md) と [Linux ガイド](linux/README.md) を参照してください。

## インストール

現在の deb リビジョンは **1.4.1-8** です。下の手順でビルドするか、配布者から deb とチェックサムを入手してください。この fork の GitHub Releases では Ubuntu パッケージをまだ公開していません。ビルド済みファイルは Git のソースツリーに含まれません。

```sh
sha256sum -c agent-speed_1.4.1-8_amd64.deb.sha256
sudo apt install ./agent-speed_1.4.1-8_amd64.deb
agent-speed --dashboard
```

アプリケーションメニューの **Agent Speed** からも起動できます。.NET ランタイムは同梱されています。ログイン時に **Ubuntu on Xorg** を選び、トレイには GNOME AppIndicators 拡張を有効にしてください。`sudo apt remove agent-speed` で削除でき、設定と履歴は保持されます。

## 機能と対象環境

- 5 種類のゲージ、最大 8 個の独立したオーブ、数値トレイ、ライブグラフ、テーマとアニメーション設定。
- ソース／モデル／実行モード別の履歴、入力／キャッシュ／出力トークン、時間で重み付けした実測速度、費用、通信量、CSV 出力。
- 初期設定は Codex のみ。他の共有パーサーとログパスは設定できますが、すべてのツールを Ubuntu 実機で検証したという意味ではありません。
- 費用は API 相当額の見積もりです。価格／為替更新、通信量計測、送信、アプリ更新チェックは初期設定で無効です。更新には設定した HTTPS フィードか手動 deb を使います。
- `≈` は生成中の参考速度の推定で、完了後に実測値で補正します。未知の値は `—` です。画面上の文字表示速度を正確に測るものではありません。
- 検証済みの対象は Ubuntu 22.04 x64／GNOME／X11 です。Wayland と ARM64 は未検証で、上流版の全機能が移植済みというわけではありません。

## 送信サービスとの互換性

HTTPS 受信サービスを設定し、共有内容を確認して明示的に送信を有効にします。Linux を受け付けないサービスでは、「上报平台标识」で **Windows（兼容旧服务）** を選び、「同意并开始上报」で適用できます。この設定は受信サービスへのプラットフォーム表記にだけ作用し、デバイス ID、署名鍵、ユーザー名、共有設定は保持されます。有効化後の新しい活動だけを送り、過去の履歴は送りません。各コンピューターで個別に設定してください。

## ソースからビルド

Ubuntu 22.04 x64 と、`PATH` 上の **.NET 10 SDK** が必要です。

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

`dist/agent-speed_1.4.1-8_amd64.deb`、SHA-256 チェックサム、更新フィードが生成されます。`bash linux/scripts/publish.sh` はランタイム同梱のポータブルディレクトリーと tar.gz を生成します。リポジトリ名は `agent-speed-ubuntu22`、Debian パッケージと起動コマンドは `agent-speed` です。

元のログは読み取り専用で、履歴データベースに会話本文を保存しません。個人のデータ、送信資格情報、診断出力はローカルに保持してください。原作は zoefix、ライセンスは [MIT](LICENSE) です。macOS／Windows のインストール方法は[上流リポジトリ](https://github.com/zoefix/agent-speed)を参照してください。
