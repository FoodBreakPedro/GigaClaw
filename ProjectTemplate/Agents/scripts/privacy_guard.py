#!/usr/bin/env python3
"""
privacy_guard.py - Privacy boundary and secret detector for GigaClaw content and outbound actions.

Exit codes: 0 = clean, 1 = violations found, 2 = could not read one or more files.
Usage: python3 privacy_guard.py <file> [<file> ...]
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

SECRET_PATTERNS: list[tuple[str, str]] = [
    (r'sk-ant-[a-zA-Z0-9_-]{24,}', "Anthropic API Key"),
    (r'sk-[a-zA-Z0-9]{32,}', "OpenAI API Key"),
    (r'ghp_[a-zA-Z0-9]{36}', "GitHub Personal Access Token"),
    (r'gho_[a-zA-Z0-9]{36}', "GitHub OAuth Token"),
    (r'AIzaSy[a-zA-Z0-9_-]{33}', "Google AI Studio / GCP Key"),
    (r'AKIA[0-9A-Z]{16}', "AWS Access Key ID"),
    (r'xox[baprs]-[0-9a-zA-Z-]{10,}', "Slack Token"),
    (r'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----', "Private Key Material"),
]


def scan_privacy(text: str) -> list[tuple[str, int, str]]:
    """Return [(label, line_number, match_excerpt)] for every hit."""
    violations: list[tuple[str, int, str]] = []
    for line_no, line in enumerate(text.splitlines(), 1):
        for pattern, label in SECRET_PATTERNS:
            for m in re.finditer(pattern, line):
                excerpt = m.group(0)[:12] + "..." if len(m.group(0)) > 15 else m.group(0)
                violations.append((label, line_no, excerpt))
    return violations


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python3 privacy_guard.py <file> [<file> ...]")
        sys.exit(2)

    print("=== PRIVACY GUARD AUDIT REPORT ===")
    any_violation = False
    any_unreadable = False

    for filepath in sys.argv[1:]:
        try:
            content = Path(filepath).read_text(encoding='utf-8', errors='replace')
        except Exception as e:
            # An unreadable artifact is NOT approved - fail loudly, never scan the path string.
            print(f"[UNREADABLE] {filepath}: {e}")
            any_unreadable = True
            continue

        violations = scan_privacy(content)
        if violations:
            any_violation = True
            print(f"[FAIL] {filepath}:")
            for label, line_no, excerpt in violations:
                print(f"  - line {line_no}: {label} ({excerpt})")
        else:
            print(f"[OK] {filepath}: no secrets or privacy boundary violations detected.")

    if any_unreadable:
        sys.exit(2)
    if any_violation:
        sys.exit(1)
    sys.exit(0)


if __name__ == "__main__":
    main()
