#!/usr/bin/env python3
"""Validate a publishable Markdown article contract.

Checks frontmatter, heading hierarchy, image alt text, local/external links,
JSON-LD syntax/types, and required OpenGraph metadata. By default external
links are syntax-checked only; pass --check-external to make network failures
fatal.

Exit codes: 0 = valid, 1 = contract violations, 2 = unreadable/invalid usage.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


SCRIPT_RE = re.compile(
    r"<script\b[^>]*\btype\s*=\s*['\"]application/ld\+json['\"][^>]*>(.*?)</script>",
    re.IGNORECASE | re.DOTALL,
)
LINK_RE = re.compile(r"(?<!!)\[([^\]]+)\]\(([^)\s]+)(?:\s+['\"][^'\"]*['\"])?\)")
IMAGE_RE = re.compile(r"!\[([^\]]*)\]\(([^)\s]+)(?:\s+['\"][^'\"]*['\"])?\)")


def frontmatter(text: str) -> tuple[dict[str, str], str, list[str]]:
    errors: list[str] = []
    if not text.startswith("---\n"):
        return {}, text, ["missing YAML frontmatter opening delimiter"]
    end = text.find("\n---\n", 4)
    if end < 0:
        return {}, text, ["missing YAML frontmatter closing delimiter"]
    values: dict[str, str] = {}
    for line_no, line in enumerate(text[4:end].splitlines(), 2):
        if not line.strip() or line.startswith((" ", "\t", "#")):
            continue
        match = re.match(r"^([A-Za-z][\w-]*):\s*(.*?)\s*$", line)
        if not match:
            errors.append(f"frontmatter line {line_no} is not a simple key/value")
            continue
        key, value = match.groups()
        values[key] = value.strip().strip("'\"")
    return values, text[end + 5 :], errors


def schema_types(value: Any) -> set[str]:
    found: set[str] = set()
    if isinstance(value, dict):
        current = value.get("@type")
        if isinstance(current, str):
            found.add(current)
        elif isinstance(current, list):
            found.update(item for item in current if isinstance(item, str))
        for nested in value.values():
            found.update(schema_types(nested))
    elif isinstance(value, list):
        for nested in value:
            found.update(schema_types(nested))
    return found


def check_url(url: str, document: Path, check_external: bool) -> list[str]:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme in {"http", "https"}:
        if not parsed.netloc:
            return [f"external link has no host: {url}"]
        if parsed.netloc in {"example.com", "www.example.com"}:
            return [f"placeholder URL is not publishable: {url}"]
        if check_external:
            request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "GigaClaw-Validator/1.0"})
            try:
                with urllib.request.urlopen(request, timeout=10) as response:
                    if not 200 <= response.status < 400:
                        return [f"external link returned HTTP {response.status}: {url}"]
            except urllib.error.HTTPError as error:
                if error.code == 405:
                    try:
                        request = urllib.request.Request(
                            url, headers={"User-Agent": "GigaClaw-Validator/1.0", "Range": "bytes=0-0"}
                        )
                        with urllib.request.urlopen(request, timeout=10):
                            pass
                    except Exception as retry_error:
                        return [f"external link failed: {url} ({retry_error})"]
                else:
                    return [f"external link returned HTTP {error.code}: {url}"]
            except Exception as error:
                return [f"external link failed: {url} ({error})"]
        return []
    if parsed.scheme in {"mailto", "tel"} or url.startswith("#"):
        return []
    if parsed.scheme:
        return [f"unsupported link scheme: {url}"]
    path_text = urllib.parse.unquote(parsed.path)
    if path_text.startswith("/"):
        return []  # Site-root routes cannot be resolved from the artifact directory.
    target = (document.parent / path_text).resolve()
    return [] if target.exists() else [f"local link target does not exist: {url}"]


def validate_article(path: Path, check_external: bool = False) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    meta, body, errors = frontmatter(text)
    warnings: list[str] = []
    required = ("title", "description", "date", "author", "tags", "canonical", "og_title", "og_description", "og_image")
    for key in required:
        if not meta.get(key):
            errors.append(f"frontmatter is missing {key}")
    description = meta.get("description", "")
    if description and not 150 <= len(description) <= 160:
        errors.append(f"description must be 150-160 characters (found {len(description)})")
    date_text = meta.get("date")
    if date_text:
        try:
            dt.date.fromisoformat(date_text)
        except ValueError:
            errors.append("date must use YYYY-MM-DD")
    canonical = meta.get("canonical")
    if canonical:
        parsed = urllib.parse.urlparse(canonical)
        if parsed.scheme != "https" or not parsed.netloc or parsed.netloc == "example.com":
            errors.append("canonical must be a non-placeholder absolute HTTPS URL")
    for key in ("og_image",):
        value = meta.get(key)
        if value and not (value.startswith("/") or urllib.parse.urlparse(value).scheme == "https"):
            errors.append(f"{key} must be a root-relative path or absolute HTTPS URL")

    headings = [(len(mark), title.strip()) for mark, title in re.findall(r"^(#{1,6})\s+(.+)$", body, re.MULTILINE)]
    if sum(1 for level, _ in headings if level == 1) != 1:
        errors.append("article must contain exactly one H1")
    for previous, current in zip(headings, headings[1:]):
        if current[0] > previous[0] + 1:
            errors.append(f"heading hierarchy skips H{previous[0]} to H{current[0]} at '{current[1]}'")

    for alt, url in IMAGE_RE.findall(body):
        if not alt.strip():
            errors.append(f"image has empty alt text: {url}")
        errors.extend(check_url(url, path, False))
    for _, url in LINK_RE.findall(body):
        errors.extend(check_url(url, path, check_external))

    schemas = SCRIPT_RE.findall(body)
    if not schemas:
        errors.append("missing application/ld+json script")
    found_types: set[str] = set()
    for index, raw in enumerate(schemas, 1):
        try:
            found_types.update(schema_types(json.loads(raw)))
        except json.JSONDecodeError as error:
            errors.append(f"JSON-LD block {index} is invalid JSON: {error.msg}")
    if "BlogPosting" not in found_types:
        errors.append("JSON-LD must contain BlogPosting")
    heading_titles = [title.lower() for _, title in headings]
    if any("faq" in title or "frequently asked" in title for title in heading_titles) and "FAQPage" not in found_types:
        errors.append("FAQ section requires FAQPage JSON-LD")
    looks_like_howto = any(
        re.search(r"\b(how to|step-by-step|steps to)\b", title, re.IGNORECASE)
        for _, title in headings
    )
    if looks_like_howto and "HowTo" not in found_types:
        errors.append("step-by-step article requires HowTo JSON-LD")
    if "HowTo" in found_types and not looks_like_howto:
        warnings.append("HowTo schema exists but no how-to heading was detected")

    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    return {
        "path": path.as_posix(),
        "valid": not errors,
        "artifact_sha256": digest,
        "schema_types": sorted(found_types),
        "errors": errors,
        "warnings": warnings,
    }


def self_test() -> None:
    import tempfile

    description = "A" * 150
    article = f"""---
title: "A useful guide"
description: "{description}"
date: "2026-07-27"
author: "A. Writer"
tags: [testing]
canonical: "https://docs.test/blog/guide"
og_title: "A useful guide"
og_description: "{description}"
og_image: "/images/guide.png"
---
# A useful guide

## Summary

Testing is a repeatable verification process.

<script type="application/ld+json">
{{"@context":"https://schema.org","@type":"BlogPosting","headline":"A useful guide"}}
</script>
"""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "post.md"
        path.write_text(article, encoding="utf-8")
        assert validate_article(path)["valid"]
        path.write_text(article.replace('"BlogPosting"', '"Thing"'), encoding="utf-8")
        assert not validate_article(path)["valid"]
    print("[OK] content_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--check-external", action="store_true")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")
    result = validate_article(Path(args.path), args.check_external)
    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"=== CONTENT CONTRACT: {result['path']} ===")
        print(f"artifact-sha256:{result['artifact_sha256']}")
        for warning in result["warnings"]:
            print(f"[WARN] {warning}")
        for error in result["errors"]:
            print(f"[FAIL] {error}")
        print("[OK] contract passed" if result["valid"] else f"[FAIL] {len(result['errors'])} violation(s)")
    return 0 if result["valid"] else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
