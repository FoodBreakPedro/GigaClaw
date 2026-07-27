#!/usr/bin/env python3
"""Validate deterministic structure for growth and lead-magnet promo copy."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path


def sections(text: str) -> list[str]:
    return [title.strip() for title in re.findall(r"^##\s+(.+?)\s*$", text, re.MULTILINE)]


def validate(path: Path, kind: str) -> tuple[list[str], str]:
    text = path.read_text(encoding="utf-8")
    errors: list[str] = []
    headings = sections(text)
    if kind == "growth":
        required = ["Primary post", "Alternative hooks", "CTA variation"]
        for heading in required:
            if heading not in headings:
                errors.append(f"missing H2 section: {heading}")
        alternative = re.search(
            r"^##\s+Alternative hooks\s*$\n(.*?)(?=^##\s+|\Z)",
            text,
            re.MULTILINE | re.DOTALL,
        )
        hooks = (
            re.findall(r"^\s*\d+[.)]\s+.+$", alternative.group(1), re.MULTILINE)
            if alternative
            else []
        )
        if len(hooks) != 2:
            errors.append(f"Alternative hooks must contain exactly 2 numbered hooks (found {len(hooks)})")
    else:
        expected = {"Post 1 — Contrarian", "Post 2 — Pain-First", "Post 3 — Results-Led"}
        missing = expected.difference(headings)
        if missing:
            errors.append(f"missing promo sections: {', '.join(sorted(missing))}")
        extras = [heading for heading in headings if heading.startswith("Post ") and heading not in expected]
        if extras:
            errors.append(f"unexpected promo post sections: {', '.join(extras)}")

    # Social paragraphs should stay scan-friendly. Ignore metadata, headings, lists, and comments.
    visible = re.sub(r"\A---\n.*?\n---\n", "", text, flags=re.DOTALL)
    visible = re.sub(r"<!--.*?-->", "", visible, flags=re.DOTALL)
    for number, paragraph in enumerate(re.split(r"\n\s*\n", visible), 1):
        lines = [line for line in paragraph.splitlines() if line.strip()]
        if not lines or lines[0].startswith(("#", "-", "*")):
            continue
        if len(lines) > 3:
            errors.append(f"paragraph {number} exceeds 3 visual lines")
    return errors, hashlib.sha256(path.read_bytes()).hexdigest()


def self_test() -> None:
    import tempfile

    text = """## Primary post

A sharp observation.

One useful detail.

## Alternative hooks

1. First hook
2. Second hook

## CTA variation

What would you change?
"""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "social.md"
        path.write_text(text, encoding="utf-8")
        assert not validate(path, "growth")[0]
        path.write_text(text.replace("2. Second hook", ""), encoding="utf-8")
        assert validate(path, "growth")[0]
    print("[OK] social_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--kind", choices=("growth", "lead-magnet-promo"), default="growth")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")
    path = Path(args.path)
    errors, digest = validate(path, args.kind)
    print(f"=== SOCIAL CONTRACT: {path} ({args.kind}) ===")
    print(f"artifact-sha256:{digest}")
    for error in errors:
        print(f"[FAIL] {error}")
    print("[OK] contract passed" if not errors else f"[FAIL] {len(errors)} violation(s)")
    return 0 if not errors else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
