#!/usr/bin/env python3
"""Validate source provenance tables in research briefs and design specs.

Research table columns:
  Claim | Source | Retrieved | Confidence | Evidence type
Design table columns:
  Token | Value | Source | Selector/location | Retrieved | Confidence

`Source` must be an HTTPS URL, an existing local artifact, or `inference` when
confidence is `low`. Dates use YYYY-MM-DD; confidence is high/medium/low.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import re
import sys
import urllib.parse
from pathlib import Path


DESIGN_HEADINGS = [
    "Macrostructure",
    "Typography",
    "Color",
    "Spacing",
    "Micro-interactions",
    "Do-not-copy",
]


def normalize(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", value.lower()).strip()


def parse_tables(text: str) -> list[tuple[list[str], list[list[str]]]]:
    lines = text.splitlines()
    tables: list[tuple[list[str], list[list[str]]]] = []
    index = 0
    while index + 1 < len(lines):
        header = lines[index]
        separator = lines[index + 1]
        if "|" not in header or not re.match(r"^\s*\|?[\s:|-]+\|[\s:|-]*\|?\s*$", separator):
            index += 1
            continue
        columns = [cell.strip() for cell in header.strip().strip("|").split("|")]
        rows: list[list[str]] = []
        index += 2
        while index < len(lines) and "|" in lines[index] and lines[index].strip():
            row = [cell.strip() for cell in lines[index].strip().strip("|").split("|")]
            if len(row) == len(columns):
                rows.append(row)
            index += 1
        tables.append((columns, rows))
    return tables


def validate_source(source: str, document: Path, confidence: str) -> str | None:
    source = source.strip()
    if source.lower() == "inference":
        return None if confidence == "low" else "inference rows must use low confidence"
    parsed = urllib.parse.urlparse(source)
    if parsed.scheme == "https" and parsed.netloc:
        return None
    if parsed.scheme:
        return f"source uses unsupported scheme: {source}"
    candidate = (document.parent / source).resolve()
    if candidate.exists():
        return None
    return f"source is neither HTTPS, an existing local path, nor inference: {source}"


def validate(path: Path, kind: str, allow_community_only: bool) -> tuple[list[str], str]:
    text = path.read_text(encoding="utf-8")
    errors: list[str] = []
    if kind == "design":
        actual = [title.strip() for _, title in re.findall(r"^(#{2})\s+(.+)$", text, re.MULTILINE)]
        positions: list[int] = []
        for heading in DESIGN_HEADINGS:
            if heading not in actual:
                errors.append(f"missing required H2: {heading}")
            else:
                positions.append(actual.index(heading))
        if len(positions) == len(DESIGN_HEADINGS) and positions != sorted(positions):
            errors.append("design spec H2 headings are out of order")

    expected = (
        ["token", "value", "source", "selector location", "retrieved", "confidence"]
        if kind == "design"
        else ["claim", "source", "retrieved", "confidence", "evidence type"]
    )
    inventory: tuple[list[str], list[list[str]]] | None = None
    for columns, rows in parse_tables(text):
        normalized = [normalize(column) for column in columns]
        if all(item in normalized for item in expected):
            inventory = (normalized, rows)
            break
    if inventory is None:
        errors.append(f"missing {kind} source inventory table with columns: {', '.join(expected)}")
        return errors, hashlib.sha256(path.read_bytes()).hexdigest()

    columns, rows = inventory
    if not rows:
        errors.append("source inventory has no rows")
        return errors, hashlib.sha256(path.read_bytes()).hexdigest()
    index = {name: columns.index(name) for name in expected}
    evidence_types: set[str] = set()
    for row_number, row in enumerate(rows, 1):
        confidence = row[index["confidence"]].lower()
        if confidence not in {"high", "medium", "low"}:
            errors.append(f"inventory row {row_number}: confidence must be high, medium, or low")
        retrieved = row[index["retrieved"]]
        try:
            dt.date.fromisoformat(retrieved)
        except ValueError:
            errors.append(f"inventory row {row_number}: retrieved must use YYYY-MM-DD")
        source_error = validate_source(row[index["source"]], path, confidence)
        if source_error:
            errors.append(f"inventory row {row_number}: {source_error}")
        if kind == "design":
            if not row[index["token"]] or not row[index["value"]] or not row[index["selector location"]]:
                errors.append(f"inventory row {row_number}: token, value, and selector/location are required")
        else:
            evidence = normalize(row[index["evidence type"]])
            evidence_types.add(evidence)
            if evidence not in {"primary official", "secondary", "community", "first party"}:
                errors.append(
                    f"inventory row {row_number}: evidence type must be primary/official, first-party, secondary, or community"
                )
    if kind == "research" and not allow_community_only:
        if not evidence_types.intersection({"primary official", "first party"}):
            errors.append("research brief requires at least one primary/official or first-party source")
    return errors, hashlib.sha256(path.read_bytes()).hexdigest()


def self_test() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "brief.md"
        path.write_text(
            """## Source inventory

| Claim | Source | Retrieved | Confidence | Evidence type |
|---|---|---|---|---|
| API behavior | https://docs.test/api | 2026-07-27 | high | primary/official |
""",
            encoding="utf-8",
        )
        assert not validate(path, "research", False)[0]
        path.write_text(path.read_text().replace("2026-07-27", "yesterday"), encoding="utf-8")
        assert validate(path, "research", False)[0]
    print("[OK] source_inventory self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--kind", choices=("research", "design"), default="research")
    parser.add_argument("--allow-community-only", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")
    path = Path(args.path)
    errors, digest = validate(path, args.kind, args.allow_community_only)
    print(f"=== SOURCE INVENTORY: {path} ===")
    print(f"artifact-sha256:{digest}")
    for error in errors:
        print(f"[FAIL] {error}")
    print("[OK] provenance contract passed" if not errors else f"[FAIL] {len(errors)} violation(s)")
    return 0 if not errors else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
