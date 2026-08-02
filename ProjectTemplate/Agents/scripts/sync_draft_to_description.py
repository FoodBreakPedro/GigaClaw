#!/usr/bin/env python3
"""Parses a markdown post file (frontmatter + body) and updates a GigaClaw ticket's
description with AD-7 formatted frontmatter and attaches the 'ready-for-cms' label.

Usage:
    python3 .agents/scripts/sync_draft_to_description.py \
      --project <project-slug> --ticket <ticket-id> --author blog-seo \
      --file content/posts/my-post.md [--slug my-post]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path


def parse_post_file(filepath: Path, override_slug: str | None = None) -> tuple[dict[str, str], str]:
    content = filepath.read_text(encoding="utf-8")
    parts = re.split(r'^---\s*$', content, flags=re.MULTILINE)

    yaml_dict: dict[str, str] = {}
    if len(parts) >= 3:
        yaml_text = parts[1]
        body = '---'.join(parts[2:]).strip()
        for line in yaml_text.splitlines():
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            if ':' in line:
                k, v = line.split(':', 1)
                yaml_dict[k.strip()] = v.strip().strip('"\'')
    else:
        body = content.strip()

    title = yaml_dict.get('title', yaml_dict.get('og_title', 'Untitled Post'))
    desc = yaml_dict.get('description', yaml_dict.get('og_description', yaml_dict.get('excerpt', '')))
    slug = override_slug or yaml_dict.get('slug', filepath.stem)
    contentType = yaml_dict.get('contentType', 'article')

    tags = yaml_dict.get('tags', '')
    primary_keyword = slug
    if tags:
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


def sync_draft_to_ticket(api_url: str, project: str, ticket: int, author: str, file_path: Path, slug_val: str | None = None) -> None:
    root = api_url.rstrip("/")
    if root.endswith("/api"):
        root = root[:-4]

    ticket_url = f"{root}/api/projects/{urllib.parse.quote(project, safe='')}/tickets/{ticket}"

    _, ad7_desc = parse_post_file(file_path, slug_val)

    # 1. Fetch current ticket to get labels
    req_get = urllib.request.Request(ticket_url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req_get, timeout=20) as resp:
        ticket_data = json.loads(resp.read())

    # Fetch project labels
    labels_url = f"{root}/api/projects/{urllib.parse.quote(project, safe='')}/labels"
    req_labels = urllib.request.Request(labels_url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req_labels, timeout=20) as resp:
        labels_list = json.loads(resp.read())

    ready_label_id = None
    for lbl in labels_list:
        if lbl.get("name") == "ready-for-cms":
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

    if ready_label_id and ready_label_id not in current_label_ids:
        current_label_ids.append(ready_label_id)

    # 3. Patch description and labels
    patch_payload = {
        "author": author,
        "description": ad7_desc,
        "labelIds": current_label_ids
    }

    req_patch = urllib.request.Request(
        ticket_url,
        data=json.dumps(patch_payload).encode("utf-8"),
        method="PATCH",
        headers={"Content-Type": "application/json", "Accept": "application/json"}
    )
    with urllib.request.urlopen(req_patch, timeout=20) as resp:
        print(f"[OK] Synced {file_path.name} to ticket #{ticket} description & attached ready-for-cms (HTTP {resp.status})")


def main() -> int:
    parser = argparse.ArgumentParser(description="Sync post file to ticket AD-7 description")
    parser.add_argument("--api", default=os.environ.get("GIGACLAW_API_URL", "http://127.0.0.1:5230"))
    parser.add_argument("--project", required=True)
    parser.add_argument("--ticket", type=int, required=True)
    parser.add_argument("--author", required=True)
    parser.add_argument("--file", required=True)
    parser.add_argument("--slug")

    args = parser.parse_args()
    file_path = Path(args.file)
    if not file_path.is_file():
        print(f"[ERROR] Post file missing or unreadable: {file_path}", file=sys.stderr)
        return 2

    sync_draft_to_ticket(args.api, args.project, args.ticket, args.author, file_path, args.slug)
    return 0

if __name__ == "__main__":
    sys.exit(main())
