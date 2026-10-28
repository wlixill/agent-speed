#!/usr/bin/env python3
"""Collect the pinned dependencies' actual license notices from their build-time NuGet packages."""
import json
import os
from pathlib import Path
import shutil
import sys


def main():
    publish, destination = map(Path, sys.argv[1:])
    packages = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
    dependencies = json.loads((publish / "AgentSpeed.Linux.deps.json").read_text())["libraries"]
    versions = {name.rsplit("/", 1)[0].lower(): name.rsplit("/", 1)[1] for name in dependencies}
    runtime = json.loads((publish / "AgentSpeed.Linux.runtimeconfig.json").read_text())["runtimeOptions"]
    runtime_version = next(item["version"] for item in runtime["includedFrameworks"] if item["name"] == "Microsoft.NETCore.App")
    versions["microsoft.netcore.app.runtime.linux-x64"] = runtime_version
    vendor = Path(__file__).resolve().parent.parent / "packaging" / "licenses"
    sources = json.loads((vendor / "sources.json").read_text())
    destination.mkdir(parents=True, exist_ok=True)
    for name, source in sources.items():
        if versions.get(name.lower()) != source["version"]:
            raise SystemExit(f"Refresh {name} license notice for dependency version {versions.get(name.lower())}")
        shutil.copy2(vendor / source["file"], destination / source["file"])
    shutil.copy2(vendor / "sources.json", destination / "sources.json")
    for name in ("microsoft.netcore.app.runtime.linux-x64", "skiasharp", "skiasharp.nativeassets.linux",
                 "harfbuzzsharp", "harfbuzzsharp.nativeassets.linux"):
        directory = packages / name / versions[name]
        files = [path for path in directory.iterdir() if path.name.lower() in
                 ("license.txt", "third-party-notices.txt")]
        if not files:
            raise SystemExit(f"License notices not found in {directory}; set NUGET_PACKAGES to the build's package cache")
        for path in files:
            shutil.copy2(path, destination / (name + "-" + versions[name] + "-" + path.name))
    print(f"Collected {len(list(destination.iterdir()))} dependency license and notice files")


if __name__ == "__main__":
    main()
