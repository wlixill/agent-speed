#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
app_version="$(cat "$repo_root/VERSION")"
deb_version="${app_version}-$(cat "$repo_root/linux/DEBIAN_REVISION")"
publish_dir=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --publish-dir) [ "$#" -ge 2 ] || exit 2; publish_dir="$(realpath -- "$2")"; shift 2 ;;
        --deb-version) [ "$#" -ge 2 ] || exit 2; deb_version="$2"; shift 2 ;;
        -h|--help)
            echo "Usage: $0 [--publish-dir EXISTING_SELF_CONTAINED_BUILD] [--deb-version VERSION]"
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done
for dependency in dpkg-deb dpkg python3 desktop-file-validate; do
    command -v "$dependency" >/dev/null || { echo "Missing build tool: $dependency" >&2; exit 1; }
done
dpkg --validate-version "$deb_version"
mkdir -p "$repo_root/artifacts/linux-deb" "$repo_root/dist"
build_dir="$(mktemp -d "$repo_root/artifacts/linux-deb/package.XXXXXX")"
if [ -z "$publish_dir" ]; then
    publish_dir="$build_dir/publish"
    # Publish separately from the portable build, which may already be running on this computer.
    dotnet publish "$repo_root/linux/src/AgentSpeed.Linux" -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version="$app_version" \
        --artifacts-path "$repo_root/artifacts/linux-deb/dotnet" -o "$publish_dir"
fi
python3 - "$publish_dir" "$app_version" <<'PY'
import json, pathlib, struct, sys
root = pathlib.Path(sys.argv[1])
header = (root / 'AgentSpeed.Linux').read_bytes()[:20]
if header[:6] != b'\x7fELF\x02\x01' or struct.unpack('<H', header[18:20])[0] != 62:
    raise SystemExit('The Debian package requires an x86-64 Linux executable')
for name in ['libcoreclr.so', 'libhostfxr.so', 'libSkiaSharp.so', 'AgentSpeed.Linux.dll']:
    if not (root / name).is_file():
        raise SystemExit('Incomplete self-contained publish directory: missing ' + name)
dependencies = json.loads((root / 'AgentSpeed.Linux.deps.json').read_text())['libraries']
if 'AgentSpeed.Linux/' + sys.argv[2] not in dependencies:
    raise SystemExit('Publish version does not match VERSION; rebuild the application')
PY
stage="$build_dir/root"
app_dir="$stage/opt/agent-speed"
doc_dir="$stage/usr/share/doc/agent-speed"
mkdir -p "$stage/DEBIAN" "$app_dir" "$doc_dir" "$stage/usr/bin" \
    "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/256x256/apps"
cp -a -- "$publish_dir/." "$app_dir/"
helper_build="$build_dir/traffic-helper"
bash "$repo_root/linux/traffic-helper/build.sh" "$helper_build"
mkdir -p "$app_dir/traffic" "$stage/usr/share/polkit-1/actions" "$doc_dir/traffic-helper"
install -m 0755 "$helper_build/agent-speed-traffic-helper" "$app_dir/traffic/agent-speed-traffic-helper"
install -m 0644 "$helper_build/traffic.bpf.o" "$app_dir/traffic/traffic.bpf.o"
install -m 0644 "$repo_root/linux/traffic-helper/org.agentspeed.traffic.policy" "$stage/usr/share/polkit-1/actions/org.agentspeed.traffic.policy"
install -m 0644 "$repo_root/linux/traffic-helper/traffic.bpf.c" "$repo_root/linux/traffic-helper/traffic-helper.c" "$doc_dir/traffic-helper/"
install -m 0644 "$repo_root/linux/traffic-helper/README.md" "$repo_root/linux/traffic-helper/build.sh" "$doc_dir/traffic-helper/"
mkdir -p "$doc_dir/notices/libbpf"
install -m 0644 "$repo_root/linux/traffic-helper/vendor/NOTICE" "$repo_root/linux/traffic-helper/vendor/LIBBPF-LICENSE" \
    "$repo_root/linux/traffic-helper/vendor/SHA256SUMS" "$repo_root/linux/traffic-helper/vendor/libbpf-v1.4.7.tar.gz" "$doc_dir/notices/libbpf/"
