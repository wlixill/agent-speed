#!/usr/bin/env python3
"""Exercise actual X11 settings/model controls and background CLI with isolated synthetic data."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time


def windows(pid):
    result = subprocess.run(["xprop", "-root", "_NET_CLIENT_LIST"], capture_output=True, text=True, check=True).stdout
    ids = re.findall(r"0x[0-9a-fA-F]+", result)
    if not ids:
        result = subprocess.check_output(["xwininfo", "-root", "-tree"], text=True)
        ids = re.findall(r"0x[0-9a-fA-F]+", result)
    matches = []
    for window in dict.fromkeys(ids):
        props = subprocess.run(["xprop", "-id", window, "_NET_WM_PID", "_NET_WM_NAME"], capture_output=True, text=True).stdout
        if re.search(r"_NET_WM_PID\(CARDINAL\) = " + str(pid) + r"\b", props):
            # Xvfb without a window manager lists created-but-unmapped native windows in its root tree.
            geometry = subprocess.run(["xwininfo", "-id", window, "-stats"], capture_output=True, text=True).stdout
            if re.search(r"Map State:\s*IsViewable\b", geometry):
                matches.append(props)
    return matches


def wait(process, predicate, timeout=15):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if process.poll() is not None:
            raise AssertionError(f"app exited unexpectedly: {process.returncode}")
        if predicate():
            return
        time.sleep(.1)
    raise AssertionError("timed out waiting for application state")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--controls-only", action="store_true", help="Skip separate CLI startup checks")
    args = parser.parse_args()
    assert os.environ.get("DISPLAY"), "An actual X11 DISPLAY or xvfb-run is required"
    repo = Path(__file__).resolve().parents[2]
    root = Path(tempfile.mkdtemp(prefix="agent-speed-feature-smoke-"))
    print(f"FEATURE_SMOKE_ARTIFACTS={root}", flush=True)
    dotnet = str(Path(os.environ["DOTNET_ROOT"]) / "dotnet") if os.environ.get("DOTNET_ROOT") else shutil.which("dotnet")
    assert dotnet, "The .NET SDK is required to build the test-only UI harness"
    project = repo / "linux/tests/AgentSpeed.Linux.FeatureProbe/AgentSpeed.Linux.FeatureProbe.csproj"
    build = root / "build"
    with (root / "build.log").open("w") as log:
        result = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--artifacts-path", str(build)],
                                stdout=log, stderr=log, timeout=180)
    assert result.returncode == 0, (root / "build.log").read_text()
    harness = build / "bin/AgentSpeed.Linux.FeatureProbe/release/AgentSpeed.Linux.FeatureProbe.dll"
    print("test-only harness built", flush=True)
    with (root / "controls.log").open("w") as log:
        result = subprocess.run([dotnet, str(harness), str(root / "controls")], stdout=log, stderr=log, timeout=55)
    assert result.returncode == 0, (root / "controls.log").read_text()
    print((root / "controls.log").read_text(), flush=True)
    if args.controls_only:
        print("PASS actual settings bindings, exact model counters and full agent list", flush=True)
        return

    command = [str(args.executable.resolve())]
    if args.executable.suffix == ".dll":
        command.insert(0, dotnet)
    sessions = root / "controls/Codex 数据 %U/sessions"
    # Explicit background suppresses a saved foreground preference. Normal startup respects the saved preference.
    for name, orb, preference, flags in [("explicit-background", False, False, ["--background"]),
                                          ("saved-background-with-orb", True, True, [])]:
        data = root / name
        data.mkdir()
        (data / "settings.json").write_text(json.dumps({"OrbVisible": orb, "StartInBackground": preference}))
        common = [*command, "--codex-dir", str(sessions), "--data-dir", str(data), "--software-rendering"]
        logfile = root / (name + ".log")
        with logfile.open("w") as log:
            process = subprocess.Popen([*common, *flags, "--exit-after", "12"], stdout=log, stderr=log)
            try:
                wait(process, lambda: (data / "history.sqlite").exists())
                time.sleep(.8)
                actual = windows(process.pid)
                assert not any("实时仪表" in value for value in actual), actual
                assert any("悬浮窗" in value for value in actual) == orb, actual
                if not orb:
                    assert not actual, actual
                duplicate = subprocess.run([*common, "--background"], capture_output=True, text=True, timeout=10)
                assert duplicate.returncode == 0 and "已在后台运行" in duplicate.stdout, duplicate
                time.sleep(.6)
                assert not any("实时仪表" in value for value in windows(process.pid)), "A repeated background launch opened the dashboard"
                foreground = subprocess.run([*common, "--dashboard"], capture_output=True, text=True, timeout=10)
                assert foreground.returncode == 0 and "已在运行" in foreground.stdout, foreground
                wait(process, lambda: any("实时仪表" in value for value in windows(process.pid)), timeout=8)
                process.wait(timeout=25)
                assert process.returncode == 0, logfile.read_text()
                print(f"PASS {name}: initial visibility, repeated --background, existing-process --dashboard", flush=True)
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)
    print("PASS actual settings bindings, exact model counters, full agent list and background startup", flush=True)


if __name__ == "__main__":
    main()
