#!/usr/bin/env python3
"""Test production SpeedApplication features 5-8 with isolated synthetic Codex/config/settings and real UI bindings."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time


def watcher_items():
    if not shutil.which("gdbus") or not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
        return None
    result = subprocess.run(["gdbus", "call", "--session", "--dest", "org.kde.StatusNotifierWatcher",
                             "--object-path", "/StatusNotifierWatcher", "--method", "org.freedesktop.DBus.Properties.Get",
                             "org.kde.StatusNotifierWatcher", "RegisteredStatusNotifierItems"], capture_output=True, text=True, timeout=3)
    return set(re.findall(r"'([^']+)'", result.stdout)) if result.returncode == 0 else None


def icon_sample(item, stage):
    service, separator, suffix = item.partition("/")
    result = subprocess.run(["gdbus", "call", "--session", "--dest", service,
                             "--object-path", "/" + suffix if separator else "/StatusNotifierItem",
                             "--method", "org.freedesktop.DBus.Properties.Get", "org.kde.StatusNotifierItem", "IconPixmap"],
                            capture_output=True, text=True, timeout=3)
    if result.returncode:
        return None
    return {"stage": stage, "sha256": hashlib.sha256(result.stdout.encode()).hexdigest(),
            "dimensions": [[int(a), int(b)] for a, b in re.findall(r"\((\d+),\s*(\d+),\s*\[", result.stdout)]}


def item_pid(item):
    service = item.partition("/")[0]
    result = subprocess.run(["gdbus", "call", "--session", "--dest", "org.freedesktop.DBus",
                             "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.GetConnectionUnixProcessID",
                             service], capture_output=True, text=True, timeout=3)
    match = re.search(r"uint32\s+(\d+)", result.stdout)
    return int(match.group(1)) if result.returncode == 0 and match else None


def belongs_to_process(item, pid):
    # Avalonia embeds its process-local PID in this service name. The bus daemon can report a host PID
    # when the UI probe is launched in a PID namespace, so compare that documented application identity first.
    service = item.partition("/")[0]
    return service.startswith(f"org.kde.StatusNotifierItem-{pid}-") or item_pid(item) == pid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="Empty directory for retained logs, isolated data and PNG captures")
    args = parser.parse_args()
    assert os.environ.get("DISPLAY"), "An actual X11 desktop or xvfb-run DISPLAY is required"
    root = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="agent-speed-app-feature58-"))
    if args.output:
        assert not root.exists() or not any(root.iterdir()), "--output must be empty"
        root.mkdir(parents=True, exist_ok=True)
    print(f"APP_FEATURE58_ARTIFACTS={root}", flush=True)
    repo = Path(__file__).resolve().parents[2]
    dotnet = str(Path(os.environ["DOTNET_ROOT"]) / "dotnet") if os.environ.get("DOTNET_ROOT") else shutil.which("dotnet")
    assert dotnet, "The .NET SDK is required for this test-only production application harness"
    project = repo / "linux/tests/AgentSpeed.Linux.AppFeature58Probe/AgentSpeed.Linux.AppFeature58Probe.csproj"
    build = root / "build"
    with (root / "build.log").open("w") as log:
        result = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--artifacts-path", str(build),
                                 "-m:2", "--disable-build-servers"], stdout=log, stderr=log, timeout=180)
    assert result.returncode == 0, (root / "build.log").read_text()
    harness = build / "bin/AgentSpeed.Linux.AppFeature58Probe/release/AgentSpeed.Linux.AppFeature58Probe.dll"
    run = root / "run"
    before = watcher_items()
    env = dict(os.environ, XDG_CONFIG_HOME=str(root / "xdg-config"), XDG_DATA_HOME=str(root / "xdg-data"),
               CODEX_HOME=str(run / "Codex synthetic"))
    samples, item, last_sample = [], None, 0.0
    with (root / "app.log").open("w") as log:
        process = subprocess.Popen([dotnet, str(harness), str(run)], stdout=log, stderr=log, env=env)
        try:
            deadline = time.monotonic() + 85
            while process.poll() is None:
                assert time.monotonic() < deadline, "production harness timed out"
                if before is not None and time.monotonic() - last_sample > .45:
                    last_sample = time.monotonic()
                    current = watcher_items()
                    new = current - before if current is not None else set()
                    if item is None:
                        item = next((candidate for candidate in new if belongs_to_process(candidate, process.pid)), None)
                    if item is not None:
                        try:
                            stage = json.loads((run / "stage.json").read_text())["stage"]
                        except (FileNotFoundError, json.JSONDecodeError):
                            stage = "initial-features"
                        sample = icon_sample(item, stage)
                        if sample is not None:
                            samples.append(sample)
                time.sleep(.1)
            assert process.returncode == 0, (root / "app.log").read_text()
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
    print((root / "app.log").read_text(), flush=True)
    result = json.loads((run / "result.json").read_text())
    assert result["productionApplication"] == "AgentSpeed.Linux.SpeedApplication"
    assert result["isolated"] and result["fixtureOnly"] and not result["realAutostartUsed"]
    assert result["currency"] == "CNY" and not result["network"]["NetworkEnabled"]
    assert result["network"]["LastPriceCheck"] is None and result["network"]["LastRatesCheck"] is None
    assert len(result["checks"]) >= 25
    (root / "dbus-icons.json").write_text(json.dumps({"item": item, "samples": samples}, indent=2))
    if samples:
        assert any([64, 64] in sample["dimensions"] for sample in samples), samples
        assert len({sample["sha256"] for sample in samples}) >= 2, "actual StatusNotifier IconPixmap never changed"
        print("PASS real session DBus StatusNotifier exposes 64x64 numeric IconPixmap and changing icon pixels", flush=True)
    else:
        print("DBus StatusNotifier watcher unavailable; actual production TrayIcon bindings verified in-process", flush=True)
    try:
        from PIL import Image
    except ImportError:
        print("Pillow unavailable; captured PNG existence and production theme assertions remain verified", flush=True)
    else:
        light = Image.open(run / "dashboard-light.png").convert("RGB")
        dark = Image.open(run / "dashboard-dark.png").convert("RGB")
        assert light.getpixel((5, 5)) == (244, 245, 248), light.getpixel((5, 5))
        assert dark.getpixel((5, 5)) == (25, 26, 32), dark.getpixel((5, 5))
        print("PASS actual Light/Dark dashboard PNG pixels match the live mutable background palette", flush=True)
    captures = ["dashboard-light", "dashboard-dark", "dashboard-auto", "settings-light", "history-modes-light-cny", "history-models-light-cny"]
    for name in captures:
        assert (run / (name + ".png")).stat().st_size > 2000, name
    assert (run / "production-capture/status.json").is_file()
    print(f"PASS actual production app features 5-8: {len(result['checks'])} assertions; synthetic data only", flush=True)


if __name__ == "__main__":
    main()
