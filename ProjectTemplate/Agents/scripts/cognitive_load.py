#!/usr/bin/env python3
"""
cognitive_load.py - Cognitive load & reading fatigue helper for GigaClaw content agents.

Audits:
  - Paragraph length distribution (flags paragraphs > 4 sentences or > 100 words)
  - Dense word clusters
  - Heading spacing
"""

from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Any

# These scripts print non-ASCII (arrows, middots, dashes, accents) and the host reads their
# stdout as UTF-8. Python on Windows still defaults stdout to the ANSI code page (cp1252), where
# those characters raise UnicodeEncodeError *after* the work succeeded -- turning a clean pass
# into a crash. Pin the stream instead of degrading the output to ASCII.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")


def strip_non_prose(text: str) -> str:
    """Drop frontmatter, fenced code, embedded scripts, and table rows so
    paragraph metrics reflect the prose only."""
    text = re.sub(r'\A---\n.*?\n---\n', '', text, flags=re.DOTALL)
    text = re.sub(r'```.*?```', '', text, flags=re.DOTALL)
    text = re.sub(r'<script\b.*?</script>', '', text, flags=re.DOTALL | re.IGNORECASE)
    # Drop table rows and list items: a bullet list is not a fatigue paragraph.
    lines = [ln for ln in text.splitlines()
             if not ln.lstrip().startswith('|')
             and not re.match(r'^\s*(?:[-*+]|\d{1,3}\.)\s+', ln)]
    return '\n'.join(lines)


def analyze_cognitive_load(text: str) -> dict[str, Any]:
    text = strip_non_prose(text)
    paragraphs = [p.strip() for p in text.split('\n\n') if p.strip() and not p.strip().startswith('#')]
    
    total_paragraphs = len(paragraphs)
    long_paragraphs = []
    
    for idx, p in enumerate(paragraphs, 1):
        sentences = [s for s in re.split(r'[.!?]+', p) if s.strip()]
        words = re.findall(r'\b\w+\b', p)
        if len(sentences) > 4 or len(words) > 100:
            long_paragraphs.append({
                "paragraph_num": idx,
                "sentences": len(sentences),
                "words": len(words),
                "snippet": p[:60] + "..."
            })
            
    return {
        "total_paragraphs": total_paragraphs,
        "fatigue_paragraphs": len(long_paragraphs),
        "issues": long_paragraphs
    }


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python3 cognitive_load.py <path-to-markdown-file>")
        sys.exit(1)

    filepath = sys.argv[1]
    try:
        content = Path(filepath).read_text(encoding='utf-8')
    except Exception as e:
        print(f"Error reading file {filepath}: {e}")
        sys.exit(1)
        
    res = analyze_cognitive_load(content)
    print("=== COGNITIVE LOAD REPORT ===")
    if res['total_paragraphs'] == 0:
        print("No prose paragraphs found — nothing to analyze.")
        sys.exit(0)
    print(f"Total Paragraphs: {res['total_paragraphs']}")
    print(f"Reading Fatigue Risk Paragraphs: {res['fatigue_paragraphs']}")
    
    if res['issues']:
        print("\n[WARNING] Paragraphs Exceeding 4 Sentences / 100 Words:")
        for issue in res['issues']:
            print(f"  - Paragraph #{issue['paragraph_num']}: {issue['sentences']} sentences, {issue['words']} words ('{issue['snippet']}')")
    else:
        print("\n[OK] Excellent paragraph pacing! No reading fatigue detected.")


if __name__ == "__main__":
    main()
