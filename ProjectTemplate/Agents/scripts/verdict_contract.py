#!/usr/bin/env python3
"""Validate a GigaClaw agent verdict against the frozen v1 verdict contract.

The verdict is the single machine-readable judgement shape shared by every
reviewer, quality gate and eval judge (`verdict.schema.json`, next to this
script). Structural rules come from the schema itself - this script contains no
second copy of the field list - and cross-field rules (score <= max, SHIP has no
veto items, evidence refs resolve, digest freshness) are applied on top.

Fail closed: anything this script does not accept is BLOCK for the caller.

Usage:
  verdict_contract.py <verdict.json> [--expect-digest sha256:...]
                                     [--expect-agent qa-tester] [--expect-ticket 42]
  verdict_contract.py --extract <comment.md> [--out verdict.json] [...same checks]
  verdict_contract.py --self-test

Exit codes: 0 = valid, 1 = contract violations (treat as BLOCK), 2 = unreadable
input or invalid usage.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

from schema_check import validate as validate_schema, workspace_relative_errors

SCHEMA_NAME = "verdict.schema.json"
MARKER_RE = re.compile(
    r"^GIGACLAW-VERDICT\s+v1\s+(?P<agent>[a-z0-9][a-z0-9-]*)\s+"
    r"(?P<verdict>SHIP|FIX|BLOCK)\s+artifact-(?P<digest>sha256:[0-9a-f]{64})\s*$",
    re.MULTILINE,
)
FENCE_RE = re.compile(r"```json\s*\n(?P<body>.*?)\n```", re.DOTALL)
SHA256_RE = re.compile(r"^sha256:[0-9a-f]{64}$")


# --------------------------------------------------------------------------
# Cross-field rules the schema cannot express
# --------------------------------------------------------------------------

def _evidence_ref_errors(entry: dict, path: str) -> list[str]:
    kind, ref = entry.get("kind"), entry.get("ref", "")
    if kind == "hash":
        return [] if SHA256_RE.match(ref) else [f"{path}.ref must be sha256:<64 hex> for kind 'hash'"]
    if kind == "link":
        if not ref.startswith("https://"):
            return [f"{path}.ref must be an absolute https URL for kind 'link'"]
        host = ref[len("https://"):].split("/", 1)[0].lower()
        if not host or host in {"example.com", "www.example.com"}:
            return [f"{path}.ref is a placeholder URL, not evidence: {ref}"]
        return []
    if kind == "path":
        return workspace_relative_errors(ref, f"{path}.ref")
    return []


def semantic_errors(verdict: dict) -> list[str]:
    errors: list[str] = []

    categories = verdict.get("categories") or []
    seen: set[str] = set()
    for index, category in enumerate(categories):
        if not isinstance(category, dict):
            continue
        path = f"$.categories[{index}]"
        name, score, maximum = category.get("name"), category.get("score"), category.get("max")
        if isinstance(name, str):
            key = name.strip().lower()
            if key in seen:
                errors.append(f"{path}.name duplicates an earlier category: {name}")
            seen.add(key)
        if isinstance(score, (int, float)) and isinstance(maximum, (int, float)) and score > maximum:
            errors.append(f"{path}.score ({score}) exceeds max ({maximum})")

    veto_items = verdict.get("vetoItems")
    veto_items = veto_items if isinstance(veto_items, list) else []
    decision = verdict.get("verdict")
    if decision == "SHIP" and veto_items:
        errors.append("$.verdict is SHIP but vetoItems is not empty - a veto item forbids SHIP")
    if decision in {"FIX", "BLOCK"}:
        below_max = any(
            isinstance(c, dict)
            and isinstance(c.get("score"), (int, float))
            and isinstance(c.get("max"), (int, float))
            and c["score"] < c["max"]
            for c in categories
        )
        if not veto_items and not below_max:
            errors.append(
                f"$.verdict is {decision} but nothing is machine-readable as the reason "
                "(no vetoItems and every category scores full marks)"
            )

    evidence = verdict.get("evidence")
    evidence = evidence if isinstance(evidence, list) else []
    refs = {e.get("ref") for e in evidence if isinstance(e, dict)}
    for index, entry in enumerate(evidence):
        if isinstance(entry, dict):
            errors.extend(_evidence_ref_errors(entry, f"$.evidence[{index}]"))
    for index, item in enumerate(veto_items):
        if not isinstance(item, dict):
            continue
        for ref in item.get("evidenceRefs", []) or []:
            if ref not in refs:
                errors.append(
                    f"$.vetoItems[{index}].evidenceRefs references '{ref}', "
                    "which is not listed in $.evidence"
                )

    cycle = verdict.get("reviewCycle")
    if isinstance(cycle, dict):
        current, maximum = cycle.get("current"), cycle.get("max")
        if isinstance(current, int) and isinstance(maximum, int) and current > maximum:
            errors.append(f"$.reviewCycle.current ({current}) exceeds max ({maximum})")

    return errors


def expectation_errors(
    verdict: dict,
    expect_digest: str | None = None,
    expect_agent: str | None = None,
    expect_ticket: str | None = None,
) -> list[str]:
    """Freshness/identity checks a caller performs against what it actually dispatched."""
    errors: list[str] = []
    if expect_digest:
        expected = expect_digest if expect_digest.startswith("sha256:") else f"sha256:{expect_digest}"
        if verdict.get("inputDigest") != expected:
            errors.append(
                f"stale verdict: inputDigest {verdict.get('inputDigest')} does not match "
                f"the reviewed artifact {expected}"
            )
    if expect_agent and verdict.get("agent") != expect_agent:
        errors.append(f"$.agent is '{verdict.get('agent')}', expected '{expect_agent}'")
    if expect_ticket is not None and str(verdict.get("ticketId")) != str(expect_ticket):
        errors.append(f"$.ticketId is '{verdict.get('ticketId')}', expected '{expect_ticket}'")
    return errors


def totals(verdict: dict) -> dict[str, float]:
    categories = [c for c in (verdict.get("categories") or []) if isinstance(c, dict)]
    score = sum(c["score"] for c in categories if isinstance(c.get("score"), (int, float)))
    maximum = sum(c["max"] for c in categories if isinstance(c.get("max"), (int, float)))
    percent = round(score / maximum * 100, 1) if maximum else 0.0
    return {"score": score, "max": maximum, "percent": percent}


# --------------------------------------------------------------------------
# Comment transport
# --------------------------------------------------------------------------

def extract_verdict(comment: str) -> tuple[dict | None, list[str]]:
    """Pull the verdict out of a ticket comment body.

    Transport: a `GIGACLAW-VERDICT v1 <agent> <VERDICT> artifact-sha256:<digest>`
    marker line followed by a fenced ```json block. The last marker in the body
    wins so an edited comment cannot resurrect an earlier judgement.
    """
    markers = list(MARKER_RE.finditer(comment))
    if not markers:
        return None, ["comment has no 'GIGACLAW-VERDICT v1 <agent> <VERDICT> artifact-sha256:<digest>' marker line"]
    marker = markers[-1]
    fence = FENCE_RE.search(comment, marker.end())
    if not fence:
        return None, ["comment has a verdict marker but no ```json verdict block after it"]
    try:
        payload = json.loads(fence.group("body"))
    except json.JSONDecodeError as error:
        return None, [f"verdict block is not valid JSON: {error.msg} (line {error.lineno})"]
    if not isinstance(payload, dict):
        return None, ["verdict block must be a JSON object"]

    errors = []
    for field, expected in (
        ("agent", marker.group("agent")),
        ("verdict", marker.group("verdict")),
        ("inputDigest", marker.group("digest")),
    ):
        if payload.get(field) != expected:
            errors.append(
                f"marker line says {field}={expected} but the verdict block says {payload.get(field)}"
            )
    return payload, errors


