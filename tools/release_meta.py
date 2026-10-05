#!/usr/bin/env python3
"""Write the meta.json shipped inside the release ZIP.

Jellyfin groups the versions of a plugin by the name in meta.json. Without this file an install from
the catalog writes the catalog's package name ("AnimeClick Metadata"), while a loaded plugin renames
itself to its own Name ("AnimeClick Plugin"): an update then sits next to the copy it updates under a
different name and Jellyfin loads both (issue #2). Shipping the plugin's own name here keeps every
version under one name, so Jellyfin removes the older copy on the next start.
"""
import datetime
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(pattern, path):
    match = re.search(pattern, (ROOT / path).read_text(encoding="utf-8"))
    if not match:
        sys.exit(f"{pattern} not found in {path}")
    return match.group(1)


def manifest(changelog=""):
    return {
        "category": "Metadata",
        "changelog": changelog,
        "description": read(r'Description => "([^"]+)"', "Plugin.cs"),
        "guid": read(r'Guid\.Parse\("([0-9a-f-]{36})"\)', "Plugin.cs"),
        "name": read(r'public override string Name => "([^"]+)"', "Plugin.cs"),
        "overview": "Titoli, trame, generi, cast ed episodi degli anime in italiano da AnimeClick.it.",
        "owner": "iCosiSenpai",
        "targetAbi": read(r"<JellyfinTargetAbi>([^<]+)</JellyfinTargetAbi>", "Directory.Build.props"),
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "version": read(r"<Version>([^<]+)</Version>", "AnimeClick.Plugin.csproj"),
        "status": "Active",
        "autoUpdate": True,
        "assemblies": [],
    }


def main():
    target = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path("meta.json")
    changelog = sys.argv[2] if len(sys.argv) > 2 else ""
    target.write_text(json.dumps(manifest(changelog), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{target}: {manifest()['name']} {manifest()['version']}")


if __name__ == "__main__":
    main()
