#!/usr/bin/env python3
"""Check the actual Debian payload, launcher, permissions, checksums and isolated dpkg unpack/remove."""
import argparse
import configparser
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tarfile
import tempfile


def run(*command):
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(result.stdout)
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    package = args.package.resolve()
    output = args.output_dir.resolve() if args.output_dir else Path(tempfile.mkdtemp(prefix="agent-speed-deb-check-"))
    output.mkdir(parents=True, exist_ok=True)
    assert not any(output.iterdir()), f"Refusing to overwrite a nonempty validation directory: {output}"
    root, control = output / "root", output / "control"
    metadata = {field: run("dpkg-deb", "--field", str(package), field).strip() for field in
                ("Package", "Version", "Architecture", "Depends")}
    assert metadata["Package"] == "agent-speed" and metadata["Architecture"] == "amd64", metadata
    assert "libicu70" in metadata["Depends"] and "libssl3" in metadata["Depends"], metadata
    assert "dotnet-runtime" not in metadata["Depends"] and "dotnet-sdk" not in metadata["Depends"], metadata
    archive_process = subprocess.Popen(["dpkg-deb", "--fsys-tarfile", str(package)], stdout=subprocess.PIPE)
    with tarfile.open(fileobj=archive_process.stdout, mode="r|") as archive:
        for member in archive:
            assert member.uid == member.gid == 0, member.name
            assert not member.mode & 0o6002, (member.name, oct(member.mode))
            assert ".." not in Path(member.name).parts and not member.name.startswith("/"), member.name
            assert "/home/" not in member.name and "/.codex/" not in member.name, member.name
    assert archive_process.wait() == 0
    run("dpkg-deb", "--extract", str(package), str(root))
    run("dpkg-deb", "--control", str(package), str(control))
    for filename in ("preinst", "postinst", "prerm", "postrm"):
        assert not (control / filename).exists(), "Package should not run installation scripts: " + filename
    for line in (control / "md5sums").read_text().splitlines():
        expected, name = line.split("  ", 1)
        assert hashlib.md5((root / name).read_bytes()).hexdigest() == expected, name
    executable = root / "opt/agent-speed/AgentSpeed.Linux"
    assert executable.stat().st_mode & 0o111
    assert json.loads((executable.parent / "build-info.json").read_text())["debianVersion"] == metadata["Version"]
    for name in ("libcoreclr.so", "libhostfxr.so", "libSkiaSharp.so", "AgentSpeed.Linux.runtimeconfig.json"):
        assert (executable.parent / name).is_file(), name
    runtime = json.loads((executable.parent / "AgentSpeed.Linux.runtimeconfig.json").read_text())
    assert runtime["runtimeOptions"]["includedFrameworks"]
    if subprocess.run(["dpkg", "--compare-versions", metadata["Version"], "ge", "1.4.1-6"], check=False).returncode == 0:
        import xml.etree.ElementTree as ET
        helper = executable.parent / "traffic/agent-speed-traffic-helper"
        bpf = executable.parent / "traffic/traffic.bpf.o"
        assert helper.is_file() and helper.stat().st_mode & 0o111
        assert bpf.is_file() and not bpf.stat().st_mode & 0o111
        assert helper.read_bytes()[:4] == b"\x7fELF" and bpf.read_bytes()[:4] == b"\x7fELF"
        assert "libelf1" in metadata["Depends"] and "policykit-1" in metadata["Depends"]
        assert (root / "usr/share/doc/agent-speed/notices/libbpf/LIBBPF-LICENSE").is_file()
        assert (root / "usr/share/doc/agent-speed/notices/libbpf/libbpf-v1.4.7.tar.gz").is_file()
        if Path("/sys/kernel/btf/vmlinux").is_file():
            assert json.loads(run(str(helper), "--preflight"))["privilegedAttachPerformed"] is False
        policy = ET.parse(root / "usr/share/polkit-1/actions/org.agentspeed.traffic.policy").getroot()
        action = policy.find("action")
        assert action is not None and action.attrib["id"] == "org.agentspeed.traffic.capture"
        assert action.find("defaults/allow_any").text == "no"
        assert action.find("annotate").text == "/opt/agent-speed/traffic/agent-speed-traffic-helper"
        assert not (control / "postinst").exists(), "Enhanced capture must never start during package installation"
    desktop = root / "usr/share/applications/agent-speed.desktop"
    run("desktop-file-validate", str(desktop))
    config = configparser.ConfigParser(interpolation=None)
    config.read(desktop)
    entry = config["Desktop Entry"]
    assert entry["Exec"] == "/opt/agent-speed/AgentSpeed.Linux --dashboard"
    assert entry["TryExec"] == "/opt/agent-speed/AgentSpeed.Linux"
    assert entry["StartupWMClass"] == "agent-speed" and entry["Icon"] == "agent-speed"
    assert entry.getboolean("Terminal") is False
    icon = root / "usr/share/icons/hicolor/256x256/apps/agent-speed.png"
    assert struct.unpack(">II", icon.read_bytes()[16:24]) == (256, 256)
    wrapper = root / "usr/bin/agent-speed"
    assert wrapper.stat().st_mode & 0o111
    run("sh", "-n", str(wrapper))
    assert 'exec /opt/agent-speed/AgentSpeed.Linux "$@"' in wrapper.read_text()
    assert (root / "usr/share/doc/agent-speed/notices").is_dir()
    assert not list(root.rglob("history.sqlite")) and not list(root.rglob("settings.json"))
    if shutil.which("bwrap") or shutil.which("fakeroot"):
        installed = output / "dpkg-root"
        admin = installed / "var/lib/dpkg"
        (admin / "updates").mkdir(parents=True)
        (admin / "status").touch()
        sentinel = installed / "home/test-user/.config/agent-speed/settings.json"
        sentinel.parent.mkdir(parents=True)
        sentinel.write_text('{"keep":"user preferences"}')
        if shutil.which("bwrap"):
            # Map namespace root to the current user, and allow writes only in this disposable test directory.
            # This also works when the caller is already in a user namespace, where fakeroot cannot fake tar's chown.
            sandbox = ["bwrap", "--unshare-user", "--uid", "0", "--gid", "0", "--ro-bind", "/", "/",
                       "--bind", str(output), str(output), "--"]
        else:
            sandbox = ["fakeroot"]
        prefix = [*sandbox, "dpkg", "--root=" + str(installed), "--force-not-root", "--log=" + str(output / "dpkg.log")]
        run(*prefix, "--unpack", str(package))
        assert (installed / "usr/share/applications/agent-speed.desktop").is_file()
        run(*prefix, "--remove", "agent-speed")
        assert not (installed / "usr/share/applications/agent-speed.desktop").exists()
        assert not (installed / "opt/agent-speed/AgentSpeed.Linux").exists()
        assert sentinel.read_text() == '{"keep":"user preferences"}'
        print("Isolated dpkg unpack/remove: PASS (user settings retained)")
    (output / "result.json").write_text(json.dumps({"metadata": metadata, "root": str(root), "desktop": str(desktop)}, indent=2))
    print("Debian metadata, root ownership, modes, hashes, runtime, menu and icon: PASS")
    print("DEB_CHECK_ROOT=" + str(root))


if __name__ == "__main__":
    main()
