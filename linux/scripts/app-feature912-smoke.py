#!/usr/bin/env python3
"""Run actual SpeedApplication legacy and multi-source features 9–12 with isolated synthetic transcripts on X11."""
import argparse
import atexit
import csv
import io
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import tempfile
import time


def group_children(group):
    """Only processes in our freshly created session; exclude already exited zombies."""
    result = []
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            text = (entry / "stat").read_text()
            fields = text[text.rfind(")") + 2:].split()
            if int(fields[2]) == group and fields[0] != "Z":
                result.append({"pid": int(entry.name), "command": (entry / "comm").read_text().strip()})
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            pass
    return result


def run_case(dotnet, harness, root, mode):
    run = root / mode
    env = dict(os.environ, XDG_CONFIG_HOME=str(run / "xdg-config"), XDG_DATA_HOME=str(run / "xdg-data"),
               CODEX_HOME=str(run / "Codex synthetic"), CLAUDE_CONFIG_DIR=str(run / "Claude synthetic"),
               GEMINI_CLI_HOME=str(run / "Gemini synthetic"))
    with (root / (mode + ".log")).open("w") as log:
        process = subprocess.Popen([dotnet, str(harness), str(run), mode], stdout=log, stderr=log,
                                   env=env, start_new_session=True)
        try:
            process.wait(timeout=135)
            assert process.returncode == 0, (root / (mode + ".log")).read_text()
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                process.wait(timeout=10)
    print((root / (mode + ".log")).read_text(), flush=True)
    result = json.loads((run / "result.json").read_text())
    exited = json.loads((run / "exit.json").read_text())
    assert result["productionApplication"] == "AgentSpeed.Linux.SpeedApplication"
    assert result["isolated"] and result["fixtureOnly"] and result["mode"] == mode
    assert exited == {"visibleWindows": 0, "managerCount": 0, "appearanceDisposed": True}, exited
    deadline = time.monotonic() + 4
    while group_children(process.pid) and time.monotonic() < deadline:
        time.sleep(.05)
    remaining = group_children(process.pid)
    assert not remaining, f"Production quit left live child processes: {remaining}"
    print(f"PASS {mode}: normal production Quit closes every owned window and leaves no live gsettings/child process", flush=True)
    return run, result


def rows(path):
    return list(csv.DictReader(io.StringIO(path.read_text(encoding="utf-8-sig"))))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="Empty output directory retaining logs, isolated settings and screenshots")
    parser.add_argument("--nested-x11", action="store_true", help="Create and clean up a dedicated Xephyr display on the existing desktop")
    args = parser.parse_args()
    assert os.environ.get("DISPLAY"), "Use an actual X11 desktop or isolated Xephyr/Xvfb DISPLAY"
    root = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="agent-speed-app-feature912-"))
    if args.output:
        assert not root.exists() or not any(root.iterdir()), "--output must be empty"
        root.mkdir(parents=True, exist_ok=True)
    print("APP_FEATURE912_ARTIFACTS=" + str(root), flush=True)
    if args.nested_x11:
        assert shutil.which("Xephyr"), "Xephyr is required for --nested-x11"
        number = next(number for number in range(110, 200) if not Path(f"/tmp/.X{number}-lock").exists() and not Path(f"/tmp/.X11-unix/X{number}").exists())
        server_log = (root / "xephyr.log").open("w")
        server = subprocess.Popen(["Xephyr", f":{number}", "-screen", "1440x1080", "-nolisten", "tcp", "-ac", "-noreset",
                                   "-title", "Agent Speed · isolated production 9–12 probe"], stdout=server_log, stderr=server_log)

        def close_server():
            if server.poll() is None:
                server.terminate()
                server.wait(timeout=5)
            server_log.close()

        atexit.register(close_server)
        deadline = time.monotonic() + 8
        while not Path(f"/tmp/.X11-unix/X{number}").exists() and time.monotonic() < deadline and server.poll() is None:
            time.sleep(.05)
        assert server.poll() is None and Path(f"/tmp/.X11-unix/X{number}").exists(), "Xephyr did not become ready"
        os.environ["DISPLAY"] = f":{number}"
    repo = Path(__file__).resolve().parents[2]
    dotnet = str(Path(os.environ["DOTNET_ROOT"]) / "dotnet") if os.environ.get("DOTNET_ROOT") else shutil.which("dotnet")
    assert dotnet, "A .NET SDK is required for this test-only production factory harness"
    project = repo / "linux/tests/AgentSpeed.Linux.AppFeature912Probe/AgentSpeed.Linux.AppFeature912Probe.csproj"
    build = root / "build"
    with (root / "build.log").open("w") as log:
        compiled = subprocess.run([dotnet, "build", str(project), "-c", "Release", "--artifacts-path", str(build),
                                   "-m:2", "--disable-build-servers"], stdout=log, stderr=log, timeout=180)
    assert compiled.returncode == 0, (root / "build.log").read_text()
    harness = build / "bin/AgentSpeed.Linux.AppFeature912Probe/release/AgentSpeed.Linux.AppFeature912Probe.dll"
    legacy, old = run_case(dotnet, harness, root, "legacy")
    multi, new = run_case(dotnet, harness, root, "multi")
    assert len(old["checks"]) >= 5 and len(new["checks"]) >= 20
    assert old["settings"]["EnabledSources"] is None and len(old["settings"]["Orbs"]) == 1
    assert not new["settings"]["TrafficEnabled"] and not new["settings"]["EnablePriceUpdates"]
    assert not new["settings"]["AutoCheckUpdates"] and new["settings"]["UpdateFeedUrl"] == ""
    data = rows(multi / "claude-history.csv")
    assert sum(int(row["requests"]) for row in data) == 2
    assert sum(int(row["input_tokens"]) for row in data) == 3300
    assert sum(int(row["output_tokens"]) for row in data) == 210
    assert {row["source"] for row in data} == {"claude-code"}
    models = rows(multi / "claude-models.csv")
    assert len(models) == 1 and models[0]["source"] == "claude-code" and models[0]["model"] == "claude-opus-5-5"
    assert int(models[0]["input_tokens"]) == 3300 and int(models[0]["output_tokens"]) == 210
    all_models = rows(multi / "all-source-models.csv")
    assert {row["source"] for row in all_models} == {"codex", "claude-code", "gemini"}
    assert sum(int(row["output_tokens"]) for row in all_models) == 554
    recovered = rows(multi / "gemini-recovered-history.csv")
    assert {row["source"] for row in recovered} == {"gemini"}
    assert sum(int(row["output_tokens"]) for row in recovered) == 44
    for name in ("legacy-dashboard", "claude-history", "orb-Codex", "orb-ClaudeCode", "orb-Gemini", "traffic-enabled", "updates-offline", "reporting-offline", "management"):
        assert (multi / (name + ".png")).stat().st_size > 1000, name
    print(f"PASS production features 9–12: {len(old['checks']) + len(new['checks'])} assertions; three real parsers; exact CSV counters; local-only sampling; isolated legacy settings", flush=True)


if __name__ == "__main__":
    main()