# --------------------------------------------------------------------------
# Entry points
# --------------------------------------------------------------------------

def load_schema(explicit: Path | None = None) -> dict:
    path = explicit or (Path(__file__).resolve().parent / SCHEMA_NAME)
    return json.loads(path.read_text(encoding="utf-8"))


def validate_verdict(
    verdict: Any,
    schema: dict | None = None,
    expect_digest: str | None = None,
    expect_agent: str | None = None,
    expect_ticket: str | None = None,
    transport_errors: list[str] | None = None,
) -> dict[str, Any]:
    schema = schema or load_schema()
    errors = list(transport_errors or [])
    if not isinstance(verdict, dict):
        errors.append("$ must be a JSON object")
        return {"valid": False, "verdict": None, "errors": errors, "totals": None}

    errors.extend(validate_schema(verdict, schema, schema))
    if not errors:  # Cross-field rules assume the structure already holds.
        errors.extend(semantic_errors(verdict))
    errors.extend(expectation_errors(verdict, expect_digest, expect_agent, expect_ticket))

    return {
        "valid": not errors,
        "verdict": verdict.get("verdict") if not errors else None,
        "agent": verdict.get("agent"),
        "ticketId": verdict.get("ticketId"),
        "inputDigest": verdict.get("inputDigest"),
        "errors": errors,
        "totals": totals(verdict),
    }


