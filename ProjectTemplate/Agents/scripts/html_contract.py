#!/usr/bin/env python3
"""Static HTML, accessibility, responsive-layout, and contrast validator.

Modes:
  --kind lead-magnet  requires 5-7 H2/section units and print CSS.
  --kind ui           requires Hallmark macrostructure/tokens/interactions.

This deliberately complements, rather than claims to replace, browser renders
and an accessibility engine.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
import urllib.parse
from html.parser import HTMLParser
from pathlib import Path


class AuditParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.tags: list[tuple[str, dict[str, str], int]] = []
        self.stack: list[tuple[str, dict[str, str], list[str], int]] = []
        self.completed: list[tuple[str, dict[str, str], str, int]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {key.lower(): (value or "") for key, value in attrs}
        line = self.getpos()[0]
        self.tags.append((tag.lower(), values, line))
        self.stack.append((tag.lower(), values, [], line))

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {key.lower(): (value or "") for key, value in attrs}
        line = self.getpos()[0]
        self.tags.append((tag.lower(), values, line))
        self.completed.append((tag.lower(), values, "", line))

    def handle_data(self, data: str) -> None:
        for _, _, chunks, _ in self.stack:
            chunks.append(data)

    def handle_endtag(self, tag: str) -> None:
        for index in range(len(self.stack) - 1, -1, -1):
            if self.stack[index][0] == tag.lower():
                current = self.stack.pop(index)
                self.completed.append((current[0], current[1], "".join(current[2]).strip(), current[3]))
                break


def parse_hex(value: str) -> tuple[float, float, float] | None:
    match = re.fullmatch(r"#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})", value.strip())
    if not match:
        return None
    digits = match.group(1)
    if len(digits) == 3:
        digits = "".join(char * 2 for char in digits)
    return tuple(int(digits[index : index + 2], 16) / 255 for index in (0, 2, 4))  # type: ignore[return-value]


def luminance(color: tuple[float, float, float]) -> float:
    channels = [value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4 for value in color]
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]


def contrast(first: str, second: str) -> float | None:
    left, right = parse_hex(first), parse_hex(second)
    if left is None or right is None:
        return None
    high, low = sorted((luminance(left), luminance(right)), reverse=True)
    return round((high + 0.05) / (low + 0.05), 2)


def validate(path: Path, kind: str) -> tuple[list[str], list[str], str]:
    text = path.read_text(encoding="utf-8")
    parser = AuditParser()
    parser.feed(text)
    errors: list[str] = []
    notes: list[str] = []
    tags = parser.tags
    completed = parser.completed

    html = next((attrs for tag, attrs, _ in tags if tag == "html"), {})
    if not html.get("lang"):
        errors.append("html element requires a lang attribute")
    if not any(tag == "meta" and attrs.get("name", "").lower() == "viewport" for tag, attrs, _ in tags):
        errors.append("missing responsive viewport meta tag")
    titles = [content for tag, _, content, _ in completed if tag == "title" and content]
    if len(titles) != 1:
        errors.append("document requires exactly one non-empty title")
    if not any(tag == "main" for tag, _, _ in tags):
        errors.append("document requires a main landmark")
    h1s = [item for item in completed if item[0] == "h1" and item[2]]
    if len(h1s) != 1:
        errors.append("document requires exactly one non-empty H1")

    ids = {attrs.get("id") for _, attrs, _ in tags if attrs.get("id")}
    labels_for = {attrs.get("for") for tag, attrs, _ in tags if tag == "label" and attrs.get("for")}
    for tag, attrs, line in tags:
        if tag == "img" and not attrs.get("alt", "").strip():
            errors.append(f"line {line}: image requires non-empty alt text")
        if tag in {"input", "select", "textarea"}:
            field_id = attrs.get("id")
            if not attrs.get("aria-label") and (not field_id or field_id not in labels_for):
                errors.append(f"line {line}: {tag} requires a label or aria-label")
        if tag == "a":
            href = attrs.get("href", "").strip()
            if not href:
                errors.append(f"line {line}: anchor requires href")
            elif href.startswith("#") and href[1:] not in ids:
                errors.append(f"line {line}: fragment target does not exist: {href}")
            else:
                parsed = urllib.parse.urlparse(href)
                if parsed.scheme and parsed.scheme not in {"https", "mailto", "tel"}:
                    errors.append(f"line {line}: unsupported or insecure link scheme: {href}")
                elif not parsed.scheme and not href.startswith(("#", "/")):
                    local = (path.parent / urllib.parse.unquote(parsed.path)).resolve()
                    if not local.exists():
                        errors.append(f"line {line}: local link target does not exist: {href}")
    for tag, attrs, content, line in completed:
        if tag in {"a", "button"} and not content and not attrs.get("aria-label"):
            errors.append(f"line {line}: {tag} requires accessible text or aria-label")

    css = "\n".join(content for tag, _, content, _ in completed if tag == "style")
    if not css:
        errors.append("self-contained artifact requires an inline style block")
    if "@media" not in css:
        errors.append("responsive CSS requires at least one @media query")

    custom = dict(re.findall(r"(--[\w-]+)\s*:\s*([^;}{]+)", css))
    if "--text" in custom and "--bg" in custom:
        ratio = contrast(custom["--text"], custom["--bg"])
        if ratio is None:
            notes.append("static audit could not resolve --text/--bg contrast; verify computed styles in browser")
        else:
            notes.append(f"computed --text/--bg contrast: {ratio}:1")
            if ratio < 4.5:
                errors.append(f"--text/--bg contrast is {ratio}:1; normal text requires 4.5:1")

    if kind == "lead-magnet":
        section_count = sum(1 for tag, _, _, _ in completed if tag == "h2")
        if not 5 <= section_count <= 7:
            errors.append(f"lead magnet requires 5-7 H2 sections (found {section_count})")
        if "@media print" not in re.sub(r"\s+", " ", css).lower():
            errors.append("lead magnet requires @media print CSS")
    else:
        macro = re.search(r"/\*\s*macrostructure:\s*([a-z0-9-]+-v\d+)\s*\*/", css)
        if not macro:
            errors.append("UI CSS requires versioned macrostructure comment")
        for token in ("--bg", "--text", "--accent", "--border", "--font-heading", "--font-body"):
            if token not in custom:
                errors.append(f"UI design system is missing {token}")
        if ":focus" not in css and ":focus-visible" not in css:
            errors.append("UI CSS requires a focus state")
        if ":hover" not in css:
            errors.append("UI CSS requires a hover state")
        if "transition" not in css:
            errors.append("UI CSS requires at least one transition")

    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    return errors, notes, digest


def self_test() -> None:
    import tempfile

    html = """<!doctype html><html lang="en"><head><title>Test</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>/* macrostructure: split-editorial-v1 */:root{--bg:#fff;--text:#111;--accent:#900;
--border:#777;--font-heading:serif;--font-body:sans-serif}a:hover{opacity:.8}
a:focus-visible{outline:2px solid}a{transition:opacity .2s}@media(max-width:600px){main{display:block}}</style>
</head><body><main><h1>Test</h1><a href="https://docs.test">Read docs</a></main></body></html>"""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "ui.html"
        path.write_text(html, encoding="utf-8")
        assert not validate(path, "ui")[0]
        path.write_text(html.replace('lang="en"', ""), encoding="utf-8")
        assert validate(path, "ui")[0]
    print("[OK] html_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--kind", choices=("lead-magnet", "ui"), required=False, default="ui")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")
    path = Path(args.path)
    errors, notes, digest = validate(path, args.kind)
    print(f"=== HTML CONTRACT: {path} ({args.kind}) ===")
    print(f"artifact-sha256:{digest}")
    for note in notes:
        print(f"[INFO] {note}")
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
