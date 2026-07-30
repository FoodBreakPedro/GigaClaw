#!/usr/bin/env python3
"""
sbom_diff.py - Dependency inventory and delta for the supply-chain-reviewer lane.

Deliberately offline and dependency-free: it reads lockfiles and manifests from the working tree
and prints JSON or markdown. It never resolves, installs, executes or downloads anything, so the
one component that touches every dependency in the project cannot itself become a supply-chain
risk. Advisory lookups are the agent's job, not this script's.

Usage:
  python3 sbom_diff.py --root . --out doc/security/sbom/current.json
  python3 sbom_diff.py --root . --baseline doc/security/sbom/previous.json --format markdown

Exit codes: 0 = inventory produced, 1 = no recognized manifest found, 2 = bad usage or unreadable
input.
"""

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

SKIP_DIRS = {
    ".git", "node_modules", "bin", "obj", "dist", "build", "vendor",
    "__pycache__", ".venv", "venv", ".tox", "target", "packages",
}

# Ecosystem -> file names this script knows how to read. A file that is not listed here is still
# part of the supply chain; the agent enumerates it by hand and says so in the report.
RECOGNIZED = {
    "npm": ("package-lock.json",),
    "pypi": ("requirements.txt", "requirements.lock"),
    "nuget": ("packages.lock.json",),
    "nuget-manifest": (".csproj",),
}


def walk(root):
    for current, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(files):
            yield os.path.join(current, name)


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


def read_text(path):
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read()


def parse_npm_lock(text):
    """package-lock.json v2/v3: the 'packages' map keys are paths, '' is the project itself."""
    data = json.loads(text)
    out = []
    packages = data.get("packages")
    if isinstance(packages, dict):
        for key, meta in packages.items():
            if not key or not isinstance(meta, dict):
                continue
            name = meta.get("name") or key.split("node_modules/")[-1]
            version = meta.get("version")
            if not name or not version:
                continue
            direct = key.count("node_modules/") == 1
            out.append((name, str(version), direct))
        return out
    for name, meta in (data.get("dependencies") or {}).items():
        if isinstance(meta, dict) and meta.get("version"):
            out.append((name, str(meta["version"]), True))
    return out


REQUIREMENT = re.compile(r"^\s*([A-Za-z0-9._-]+)\s*==\s*([^\s;#]+)")


def parse_requirements(text):
    out = []
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        match = REQUIREMENT.match(line)
        if match:
            out.append((match.group(1), match.group(2), True))
    return out


def parse_nuget_lock(text):
    data = json.loads(text)
    out = []
    for framework, entries in (data.get("dependencies") or {}).items():
        if not isinstance(entries, dict):
            continue
        for name, meta in entries.items():
            if not isinstance(meta, dict):
                continue
            version = meta.get("resolved") or meta.get("requested")
            if not version:
                continue
            direct = str(meta.get("type", "")).lower() == "direct"
            out.append((name, str(version), direct))
    return out


def parse_csproj(text):
    out = []
    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        return out
    for node in root.iter():
        tag = node.tag.split("}")[-1]
        if tag != "PackageReference":
            continue
        name = node.get("Include") or node.get("Update")
        version = node.get("Version")
        if version is None:
            child = node.find("Version")
            if child is None:
                for candidate in node:
                    if candidate.tag.split("}")[-1] == "Version":
                        child = candidate
                        break
            version = child.text.strip() if child is not None and child.text else None
        if name and version:
            out.append((name, version, True))
    return out


PARSERS = {
    "package-lock.json": ("npm", parse_npm_lock),
    "requirements.txt": ("pypi", parse_requirements),
    "requirements.lock": ("pypi", parse_requirements),
    "packages.lock.json": ("nuget", parse_nuget_lock),
}


