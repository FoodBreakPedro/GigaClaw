#!/usr/bin/env python3
"""Deterministic email deliverability and consent-boundary validator.

Required Markdown layout:
  YAML keys: artifact_type, audience_relationship, outreach_basis
  ## Subject lines (exactly three numbered options)
  ## Body (exactly one `<!-- CTA -->` marker before the CTA)

Every sendable variant is checked separately: each subject plus the shared body
may contain at most one exclamation mark.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path


BLOCKED_PHRASES = (
    "free!!!",
    "act now",
    "limited time",
    "100% guaranteed",
    "risk-free",
    "no obligation",
)


def parse_frontmatter(text: str) -> tuple[dict[str, str], str]:
    if not text.startswith("---\n"):
        return {}, text
    end = text.find("\n---\n", 4)
    if end < 0:
        return {}, text
    values: dict[str, str] = {}
    for line in text[4:end].splitlines():
        match = re.match(r"^([A-Za-z][\w-]*):\s*(.*?)\s*$", line)
        if match:
            values[match.group(1)] = match.group(2).strip().strip("'\"")
    return values, text[end + 5 :]


def section(body: str, name: str) -> str | None:
    match = re.search(
        rf"^##\s+{re.escape(name)}\s*$\n(.*?)(?=^##\s+|\Z)",
        body,
        re.MULTILINE | re.DOTALL | re.IGNORECASE,
    )
    return match.group(1).strip() if match else None


def validate(path: Path) -> tuple[list[str], dict[str, object]]:
    text = path.read_text(encoding="utf-8")
    meta, markdown = parse_frontmatter(text)
    errors: list[str] = []
    artifact_type = meta.get("artifact_type", "")
    if artifact_type not in {"cold-email", "newsletter", "nurture"}:
        errors.append("artifact_type must be cold-email, newsletter, or nurture")
    relationship = meta.get("audience_relationship", "")
    if relationship not in {"prospect", "subscriber", "customer"}:
        errors.append("audience_relationship must be prospect, subscriber, or customer")
    if not meta.get("outreach_basis"):
        errors.append("outreach_basis must document the campaign's consent/relationship basis")

    subject_section = section(markdown, "Subject lines")
    subjects = (
        re.findall(r"^\s*\d+[.)]\s+(.+?)\s*$", subject_section, re.MULTILINE)
        if subject_section is not None
        else []
    )
    if len(subjects) != 3:
        errors.append(f"Subject lines section must contain exactly 3 numbered options (found {len(subjects)})")
    body = section(markdown, "Body")
    if body is None:
        errors.append("missing ## Body section")
        body = ""
    cta_count = body.count("<!-- CTA -->")
    if cta_count != 1:
        errors.append(f"body must contain exactly one <!-- CTA --> marker (found {cta_count})")

    visible_body = re.sub(r"<!--.*?-->", "", body, flags=re.DOTALL)
    word_count = len(re.findall(r"\b[\w'-]+\b", visible_body))
    if artifact_type == "cold-email" and word_count >= 120:
        errors.append(f"cold email body must be under 120 words (found {word_count})")
    lower = f"{' '.join(subjects)}\n{visible_body}".lower()
    for phrase in BLOCKED_PHRASES:
        if phrase in lower:
            errors.append(f"blocked deliverability phrase: {phrase}")
    for subject in subjects:
        if re.search(r"\b[A-Z]{3,}\b", subject):
            errors.append(f"subject contains an ALL-CAPS word: {subject}")
        marks = subject.count("!") + visible_body.count("!")
        if marks > 1:
            errors.append(f"sendable variant exceeds one exclamation mark: {subject}")
    body_caps = sorted(set(re.findall(r"\b[A-Z]{3,}\b", visible_body)))
    if body_caps:
        errors.append(f"body contains ALL-CAPS word(s): {', '.join(body_caps)}")

    opt_out = bool(re.search(r"\b(unsubscribe|opt[ -]out|reply stop|not relevant|no thanks)\b", visible_body, re.IGNORECASE))
    if artifact_type in {"newsletter", "nurture"}:
        if relationship not in {"subscriber", "customer"}:
            errors.append(f"{artifact_type} requires a subscriber or customer relationship")
        if not opt_out:
            errors.append(f"{artifact_type} body requires visible unsubscribe/opt-out language")
    elif artifact_type == "cold-email":
        if relationship != "prospect":
            errors.append("cold-email requires audience_relationship: prospect")
        if not opt_out:
            errors.append("cold-email body requires a clear opt-out sentence")

    metrics: dict[str, object] = {
        "artifact_type": artifact_type,
        "subjects": len(subjects),
        "body_words": word_count,
        "cta_markers": cta_count,
        "artifact_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    }
    return errors, metrics


def self_test() -> None:
    import tempfile

    email = """---
artifact_type: cold-email
audience_relationship: prospect
outreach_basis: role-relevant business outreach; owner must confirm jurisdiction
---
## Subject lines

1. A clearer reporting workflow
2. A question about reporting
3. Fewer reporting handoffs

## Body

Hi Ana,

I noticed your team publishes a weekly report. Our workflow removes one manual handoff.

<!-- CTA -->
Would a ten-minute comparison be useful?

If this is not relevant, reply no thanks and I will close the loop.
"""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "cold.md"
        path.write_text(email, encoding="utf-8")
        assert not validate(path)[0]
        path.write_text(email.replace("3. Fewer reporting handoffs\n", ""), encoding="utf-8")
        assert validate(path)[0]
    print("[OK] email_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")
    path = Path(args.path)
    errors, metrics = validate(path)
    print(f"=== EMAIL CONTRACT: {path} ===")
    for key, value in metrics.items():
        print(f"{key}: {value}")
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
