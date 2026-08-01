#!/usr/bin/env python3
"""Content-health dashboard tile: prints a kpi-grid JSON array to stdout.

The dashboard runner captures stdout into output.json (cwd = workspace root).
Scores are computed by running the bundled GEO gate script per post - no
hardcoded numbers; honest zeros when there is nothing to measure.
"""
import glob
import json
import re
import subprocess
import sys


def geo_score(path):
    try:
        proc = subprocess.run(
            [sys.executable, ".agents/scripts/ai_citation_score.py", path],
            capture_output=True, text=True, timeout=30,
        )
    except subprocess.TimeoutExpired:
        print(f"geo_score: timed out scoring {path}", file=sys.stderr)
        return None
    except (OSError, subprocess.SubprocessError) as error:
        print(f"geo_score: error running ai_citation_score.py for {path}: {error}", file=sys.stderr)
        return None

    if proc.returncode != 0:
        print(f"geo_score: ai_citation_score.py failed for {path} (exit {proc.returncode}): {proc.stderr.strip()}", file=sys.stderr)
        return None

    m = re.search(r"Overall GEO Score:\s*(\d+)\s*/\s*100", proc.stdout)
    if m is None:
        print(f"geo_score: no score found in output for {path}", file=sys.stderr)
    return int(m.group(1)) if m else None


def main():
    # Top-level only: locale variants under content/posts/<locale>/ are translations,
    # and the English-only GEO heuristic would misscore them.
    posts = sorted(glob.glob("content/posts/*.md"))
    scores = [s for s in (geo_score(p) for p in posts) if s is not None]

    cards = [
        {"label": "Total Posts", "value": str(len(posts))},
        {"label": "Avg GEO Score", "value": f"{round(sum(scores) / len(scores))}/100" if scores else "n/a"},
        {"label": "Posts Scored", "value": str(len(scores))},
    ]
    print(json.dumps(cards))


if __name__ == "__main__":
    main()
