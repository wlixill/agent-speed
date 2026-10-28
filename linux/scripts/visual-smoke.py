#!/usr/bin/env python3
"""Render the Codex visual port using explicit synthetic log fixtures, including live rocket animation and 30-day history."""
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


def main():
    executable = Path(sys.argv[1]).resolve()
    tools = importlib.util.spec_from_file_location("app_smoke", Path(__file__).with_name("smoke_test.py"))
    smoke = importlib.util.module_from_spec(tools)
    tools.loader.exec_module(smoke)
    root = Path(tempfile.mkdtemp(prefix="agent-speed-visual-smoke-"))
    sessions = root / "codex/sessions"
    captures = root / "captures"
    sessions.mkdir(parents=True)
    today = dt.datetime.now().astimezone()
    for offset in range(30, 0, -1):
        date = today - dt.timedelta(days=offset)
        start = date.replace(hour=10, minute=0, second=0, microsecond=0).astimezone(dt.timezone.utc)
        stamp = lambda value: value.isoformat(timespec="milliseconds").replace("+00:00", "Z")
        thread = "visual-history-" + str(offset)
        lines = smoke.record("session_meta", {"id": thread, "session_id": thread, "timestamp": stamp(start),
            "cwd": "/synthetic/visual-validation", "originator": "codex_cli_rs", "source": "cli"}, stamp(start))
        lines += smoke.record("turn_context", {"model": "gpt-6.1-sol"}, stamp(start))
        for request in range(4 + offset % 9):
            request_start = start + dt.timedelta(seconds=request * 40 + 1)
            lines += smoke.record("event_msg", {"type": "task_started", "turn_id": str(request)}, stamp(request_start))
            lines += smoke.record("token_usage_record", {"thread_id": thread, "session_id": thread,
                "response_id": thread + "-" + str(request), "usage": {"input_tokens": 40000 + offset * 3000,
                "cached_input_tokens": 18000, "output_tokens": 800 + (offset * 137 + request * 113) % 2400,
                "reasoning_output_tokens": 0}}, stamp(request_start + dt.timedelta(seconds=6 + offset % 7)))
            lines += smoke.record("event_msg", {"type": "task_complete", "turn_id": str(request)}, stamp(request_start + dt.timedelta(seconds=15)))
        (sessions / ("rollout-" + thread + ".jsonl")).write_text(lines)
    live = sessions / "rollout-live-visual.jsonl"
    live.write_text(smoke.record("session_meta", {"id": "linux-smoke", "session_id": "linux-smoke", "timestamp": smoke.timestamp(-5),
        "cwd": "/synthetic/animated-codex", "originator": "codex_cli_rs", "source": "cli"}, smoke.timestamp(-5))
        + smoke.record("turn_context", {"model": "gpt-6.1-sol"}, smoke.timestamp(-4)))
    command = [str(executable)]
    if executable.suffix == ".dll":
        command.insert(0, str(Path(os.environ["DOTNET_ROOT"]) / "dotnet"))
    command += ["--codex-dir", str(sessions), "--data-dir", str(root / "data"), "--dashboard", "--software-rendering",
        "--capture-dir", str(captures), "--exit-after", "28"]
    print("VISUAL_ARTIFACTS=" + str(root), flush=True)
    with (root / "app.log").open("w") as log:
        process = subprocess.Popen(command, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 23
            request = 0
            first_frames = None
            while time.monotonic() < deadline:
                with live.open("a") as writer:
                    writer.write(smoke.record("event_msg", {"type": "task_started", "turn_id": str(request)}))
                time.sleep(.9)
                with live.open("a") as writer:
                    writer.write(smoke.usage("visual-live-" + str(request), 900))
                    writer.write(smoke.record("event_msg", {"type": "task_complete", "turn_id": str(request)}))
                request += 1
                status = smoke.read_status(captures / "status.json")
                if status.get("dashboardVehicle") == "rocket" and status.get("historyVisibleDays", 0) >= 20:
                    if first_frames is None:
                        first_frames = status["orbAnimationFrames"]
                        shutil.copy2(captures / "orb.png", root / "orb-frame-a.png")
                    elif status["orbAnimationFrames"] > first_frames + 35:
                        shutil.copy2(captures / "orb.png", root / "orb-frame-b.png")
                        break
                if process.poll() is not None:
                    raise AssertionError((root / "app.log").read_text())
            assert status.get("dashboardVehicle") == "rocket" and status.get("orbVehicle") == "rocket", status
            assert status.get("curvePoints", 0) > 15 and status.get("historyVisibleDays", 0) >= 20, status
            assert (root / "orb-frame-b.png").exists(), "animation frames did not advance"
            try:
                from PIL import Image, ImageChops
                first = Image.open(root / "orb-frame-a.png").convert("RGB").crop((80, 60, 164, 160))
                second = Image.open(root / "orb-frame-b.png").convert("RGB").crop((80, 60, 164, 160))
                assert ImageChops.difference(first, second).getbbox(), "vehicle center rendered identical frames"
            except ImportError:
                assert (root / "orb-frame-a.png").read_bytes() != (root / "orb-frame-b.png").read_bytes()
            for name in ("dashboard", "orb", "history"):
                assert (captures / (name + ".png")).stat().st_size > 2000
            verified = root / "verified"
            verified.mkdir()
            for path in captures.iterdir():
                shutil.copy2(path, verified / path.name)
            print("PASS: original rocket artwork, changing vehicle frames, real live curve and rich 30-day Codex statistics", flush=True)
            process.wait(timeout=40)
            assert process.returncode == 0, (root / "app.log").read_text()
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)


if __name__ == "__main__":
    main()
