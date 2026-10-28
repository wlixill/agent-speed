# 发布 Ubuntu deb

Ubuntu 使用 `ubuntu-vVERSION-REVISION` 标签，例如 `ubuntu-v1.4.1-9`。标签中的版本必须与该提交的 `VERSION`、`linux/DEBIAN_REVISION` 一致，并包含对应的 `docs/releases/<标签>.md` 发布说明。

1. 使用本地 Git 提交版本和下载说明，推送对应标签。
2. 在本仓库 Actions 中运行 **Ubuntu release**，选择 `main` 工作流并填写已有的 Ubuntu 标签。
3. 工作流在 Ubuntu 22.04 amd64 上运行共享核心、展示层和 Linux 测试，构建自带运行时的 deb，检查包、APT 依赖及隔离 X11 启动。全部通过后，发布 deb 和 SHA-256 文件并设为最新 Release。
4. 从公开下载链接重新下载，核对 SHA-256、包版本、架构和 README 下载入口。

构建任务只有仓库读取权限。独立发布任务使用 GitHub 自动提供的本仓库临时 `contents: write` 权限，不需要个人令牌或账号级授权。工作流只接受已有的 Ubuntu 标签，不触发 macOS／Windows 发布流程。

已有草稿会复用，并替换同名草稿附件；已经公开的 Release 会拒绝覆盖，避免更换用户已下载版本的文件。需要更换已公开安装包时，增加 Debian 修订号并发布新标签。

Fork 的 Actions 默认可能关闭。首次启用后，该仓库已有的其他工作流也可以按自身触发条件运行；请在启用前核对 [工作流目录](../.github/workflows)。Ubuntu 专用流程定义见 [release-ubuntu.yml](../.github/workflows/release-ubuntu.yml)。
