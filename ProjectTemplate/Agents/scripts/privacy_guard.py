#!/usr/bin/env python3
"""
privacy_guard.py - Privacy boundary and secret detector for GigaClaw content and outbound actions.
"""

import sys
import re

SECRET_PATTERNS = [
    (r'sk-[a-zA-Z0-9]{32,}', "OpenAI/Anthropic API Key"),
    (r'ghp_[a-zA-Z0-9]{36}', "GitHub Personal Access Token"),
    (r'gho_[a-zA-Z0-9]{36}', "GitHub OAuth Token"),
    (r'AIzaSy[a-zA-Z0-9_-]{33}', "Google AI Studio / GCP Key"),
    (r'90-Personal/', "Private Vault Boundary Violation"),
    (r'Private/', "Sealed Private Path Violation")
]

def scan_privacy(text):
    violations = []
    for pattern, label in SECRET_PATTERNS:
        matches = re.findall(pattern, text)
        if matches:
            violations.append((label, len(matches)))
    return violations

def main():
    if len(sys.argv) < 2:
        print("Usage: python3 privacy_guard.py <path-to-file-or-text>")
        sys.exit(1)

    filepath = sys.argv[1]
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()
    except Exception as e:
        content = filepath

    violations = scan_privacy(content)
    print("=== PRIVACY GUARD AUDIT REPORT ===")
    if violations:
        print("[FAIL] Secret or Privacy Boundary Violations Found:")
        for label, count in violations:
            print(f"  - {label}: {count} occurrence(s)")
        sys.exit(1)
    else:
        print("[OK] Zero secrets or privacy boundary violations detected.")

if __name__ == "__main__":
    main()
