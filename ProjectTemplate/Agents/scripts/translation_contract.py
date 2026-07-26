#!/usr/bin/env python3
"""Validate structural parity and reciprocal alternates for translations."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import urllib.parse
from pathlib import Path
from typing import Any


SCRIPT_RE = re.compile(
    r"<script\b[^>]*\btype\s*=\s*['\"]application/ld\+json['\"][^>]*>(.*?)</script>",
    re.IGNORECASE | re.DOTALL,
)


def frontmatter(text: str) -> tuple[str, str]:
    if not text.startswith("---\n"):
        return "", text
    end = text.find("\n---\n", 4)
    return (text[4:end], text[end + 5 :]) if end >= 0 else ("", text)


def alternates(front: str) -> dict[str, str]:
    lines = front.splitlines()
    result: dict[str, str] = {}
    for index, line in enumerate(lines):
        if re.match(r"^alternates:\s*$", line):
            for nested in lines[index + 1 :]:
                match = re.match(r"^\s{2,}([A-Za-z0-9-]+):\s*(\S.*?)\s*$", nested)
                if not match:
                    break
                result[match.group(1)] = match.group(2).strip("'\"")
            break
    return result


def json_types(value: Any) -> set[str]:
    found: set[str] = set()
    if isinstance(value, dict):
        current = value.get("@type")
        if isinstance(current, str):
            found.add(current)
        elif isinstance(current, list):
            found.update(item for item in current if isinstance(item, str))
        for nested in value.values():
            found.update(json_types(nested))
    elif isinstance(value, list):
        for nested in value:
            found.update(json_types(nested))
    return found


def structure(body: str) -> dict[str, Any]:
    code = re.findall(r"```[\w+-]*\n(.*?)```", body, re.DOTALL)
    without_code = re.sub(r"```.*?```", "", body, flags=re.DOTALL)
    schemas = SCRIPT_RE.findall(without_code)
    types: list[list[str]] = []
    invalid_schema = False
    for raw in schemas:
        try:
            types.append(sorted(json_types(json.loads(raw))))
        except json.JSONDecodeError:
            invalid_schema = True
    table_widths: list[int] = []
    for line in without_code.splitlines():
        if re.match(r"^\s*\|?[\s:|-]*---[\s:|-]*\|[\s:|-]*\s*$", line):
            table_widths.append(max(0, line.count("|") - (0 if line.strip().startswith("|") else 1)))
    return {
        "heading_levels": [len(marks) for marks in re.findall(r"^(#{1,6})\s+", without_code, re.MULTILINE)],
        "code_blocks": code,
        "table_widths": table_widths,
        "image_targets": re.findall(r"!\[[^\]]*\]\(([^)\s]+)", without_code),
        "schema_types": types,
        "invalid_schema": invalid_schema,
    }


def prose_blocks(body: str) -> set[str]:
    body = re.sub(r"```.*?```|<script\b.*?</script>", "", body, flags=re.DOTALL | re.IGNORECASE)
    blocks: set[str] = set()
    for paragraph in re.split(r"\n\s*\n", body):
        stripped = paragraph.strip()
        if not stripped or stripped.startswith(("#", "|", "-", "*", "!", "<")):
            continue
        stripped = re.sub(r"\[[^\]]+\]\([^)]+\)", "", stripped)
        words = re.findall(r"[A-Za-zÀ-ÖØ-öø-ÿ]{2,}", stripped)
        if len(words) >= 8:
            blocks.add(" ".join(words).casefold())
    return blocks


def validate(source: Path, targets: list[Path], source_locale: str) -> tuple[list[str], str]:
    errors: list[str] = []
    source_text = source.read_text(encoding="utf-8")
    source_front, source_body = frontmatter(source_text)
    source_structure = structure(source_body)
    if source_structure["invalid_schema"]:
        errors.append("source contains invalid JSON-LD")
    locales = [source_locale] + [target.parent.name for target in targets]
    if len(set(locales)) != len(locales):
        errors.append("target locale directory names must be unique")
    expected = set(locales)
    canonical_map = alternates(source_front)
    if set(canonical_map) != expected:
        errors.append(f"source alternates keys must equal {sorted(expected)} (found {sorted(canonical_map)})")

    source_prose = prose_blocks(source_body)
    for target, locale in zip(targets, locales[1:]):
        target_text = target.read_text(encoding="utf-8")
        target_front, target_body = frontmatter(target_text)
        target_map = alternates(target_front)
        if target_map != canonical_map:
            errors.append(f"{locale}: alternates map is not reciprocal/identical to source")
        target_structure = structure(target_body)
        for key, label in (
            ("heading_levels", "heading level sequence"),
            ("code_blocks", "code block content"),
            ("table_widths", "table structure"),
            ("image_targets", "image targets"),
            ("schema_types", "JSON-LD type structure"),
        ):
            if target_structure[key] != source_structure[key]:
                errors.append(f"{locale}: {label} differs from source")
        if target_structure["invalid_schema"]:
            errors.append(f"{locale}: contains invalid JSON-LD")
        exact = sorted(source_prose.intersection(prose_blocks(target_body)))
        if exact:
            errors.append(
                f"{locale}: {len(exact)} long prose block(s) remain identical to source; first: {exact[0][:90]}"
            )
        for url in re.findall(r"(?<!!)\[[^\]]+\]\(([^)\s]+)", target_body):
            parsed = urllib.parse.urlparse(url)
            if parsed.scheme and parsed.scheme not in {"https", "mailto", "tel"}:
                errors.append(f"{locale}: unsupported or insecure link scheme: {url}")

    digest = hashlib.sha256()
    for path in sorted([source, *targets], key=lambda item: item.as_posix()):
        digest.update(path.as_posix().encode("utf-8"))
        digest.update(path.read_bytes())
    return errors, digest.hexdigest()


def self_test() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        source = root / "post.md"
        target_dir = root / "es"
        target_dir.mkdir()
        target = target_dir / "post.md"
        front_en = """---
alternates:
  en: /blog/post
  es: /es/blog/post
---
"""
        front_es = front_en
        source.write_text(front_en + "# Guide\n\n## Details\n\nThis long source paragraph explains the complete workflow with several concrete useful details.\n\n```js\nrun();\n```\n", encoding="utf-8")
        target.write_text(front_es + "# Guía\n\n## Detalles\n\nEste párrafo explica el flujo completo con varios detalles concretos y útiles para todos.\n\n```js\nrun();\n```\n", encoding="utf-8")
        assert not validate(source, [target], "en")[0]
        target.write_text(source.read_text(), encoding="utf-8")
        assert validate(source, [target], "en")[0]
    print("[OK] translation_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?")
    parser.add_argument("targets", nargs="*")
    parser.add_argument("--source-locale", default="en")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.source or not args.targets:
        parser.error("source and at least one target are required")
    source, targets = Path(args.source), [Path(item) for item in args.targets]
    errors, digest = validate(source, targets, args.source_locale)
    print("=== TRANSLATION CONTRACT ===")
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
