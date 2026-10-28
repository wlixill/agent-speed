#!/usr/bin/env python3
"""Exercise advanced production history controls on a dedicated X11 display and isolated clipboard/data."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
root = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix='agent-speed-history-advanced-'))
assert not root.exists() or not any(root.iterdir()), 'Output directory must be empty'
root.mkdir(exist_ok=True, parents=True)
print('HISTORY_ADVANCED_ARTIFACTS=' + str(root), flush=True)
dotnet = str(Path(os.environ['DOTNET_ROOT']) / 'dotnet') if os.environ.get('DOTNET_ROOT') else shutil.which('dotnet')
assert dotnet and shutil.which('Xephyr') and os.environ.get('DISPLAY'), 'SDK, Xephyr and X11 desktop are required'
with (root / 'build.log').open('w') as log:
    built = subprocess.run([dotnet, 'build', str(repo / 'linux/tests/AgentSpeed.Linux.HistoryAdvancedProbe'), '-c', 'Release', '--artifacts-path', str(root / 'build'), '-m:2'], stdout=log, stderr=log, timeout=180)
assert built.returncode == 0, (root / 'build.log').read_text()
number = next(n for n in range(240, 280) if not Path(f'/tmp/.X{n}-lock').exists() and not Path(f'/tmp/.X11-unix/X{n}').exists())
with (root / 'xephyr.log').open('w') as log:
    server = subprocess.Popen(['Xephyr', f':{number}', '-screen', '1440x1080', '-nolisten', 'tcp', '-ac', '-noreset', '-title', 'Agent Speed isolated history probe'], stdout=log, stderr=log)
    try:
        deadline = time.monotonic() + 8
        while not Path(f'/tmp/.X11-unix/X{number}').exists():
            assert server.poll() is None and time.monotonic() < deadline
            time.sleep(.1)
        env = dict(os.environ, DISPLAY=f':{number}', XDG_CONFIG_HOME=str(root / 'xdg-config'), XDG_DATA_HOME=str(root / 'xdg-data'), AGENT_SPEED_HISTORY_PROBE_ISOLATED='1')
        with (root / 'probe.log').open('w') as out:
            run = subprocess.run([dotnet, str(root / 'build/bin/AgentSpeed.Linux.HistoryAdvancedProbe/release/AgentSpeed.Linux.HistoryAdvancedProbe.dll'), str(root / 'run')], env=env, stdout=out, stderr=out, timeout=45)
        print((root / 'probe.log').read_text(), flush=True)
        assert run.returncode == 0
        result = json.loads((root / 'run/result.json').read_text())
        assert result['success'] and len(result['checks']) >= 12
        print(f"PASS advanced history actual UI: {len(result['checks'])} assertions", flush=True)
    finally:
        server.terminate()
        server.wait(timeout=5)
