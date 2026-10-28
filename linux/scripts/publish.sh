#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
rid="${1:-linux-x64}"
case "$rid" in linux-x64|linux-arm64) ;; *) echo "Usage: $0 [linux-x64|linux-arm64]" >&2; exit 2 ;; esac
version="$(cat "$repo_root/VERSION")"
output="$repo_root/dist/AgentSpeed-Ubuntu-$rid"
dotnet publish "$repo_root/linux/src/AgentSpeed.Linux" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version="$version" \
    --artifacts-path "$repo_root/artifacts/linux-publish" -o "$output"
cp "$repo_root/LICENSE" "$output/LICENSE"
cp "$repo_root/linux/README.md" "$output/README.md"
cp "$repo_root/assets/icon/icon-windows-256.png" "$output/agent-speed.png"
cat > "$output/launch.sh" <<'LAUNCH'
#!/bin/sh
set -eu
app_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$app_dir/AgentSpeed.Linux" --dashboard "$@"
LAUNCH
chmod +x "$output/AgentSpeed.Linux" "$output/launch.sh"
tar -czf "$repo_root/dist/AgentSpeed-Ubuntu-$rid.tar.gz" -C "$repo_root/dist" "$(basename "$output")"
echo "Ready: $output/AgentSpeed.Linux"
