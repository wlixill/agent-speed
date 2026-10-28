#!/usr/bin/env python3
"""Exercise advanced orb UI against the real production Application with isolated three-parser fixtures."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='Empty directory retaining isolated settings, build logs and captures')
    parser.add_argument('--nested-x11', action='store_true', help='Create a dedicated Xephyr display on the existing desktop')
    args = parser.parse_args()
    assert os.environ.get('DISPLAY'), 'X11 DISPLAY is required'
    root = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix='agent-speed-advanced-orbs-'))
    if args.output:
        assert not root.exists() or not any(root.iterdir()), '--output must be empty'
        root.mkdir(parents=True, exist_ok=True)
    print('ADVANCED_ORBS_ARTIFACTS=' + str(root), flush=True)
    dotnet = str(Path(os.environ['DOTNET_ROOT']) / 'dotnet') if os.environ.get('DOTNET_ROOT') else shutil.which('dotnet')
    assert dotnet, 'The .NET SDK is required'
    repo = Path(__file__).resolve().parents[2]
    build = root / 'build'
    project = repo / 'linux/tests/AgentSpeed.Linux.AdvancedOrbProbe/AgentSpeed.Linux.AdvancedOrbProbe.csproj'
    with (root / 'build.log').open('w') as log:
        result = subprocess.run([dotnet, 'build', str(project), '-c', 'Release', '--artifacts-path', str(build), '-m:2', '--disable-build-servers'], stdout=log, stderr=log, timeout=180)
    assert result.returncode == 0, (root / 'build.log').read_text()
    server = None
    server_log = None
    env = dict(os.environ, XDG_CONFIG_HOME=str(root / 'xdg-config'), XDG_DATA_HOME=str(root / 'xdg-data'))
    try:
        if args.nested_x11:
            assert shutil.which('Xephyr'), 'Xephyr is required'
            number = next(n for n in range(300, 350) if not Path(f'/tmp/.X{n}-lock').exists() and not Path(f'/tmp/.X11-unix/X{n}').exists())
            server_log = (root / 'xephyr.log').open('w')
            server = subprocess.Popen(['Xephyr', f':{number}', '-screen', '1440x1080', '-nolisten', 'tcp', '-ac', '-noreset', '-title', 'Agent Speed advanced orb verification'], stdout=server_log, stderr=server_log)
            until = time.monotonic() + 10
            while not Path(f'/tmp/.X11-unix/X{number}').exists():
                assert server.poll() is None and time.monotonic() < until, 'Xephyr failed to start'
                time.sleep(.1)
            env['DISPLAY'] = f':{number}'
        harness = build / 'bin/AgentSpeed.Linux.AdvancedOrbProbe/release/AgentSpeed.Linux.AdvancedOrbProbe.dll'
        run = root / 'run'
        with (root / 'app.log').open('w') as log:
            result = subprocess.run([dotnet, str(harness), str(run)], env=env, stdout=log, stderr=log, timeout=90)
        print((root / 'app.log').read_text(), flush=True)
        assert result.returncode == 0, 'Advanced orb production probe failed'
        report = json.loads((run / 'result.json').read_text())
        assert report['synthetic'] and len(report['checks']) >= 20
        assert not report['settings']['EnablePriceUpdates'] and not report['settings']['AutoCheckUpdates']
        for name in ['cost-orb', 'management', 'idle-faded-orb']:
            assert (run / (name + '.png')).stat().st_size > 1000, name
        print(f"PASS actual production advanced orb UI: {len(report['checks'])} assertions; real three-parser fixtures; isolated settings", flush=True)
    finally:
        if server is not None:
            server.terminate()
            server.wait(timeout=5)
        if server_log is not None:
            server_log.close()


if __name__ == '__main__':
    main()
