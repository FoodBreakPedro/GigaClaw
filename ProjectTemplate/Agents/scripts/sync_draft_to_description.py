#!/usr/bin/env python3
"""Parses a markdown post file (frontmatter + body) and updates a GigaClaw ticket's
description with AD-7 formatted frontmatter and attaches the 'ready-for-cms' label.

Usage:
    python3 .agents/scripts/sync_draft_to_description.py \
      --project <project-slug> --ticket <ticket-id> --author blog-seo \
      --file content/posts/my-post.md [--slug my-post]

The GigaClaw API here is local-only (127.0.0.1, default install) and unauthenticated,
matching agent_ticket.py, which talks to the same API with no auth headers.

Exit codes: 0 = success, 2 = invalid input / network / API failure (mirrors
agent_ticket.py's convention; this script has no marker-style "not found" case
so exit code 1 is unused).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


class ApiError(RuntimeError):
    pass


def _strip_quotes(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in ('"', "'"):
        return value[1:-1]
    return value


def _parse_block_list(block: list[str]) -> list[str]:
    items: list[str] = []
    for line in block:
        text = line.strip()
        if not text or text.startswith('#'):
            continue
        if text.startswith('- '):
            items.append(_strip_quotes(text[2:].strip()))
        elif text == '-':
            items.append('')
    return items


def _fold_block_scalar(indicator: str, block: list[str]) -> str:
    """Handle '>' (folded) and '|' (literal) block scalars, chomping indicators
    (-, +) included in `indicator` but not otherwise distinguished — we always
    drop trailing blank lines, which is close enough to YAML's default 'clip'
    behaviour for frontmatter values that feed straight into a one-line ticket
    description."""
    folded = indicator.startswith('>')
    content_lines = list(block)
    while content_lines and not content_lines[-1].strip():
        content_lines.pop()
    indents = [len(line) - len(line.lstrip(' ')) for line in content_lines if line.strip()]
    base_indent = min(indents) if indents else 0
    dedented = [line[base_indent:] if len(line) >= base_indent else line.strip() for line in content_lines]
    if folded:
        return ' '.join(part.strip() for part in dedented if part.strip())
    return '\n'.join(dedented)


def _parse_frontmatter(yaml_text: str) -> dict[str, str | list[str]]:
    """Minimal, dependency-free YAML subset parser for post frontmatter.

    No sibling script under this directory depends on PyYAML (or any third-party
    package — there is no requirements file for these stdlib-only scripts), so
    this stays dependency-free rather than introducing the only external import
    in the folder.

    Supports what post frontmatter actually uses: single-line scalars (quoted
    or bare, including quoted values containing colons), block-style lists
    (`key:` followed by `  - item` lines), inline flow lists (`key: [a, b]`,
    kept as a raw string — same as before), and folded/literal block scalars
    (`key: >` / `key: |`, optionally with chomping indicators, followed by
    indented continuation lines).

    Indentation is what distinguishes a new top-level key from a continuation
    of the previous one; the old line-by-line `line.strip()` parser threw that
    signal away, so any indented line with a colon in it (a list item's
    quoted value, a nested mapping's own `key: value` lines) was silently
    treated as a new top-level field and could clobber `title`/`description`
    or `tags`. A nested mapping's contents are opaque here and skipped, rather
    than flattened into the wrong key.
    """
    lines = yaml_text.splitlines()
    values: dict[str, str | list[str]] = {}
    i = 0
    n = len(lines)
    while i < n:
        raw = lines[i]
        stripped = raw.strip()
        if not stripped or stripped.startswith('#') or raw[:1].isspace():
            # Blank, a comment, or a stray indented line with no owning key at
            # this point in the walk (e.g. malformed input) — skip it.
            i += 1
            continue
        if ':' not in raw:
            i += 1
            continue
        key, _, rest = raw.partition(':')
        key = key.strip()
        value = rest.strip()
        i += 1

        block: list[str] = []
        while i < n:
            cont = lines[i]
            if not cont.strip() or cont[:1].isspace():
                block.append(cont)
                i += 1
                continue
            break

        if value.startswith(('>', '|')):
            values[key] = _fold_block_scalar(value, block)
        elif not value and any(item.strip().startswith('-') for item in block if item.strip()):
            values[key] = _parse_block_list(block)
        elif not value and block and any(':' in item for item in block if item.strip() and not item.strip().startswith('#')):
            # Nested mapping — no field this script reads lives under one; skip
            # rather than let its inner "key: value" lines masquerade as
            # top-level frontmatter fields (see docstring).
            continue
        else:
            values[key] = _strip_quotes(value)
    return values


def parse_post_file(filepath: Path, override_slug: str | None = None) -> tuple[dict[str, str | list[str]], str]:
    content = filepath.read_text(encoding="utf-8")
    parts = re.split(r'^---\s*$', content, flags=re.MULTILINE)

    yaml_dict: dict[str, str | list[str]] = {}
    if len(parts) >= 3:
        yaml_text = parts[1]
        body = '---'.join(parts[2:]).strip()
        yaml_dict = _parse_frontmatter(yaml_text)
    else:
        body = content.strip()

    title = yaml_dict.get('title', yaml_dict.get('og_title', 'Untitled Post'))
    desc = yaml_dict.get('description', yaml_dict.get('og_description', yaml_dict.get('excerpt', '')))
    slug = override_slug or yaml_dict.get('slug', filepath.stem)
    contentType = yaml_dict.get('contentType', 'article')

    tags = yaml_dict.get('tags', '')
    primary_keyword = slug
    if isinstance(tags, list):
        if tags:
            first_tag = str(tags[0]).strip().strip('"\'')
            if first_tag:
                primary_keyword = first_tag
    elif tags:
        # Inline flow form, e.g. "tags: [foo, bar]" or "tags: foo, bar" — kept
        # as a raw string by the parser above, same as before C2.
        first_tag = re.sub(r'[\[\]]', '', tags).split(',')[0].strip().strip('"\'')
        if first_tag:
            primary_keyword = first_tag

    ad7_desc = f"""---
title: {title}
slug: {slug}
excerpt: {desc}
contentType: {contentType}
seo:
  title: {title}
  description: {desc}
  primaryKeyword: {primary_keyword}
---

{body}"""

    return yaml_dict, ad7_desc


def _api_get(url: str) -> Any:
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            raw = response.read()
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")[:1000]
        raise ApiError(f"GET {url} returned HTTP {exc.code}: {body_text}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise ApiError(f"GET {url} failed: {exc}") from exc
    try:
        return json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ApiError(f"GET {url} returned invalid JSON: {exc.msg}") from exc


def _api_patch(url: str, payload: dict[str, Any]) -> int:
    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        method="PATCH",
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")[:1000]
        raise ApiError(f"PATCH {url} returned HTTP {exc.code}: {body_text}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise ApiError(f"PATCH {url} failed: {exc}") from exc


def sync_draft_to_ticket(api_url: str, project: str, ticket: int, author: str, file_path: Path, slug_val: str | None = None) -> None:
    root = api_url.rstrip("/")
    if root.endswith("/api"):
        root = root[:-4]

    ticket_url = f"{root}/api/projects/{urllib.parse.quote(project, safe='')}/tickets/{ticket}"
    labels_url = f"{root}/api/projects/{urllib.parse.quote(project, safe='')}/labels"

    _, ad7_desc = parse_post_file(file_path, slug_val)

    # 1. Fetch current ticket to get labels
    ticket_data = _api_get(ticket_url)
    if not isinstance(ticket_data, dict):
        raise ApiError(f"GET {ticket_url} did not return a JSON object")

    # Fetch project labels
    labels_list = _api_get(labels_url)
    if not isinstance(labels_list, list):
        raise ApiError(f"GET {labels_url} did not return a JSON array")

    ready_label_id = None
    for lbl in labels_list:
        if isinstance(lbl, dict) and lbl.get("name") == "ready-for-cms":
            ready_label_id = lbl.get("id")
            break

    # 2. Get existing label IDs on the ticket
    existing_labels = ticket_data.get("labels", ticket_data.get("Labels", []))
    current_label_ids = []
    if isinstance(existing_labels, list):
        for l in existing_labels:
            if isinstance(l, dict):
                lid = l.get("id", l.get("Id"))
                if lid is not None:
                    current_label_ids.append(lid)
            elif isinstance(l, int):
                current_label_ids.append(l)

    label_attached = ready_label_id is not None
    if label_attached and ready_label_id not in current_label_ids:
        current_label_ids.append(ready_label_id)

    # 3. Patch description and labels
    patch_payload = {
        "author": author,
        "description": ad7_desc,
        "labelIds": current_label_ids
    }
    status = _api_patch(ticket_url, patch_payload)

    if label_attached:
        print(f"[OK] Synced {file_path.name} to ticket #{ticket} description & attached ready-for-cms (HTTP {status})")
    else:
        print(f"[WARN] 'ready-for-cms' label not found in project {project!r}; description synced without it")
        print(f"[OK] Synced {file_path.name} to ticket #{ticket} description (HTTP {status})")


def self_test() -> None:
    import tempfile

    # List-form tags + a folded block-scalar description + a quoted value
    # containing a colon, all in one frontmatter block — the three cases the
    # old line-by-line parser mishandled.
    post = """---
title: "A guide: the basics"
slug: a-guide
description: >
  This spans
  multiple lines
  and should fold.
tags:
  - primary-tag
  - secondary-tag
contentType: article
---

Body text.
"""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "post.md"
        path.write_text(post, encoding="utf-8")
        meta, ad7 = parse_post_file(path)
        assert meta["title"] == "A guide: the basics", meta["title"]
        assert meta["description"] == "This spans multiple lines and should fold.", meta["description"]
        assert meta["tags"] == ["primary-tag", "secondary-tag"], meta["tags"]
        assert "primaryKeyword: primary-tag" in ad7
        assert "excerpt: This spans multiple lines and should fold." in ad7
        assert "title: A guide: the basics" in ad7

        # Literal block scalar preserves newlines instead of folding them.
        literal = post.replace("description: >", "description: |")
        path.write_text(literal, encoding="utf-8")
        meta, _ = parse_post_file(path)
        assert meta["description"] == "This spans\nmultiple lines\nand should fold.", meta["description"]

        # A nested mapping must not clobber the top-level title/description.
        nested = """---
title: Real Title
description: Real description.
seo:
  title: Should not win
  description: Should not win either
slug: nested-case
---

Body.
"""
        path.write_text(nested, encoding="utf-8")
        meta, _ = parse_post_file(path)
        assert meta["title"] == "Real Title", meta["title"]
        assert meta["description"] == "Real description.", meta["description"]

        # No tags at all: primary_keyword falls back to the slug.
        no_tags = """---
title: No Tags Here
slug: fallback-slug
---

Body.
"""
        path.write_text(no_tags, encoding="utf-8")
        _, ad7_no_tags = parse_post_file(path)
        assert "primaryKeyword: fallback-slug" in ad7_no_tags

        # Inline flow-style tags stay supported (raw string, comma-split).
        inline = post.replace("tags:\n  - primary-tag\n  - secondary-tag", "tags: [inline-tag, other]")
        path.write_text(inline, encoding="utf-8")
        meta, ad7_inline = parse_post_file(path)
        assert meta["tags"] == "[inline-tag, other]", meta["tags"]
        assert "primaryKeyword: inline-tag" in ad7_inline

    print("[OK] sync_draft_to_description self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description="Sync post file to ticket AD-7 description")
    parser.add_argument("--api", default=os.environ.get("GIGACLAW_API_URL", "http://127.0.0.1:5230"))
    parser.add_argument("--project")
    parser.add_argument("--ticket", type=int)
    parser.add_argument("--author")
    parser.add_argument("--file")
    parser.add_argument("--slug")
    parser.add_argument("--self-test", action="store_true")

    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0

    if not args.project or args.ticket is None or not args.author or not args.file:
        parser.error("--project, --ticket, --author, and --file are required unless --self-test is used")

    file_path = Path(args.file)
    if not file_path.is_file():
        print(f"[ERROR] Post file missing or unreadable: {file_path}", file=sys.stderr)
        return 2

    sync_draft_to_ticket(args.api, args.project, args.ticket, args.author, file_path, args.slug)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ApiError, OSError, ValueError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