python3 - "$app_dir/build-info.json" "$deb_version" <<'PY'
import json, pathlib, sys
pathlib.Path(sys.argv[1]).write_text(json.dumps({'debianVersion': sys.argv[2]}) + '\n')
PY
rm -f -- "$app_dir/"*.pdb "$app_dir/launch.sh"
install -m 0755 "$repo_root/linux/packaging/agent-speed" "$stage/usr/bin/agent-speed"
install -m 0644 "$repo_root/linux/packaging/agent-speed.desktop" "$stage/usr/share/applications/agent-speed.desktop"
install -m 0644 "$repo_root/assets/icon/icon-windows-256.png" "$stage/usr/share/icons/hicolor/256x256/apps/agent-speed.png"
install -m 0644 "$repo_root/linux/README.md" "$doc_dir/README.md"
install -m 0644 "$repo_root/LICENSE" "$doc_dir/copyright"
python3 "$repo_root/linux/scripts/collect-notices.py" "$publish_dir" "$doc_dir/notices"
desktop-file-validate "$stage/usr/share/applications/agent-speed.desktop"
installed_size="$(du -sk "$stage/opt" "$stage/usr" | awk '{size += $1} END {print size}')"
cat > "$stage/DEBIAN/control" <<CONTROL
Package: agent-speed
Version: $deb_version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Agent Speed local build <root@localhost>
Installed-Size: $installed_size
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libicu70, libssl3, libgssapi-krb5-2, ca-certificates, tzdata, libfontconfig1, libfreetype6, libsqlite3-0, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libxfixes3, libice6, libsm6, libgl1, libgtk-3-0, hicolor-icon-theme, desktop-file-utils, packagekit, packagekit-tools, libelf1, policykit-1
Recommends: gnome-shell-extension-appindicator, fonts-noto-cjk
Homepage: https://github.com/zoefix/agent-speed
Description: Local AI agent speed monitor and daily usage history
 Ubuntu 22.04 x64 GNOME/X11 preview with a floating gauge, system tray,
 application-menu launcher and persistent daily usage history.
 Includes the .NET runtime. Reads supported agent logs without modifying them.
CONTROL
python3 - "$stage" <<'PY'
import hashlib, os, pathlib, sys
root = pathlib.Path(sys.argv[1])
sums = []
for path in sorted(root.rglob('*')):
    if path.is_dir():
        path.chmod(0o755)
    elif not path.is_symlink():
        relative = path.relative_to(root).as_posix()
        executable = relative in ('usr/bin/agent-speed', 'opt/agent-speed/AgentSpeed.Linux', 'opt/agent-speed/createdump', 'opt/agent-speed/traffic/agent-speed-traffic-helper')
        path.chmod(0o755 if executable else 0o644)
        if not relative.startswith('DEBIAN/'):
            sums.append(hashlib.md5(path.read_bytes()).hexdigest() + '  ' + relative)
(root / 'DEBIAN/md5sums').write_text('\n'.join(sums) + '\n')
(root / 'DEBIAN/md5sums').chmod(0o644)
root.chmod(0o755)
PY
package="$repo_root/dist/agent-speed_${deb_version}_amd64.deb"
dpkg-deb --build --root-owner-group -Zxz -z6 "$stage" "$package"
(cd -- "$repo_root/dist" && sha256sum "$(basename -- "$package")" > "$(basename -- "$package").sha256")
python3 - "$package" "$deb_version" "$repo_root/dist/agent-speed-linux-release.json" <<'PY'
import hashlib, json, pathlib, sys
package = pathlib.Path(sys.argv[1])
feed = {'schema': 1, 'version': sys.argv[2], 'package': 'agent-speed', 'architecture': 'amd64',
        'debUrl': package.name, 'sha256': hashlib.sha256(package.read_bytes()).hexdigest(),
        'notes': 'Ubuntu 22.04 x64 / GNOME X11'}
pathlib.Path(sys.argv[3]).write_text(json.dumps(feed, indent=2) + '\n')
PY
echo "Debian package: $package"
echo "Package staging: $stage"
