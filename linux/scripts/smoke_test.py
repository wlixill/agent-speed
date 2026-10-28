#!/usr/bin/env python3
"""Exercise the actual X11 app with explicitly synthetic Codex data; no user sessions are read."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time


def timestamp(seconds=0):
    return (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=seconds)).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def record(kind, payload, when=None):
    return json.dumps({"timestamp": when or timestamp(), "type": kind, "payload": payload}) + "\n"


def usage(response, output, when=None):
    return record("token_usage_record", {"thread_id": "linux-smoke", "session_id": "linux-smoke",
        "response_id": response, "usage": {"input_tokens": output * 10, "cached_input_tokens": 0,
        "output_tokens": output, "reasoning_output_tokens": 0, "total_tokens": output * 11}}, when)


def read_status(path):
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def wait_for(process, path, predicate, timeout=25):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        status = read_status(path)
        if predicate(status):
            return status
        if process.poll() is not None:
            raise AssertionError(f"app exited early: {process.returncode}; status={status}")
        time.sleep(0.1)
    raise AssertionError(f"timed out waiting for app: {read_status(path)}")


def tray_items():
    if not shutil.which("gdbus") or not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
        return None
    result = subprocess.run(["gdbus", "call", "--session", "--dest", "org.kde.StatusNotifierWatcher",
        "--object-path", "/StatusNotifierWatcher", "--method", "org.freedesktop.DBus.Properties.Get",
        "org.kde.StatusNotifierWatcher", "RegisteredStatusNotifierItems"], capture_output=True, text=True, timeout=5)
    return set(re.findall(r"'([^']+)'", result.stdout)) if result.returncode == 0 else None


def floating_id(pid):
    if not shutil.which("xprop"):
        return None
    ids = re.findall(r"0x[0-9a-f]+", subprocess.check_output(["xprop", "-root", "_NET_CLIENT_LIST"], text=True))
    if not ids and shutil.which("xwininfo"):
        ids = re.findall(r"0x[0-9a-f]+", subprocess.check_output(["xwininfo", "-root", "-tree"], text=True))
    for window in dict.fromkeys(ids):
        properties = subprocess.run(["xprop", "-id", window, "_NET_WM_PID", "_NET_WM_NAME"], capture_output=True, text=True).stdout
        owner = re.search(r"_NET_WM_PID\(CARDINAL\) = (\d+)", properties)
        if owner and int(owner.group(1)) == pid and "悬浮窗" in properties:
            return window
    raise AssertionError("The test process's floating window is not mapped")


def check_floating_hints(pid):
    if shutil.which("xprop"):
        window_manager = subprocess.check_output(["xprop", "-root", "_NET_SUPPORTING_WM_CHECK"], text=True, timeout=5)
        if "window id" not in window_manager:
            return  # Xvfb CI has no window manager; GNOME integration is checked on a real desktop.
        properties = subprocess.check_output(["xprop", "-id", floating_id(pid), "_NET_WM_STATE"], text=True, timeout=5)
        assert "_NET_WM_STATE_ABOVE" in properties and "_NET_WM_STATE_SKIP_TASKBAR" in properties, properties


def check_screen_position(status, pid):
    if shutil.which("xwininfo"):
        geometry = subprocess.check_output(["xwininfo", "-id", floating_id(pid), "-stats"], text=True, timeout=5)
        x, y = (int(re.search(r"Absolute upper-left " + axis + r":\s*(-?\d+)", geometry).group(1)) for axis in ("X", "Y"))
        assert (status["orbX"], status["orbY"]) == (x, y), (status, geometry)


def tray_menu(item):
    service, separator, suffix = item.partition("/")
    path = "/" + suffix if separator else "/StatusNotifierItem"

    def call(object_path, method, *arguments):
        return subprocess.check_output(["gdbus", "call", "--session", "--dest", service,
            "--object-path", object_path, "--method", method, *map(str, arguments)], text=True, timeout=5)

    value = call(path, "org.freedesktop.DBus.Properties.Get", "org.kde.StatusNotifierItem", "Menu")
    menu = re.search(r"'([^']+)'", value).group(1)
    def click(label):
        # Avalonia may replace native menu IDs when its labels change. Query the current menu as GNOME does.
        layout = call(menu, "com.canonical.dbusmenu.GetLayout", 0, 1, "['label']")
        entries = {text: number for number, text in re.findall(r"\((\d+), \{[^{}]*'label': <'([^']+)'", layout)}
        assert label in entries, layout
        call(menu, "com.canonical.dbusmenu.Event", entries[label], "clicked", "<0>", 0)
    return click


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    args = parser.parse_args()
    assert os.environ.get("DISPLAY"), "DISPLAY is required; use xvfb-run for headless CI"
    executable = args.executable.resolve()
    command = [str(executable)]
    if executable.suffix == ".dll":
        command.insert(0, str(Path(os.environ["DOTNET_ROOT"]) / "dotnet"))
    root = Path(tempfile.mkdtemp(prefix="agent-speed-linux-smoke-"))
    print(f"SMOKE_ARTIFACTS={root}", flush=True)
    sessions = root / "codex" / "sessions" / dt.datetime.now().strftime("%Y/%m/%d")
    sessions.mkdir(parents=True)
    session = sessions / "rollout-linux-smoke.jsonl"
    session.write_text(record("session_meta", {"id": "linux-smoke", "session_id": "linux-smoke",
        "timestamp": timestamp(-30), "cwd": "/synthetic/ubuntu-smoke-test", "originator": "codex_cli_rs", "source": "cli"}, timestamp(-30))
        + record("turn_context", {"model": "gpt-6.1-sol"}, timestamp(-25))
        + record("event_msg", {"type": "task_started", "turn_id": "seed"}, timestamp(-24))
        + usage("seed-response", 100, timestamp(-22))
        + record("event_msg", {"type": "task_complete", "turn_id": "seed"}, timestamp(-21)))
    common = [*command, "--codex-dir", str(root / "codex" / "sessions"), "--data-dir", str(root / "data"), "--dashboard", "--software-rendering"]
    before = tray_items()
    first_capture = root / "first"
    with (root / "first.log").open("w") as log:
        process = subprocess.Popen([*common, "--capture-dir", str(first_capture), "--exit-after", "24"], stdout=log, stderr=log)
        try:
            initial = wait_for(process, first_capture / "status.json", lambda value: value.get("monitor") == "Running" and value.get("history") == "Ready")
            assert initial["Today"]["Output"] == 100, initial
            assert initial["Today"]["Requests"] == 1, initial
            check_floating_hints(process.pid)
            check_screen_position(initial, process.pid)
            after = tray_items()
            click = None
            if before is not None and after is not None:
                assert after - before, "app did not register a StatusNotifier tray item"
                print("tray registration: verified", flush=True)
                click = tray_menu(next(iter(after - before)))
                click("隐藏悬浮窗")
                wait_for(process, first_capture / "status.json", lambda value: value.get("orbVisible") is False, timeout=8)
                click("显示悬浮窗")
                restored = wait_for(process, first_capture / "status.json", lambda value: value.get("orbVisible") is True, timeout=8)
                assert (restored["orbX"], restored["orbY"]) == (initial["orbX"], initial["orbY"]), restored
                check_floating_hints(process.pid)
                check_screen_position(restored, process.pid)
                click("打开用量历史")
                print("tray hide/show/history actions: verified", flush=True)
            duplicate = subprocess.run(common, capture_output=True, text=True, timeout=8)
            assert duplicate.returncode == 0 and "已在运行" in duplicate.stdout, duplicate
            with session.open("a") as stream:
                stream.write(record("event_msg", {"type": "task_started", "turn_id": "live"}))
                stream.flush()
                time.sleep(1.2)
                stream.write(usage("live-response", 200))
                stream.write(record("event_msg", {"type": "task_complete", "turn_id": "live"}))
            updated = wait_for(process, first_capture / "status.json", lambda value: value.get("Today", {}).get("Output") == 300 and value.get("maximumObservedSpeed", 0) > 0)
            assert updated["Today"]["Requests"] == 2, updated
            assert updated.get("dashboardAnimationFrames", 0) > 0 and updated.get("orbAnimationFrames", 0) > 0, updated
            assert updated.get("curvePoints", 0) > 0, updated
            # Normal quit flushes the incremental daily history before stopping the background services.
            if click is not None:
                click("退出 Agent Speed")
            process.wait(timeout=35)
            assert process.returncode == 0, process.returncode
            settings = json.loads((root / "data" / "settings.json").read_text())
            assert settings["OrbVisible"] is True, settings
            assert (settings["OrbX"], settings["OrbY"]) == (initial["orbX"], initial["orbY"]), settings
            assert (first_capture / "orb.png").stat().st_size > 1000
            assert (first_capture / "dashboard.png").stat().st_size > 1000
            assert (first_capture / "history.png").stat().st_size > 1000
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
    session.unlink()
    third_capture = root / "retained"
    with (root / "retained.log").open("w") as log:
        result = subprocess.run([*common, "--capture-dir", str(third_capture), "--exit-after", "5"], stdout=log, stderr=log, timeout=25)
    assert result.returncode == 0, result.returncode
    retained = read_status(third_capture / "status.json")
    assert retained["history"] == "Ready", retained
    assert retained["dailyRows"][0]["Output"] == 300, retained
    print("PASS: live append, nonzero speed, single instance, X11 rendering, restart, retained daily history", flush=True)


if __name__ == "__main__":
    main()