def collect(root):
    packages = {}
    sources = []
    unreadable = []
    for path in walk(root):
        base = os.path.basename(path)
        entry = PARSERS.get(base)
        parser = None
        ecosystem = None
        if entry is not None:
            ecosystem, parser = entry
        elif base.endswith(".csproj"):
            ecosystem, parser = "nuget", parse_csproj
        if parser is None:
            continue
        try:
            parsed = parser(read_text(path))
        except (OSError, ValueError) as error:
            unreadable.append({"file": rel(root, path), "error": str(error)})
            continue
        sources.append(rel(root, path))
        for name, version, direct in parsed:
            key = "%s:%s" % (ecosystem, name)
            existing = packages.get(key)
            if existing is None:
                packages[key] = {
                    "ecosystem": ecosystem,
                    "name": name,
                    "versions": [version],
                    "direct": bool(direct),
                    "sources": [rel(root, path)],
                }
                continue
            if version not in existing["versions"]:
                existing["versions"].append(version)
                existing["versions"].sort()
            existing["direct"] = existing["direct"] or bool(direct)
            if rel(root, path) not in existing["sources"]:
                existing["sources"].append(rel(root, path))
    return {
        "version": 1,
        "sources": sorted(sources),
        "unreadable": unreadable,
        "packages": [packages[key] for key in sorted(packages)],
    }


def index(inventory):
    return {"%s:%s" % (p["ecosystem"], p["name"]): p for p in inventory.get("packages", [])}


def diff(current, baseline):
    now, before = index(current), index(baseline)
    added, removed, changed = [], [], []
    for key in sorted(set(now) | set(before)):
        left, right = before.get(key), now.get(key)
        if left is None:
            added.append(right)
        elif right is None:
            removed.append(left)
        elif left["versions"] != right["versions"]:
            changed.append({
                "ecosystem": right["ecosystem"],
                "name": right["name"],
                "from": left["versions"],
                "to": right["versions"],
                "direct": right["direct"],
            })
    return {"added": added, "removed": removed, "changed": changed}


def render_markdown(inventory, delta):
    lines = ["# Dependency inventory", ""]
    direct = sum(1 for p in inventory["packages"] if p["direct"])
    lines.append("- Sources read: %d" % len(inventory["sources"]))
    lines.append("- Packages: %d (%d direct, %d transitive)"
                 % (len(inventory["packages"]), direct, len(inventory["packages"]) - direct))
    if inventory["unreadable"]:
        lines.append("- **Unreadable files: %d — these are unchecked, not clean**"
                     % len(inventory["unreadable"]))
    if delta is None:
        lines.append("")
        lines.append("No baseline supplied; this is a full inventory, not a delta.")
        return "\n".join(lines) + "\n"
    lines += ["", "## Delta since baseline", ""]
    for label, key in (("Added", "added"), ("Removed", "removed")):
        lines.append("### %s (%d)" % (label, len(delta[key])))
        for item in delta[key]:
            lines.append("- `%s` %s %s" % (item["ecosystem"], item["name"],
                                           ", ".join(item["versions"])))
        lines.append("")
    lines.append("### Changed (%d)" % len(delta["changed"]))
    for item in delta["changed"]:
        lines.append("- `%s` %s: %s -> %s" % (item["ecosystem"], item["name"],
                                              ", ".join(item["from"]), ", ".join(item["to"])))
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description="Dependency inventory and delta (offline).")
    parser.add_argument("--root", default=".", help="Repository root to walk.")
    parser.add_argument("--baseline", help="Previous inventory JSON to diff against.")
    parser.add_argument("--out", help="Write the inventory JSON here.")
    parser.add_argument("--format", choices=("json", "markdown"), default="json")
    args = parser.parse_args()

    root = os.path.abspath(args.root)
    if not os.path.isdir(root):
        print("sbom_diff: --root is not a directory: %s" % root, file=sys.stderr)
        return 2

    inventory = collect(root)
    delta = None
    if args.baseline:
        try:
            delta = diff(inventory, json.loads(read_text(args.baseline)))
        except (OSError, ValueError) as error:
            print("sbom_diff: cannot read baseline: %s" % error, file=sys.stderr)
            return 2

    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as handle:
            json.dump(inventory, handle, indent=2, sort_keys=True)
            handle.write("\n")

    if args.format == "markdown":
        sys.stdout.write(render_markdown(inventory, delta))
    else:
        payload = {"inventory": inventory}
        if delta is not None:
            payload["delta"] = delta
        json.dump(payload, sys.stdout, indent=2, sort_keys=True)
        sys.stdout.write("\n")

    if not inventory["sources"]:
        print("sbom_diff: no recognized manifest or lockfile found under %s" % root,
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