def self_test() -> None:
    schema = load_schema()
    digest = "sha256:" + "a" * 64
    base = {
        "schemaVersion": 1,
        "agent": "qa-tester",
        "ticketId": 42,
        "verdict": "SHIP",
        "summary": "All acceptance criteria observed at runtime.",
        "categories": [{"name": "Acceptance criteria", "score": 10, "max": 10}],
        "vetoItems": [],
        "evidence": [{"kind": "path", "ref": "runs/42/qa-report.md"}],
        "reviewedAtUtc": "2026-07-30T12:00:00Z",
        "inputDigest": digest,
    }
    assert validate_verdict(base, schema)["valid"]
    assert validate_verdict(base, schema)["totals"]["percent"] == 100.0

    def rejected(mutate: dict, needle: str) -> None:
        candidate = dict(base)
        candidate.update(mutate)
        result = validate_verdict(candidate, schema)
        assert not result["valid"], f"expected rejection for {mutate}"
        assert any(needle in error for error in result["errors"]), result["errors"]

    rejected({"verdict": "APPROVE"}, "must be one of")
    rejected({"schemaVersion": 2}, "must be 1")
    rejected({"inputDigest": "abc"}, "must match")
    rejected({"reviewedAtUtc": "2026-07-30 12:00"}, "must match")
    rejected({"categories": []}, "at least 1 item")
    rejected({"categories": [{"name": "A", "score": 11, "max": 10}]}, "exceeds max")
    rejected({"agent": "QA Tester"}, "must match")
    rejected({"notAField": True}, "unknown property")
    rejected({"vetoItems": [{"code": "failing-e2e", "statement": "Login E2E fails."}]}, "forbids SHIP")
    rejected({"verdict": "FIX"}, "machine-readable as the reason")
    rejected({"evidence": [{"kind": "path", "ref": "/etc/passwd"}]}, "workspace-relative")
    rejected({"evidence": [{"kind": "path", "ref": "../secrets.txt"}]}, "traverse")
    rejected({"evidence": [{"kind": "link", "ref": "https://example.com/x"}]}, "placeholder")
    rejected(
        {
            "verdict": "FIX",
            "vetoItems": [{"code": "failing-e2e", "statement": "Login E2E fails.", "evidenceRefs": ["nope"]}],
        },
        "not listed in $.evidence",
    )

    stale = validate_verdict(base, schema, expect_digest="sha256:" + "b" * 64)
    assert not stale["valid"] and any("stale verdict" in e for e in stale["errors"])
    assert not validate_verdict(base, schema, expect_agent="ui-auditor")["valid"]

    comment = (
        "## QA report\n\nEverything passed.\n\n"
        f"GIGACLAW-VERDICT v1 qa-tester SHIP artifact-{digest}\n\n"
        "```json\n" + json.dumps(base) + "\n```\n"
    )
    payload, transport = extract_verdict(comment)
    assert payload is not None and not transport, transport
    assert validate_verdict(payload, schema, transport_errors=transport)["valid"]

    _, missing = extract_verdict("no verdict here")
    assert missing and "marker line" in missing[0]
    mismatched = comment.replace("qa-tester SHIP", "qa-tester FIX")
    _, disagreement = extract_verdict(mismatched)
    assert any("marker line says" in error for error in disagreement)

    # The schema file - not this script - is the field list; guard the freeze.
    assert schema["properties"]["schemaVersion"]["const"] == 1
    print("[OK] verdict_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("path", nargs="?", help="verdict JSON file, or comment body with --extract")
    parser.add_argument("--extract", action="store_true", help="read a ticket comment body and pull the verdict out of it")
    parser.add_argument("--out", help="with --extract, write the extracted verdict JSON here")
    parser.add_argument("--expect-digest", help="sha256 of the artifact that was actually reviewed")
    parser.add_argument("--expect-agent")
    parser.add_argument("--expect-ticket")
    parser.add_argument("--schema", help="override the schema path (defaults to verdict.schema.json next to this script)")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0
    if not args.path:
        parser.error("path is required unless --self-test is used")

    schema = load_schema(Path(args.schema) if args.schema else None)
    text = Path(args.path).read_text(encoding="utf-8")
    transport: list[str] = []
    if args.extract:
        payload, transport = extract_verdict(text)
    else:
        try:
            payload = json.loads(text)
        except json.JSONDecodeError as error:
            payload, transport = None, [f"verdict is not valid JSON: {error.msg} (line {error.lineno})"]

    result = validate_verdict(
        payload,
        schema,
        expect_digest=args.expect_digest,
        expect_agent=args.expect_agent,
        expect_ticket=args.expect_ticket,
        transport_errors=transport,
    )
    if result["valid"] and args.extract and args.out:
        Path(args.out).write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"=== VERDICT CONTRACT: {args.path} ===")
        if result["valid"]:
            score = result["totals"]
            print(f"[OK] {result['verdict']} by {result['agent']} on ticket {result['ticketId']}")
            print(f"score {score['score']}/{score['max']} ({score['percent']}%) · {result['inputDigest']}")
        else:
            for error in result["errors"]:
                print(f"[FAIL] {error}")
            print(f"[FAIL] {len(result['errors'])} violation(s) - treat this verdict as BLOCK")
    return 0 if result["valid"] else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
