#!/usr/bin/env python3
"""Validate a GigaClaw run handoff against the frozen v1 handoff contract.

A handoff is what one agent leaves for the next: outputs, owned files,
assumptions, open loops, and where the work goes next (`handoff.schema.json`,
next to this script; documented in `doc/handoff-contract.md`). Structural rules
come from the schema file itself via `schema_check`; the cross-field rules the
schema cannot express are applied here.

Fail closed: an unreadable handoff is treated as no handoff, so the next agent
starts from the ticket instead of from a half-parsed one.

Usage:
  handoff_contract.py <handoff.json> [--expect-agent programmer] [--expect-ticket 42]
  handoff_contract.py --extract <comment.md> [--out handoff.json]
  handoff_contract.py --self-test

Exit codes: 0 = valid, 1 = contract violations, 2 = unreadable input or usage.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

from schema_check import validate as validate_schema, workspace_relative_errors

# These scripts print non-ASCII (arrows, middots, dashes, accents) and the host reads their
# stdout as UTF-8. Python on Windows still defaults stdout to the ANSI code page (cp1252), where
# those characters raise UnicodeEncodeError *after* the work succeeded -- turning a clean pass
# into a crash. Pin the stream instead of degrading the output to ASCII.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

SCHEMA_NAME = "handoff.schema.json"
MARKER_RE = re.compile(
    r"^GIGACLAW-HANDOFF\s+v1\s+(?P<agent>[a-z0-9][a-z0-9-]*)\s+ticket-(?P<ticket>[0-9]+)\s+run-(?P<run>\S+)\s*$",
    re.MULTILINE,
)
FENCE_RE = re.compile(r"```json\s*\n(?P<body>.*?)\n```", re.DOTALL)
SHA256_RE = re.compile(r"^sha256:[0-9a-f]{64}$")


def _artifact_errors(entry: dict, path: str) -> list[str]:
    kind, ref = entry.get("kind"), entry.get("ref", "")
    if kind == "hash":
        return [] if SHA256_RE.match(ref) else [f"{path}.ref must be sha256:<64 hex> for kind 'hash'"]
    if kind == "link":
        if not ref.startswith("https://"):
            return [f"{path}.ref must be an absolute https URL for kind 'link'"]
        host = ref[len("https://"):].split("/", 1)[0].lower()
        if not host or host in {"example.com", "www.example.com"}:
            return [f"{path}.ref is a placeholder URL, not an artifact: {ref}"]
        return []
    if kind == "path":
        return workspace_relative_errors(ref, f"{path}.ref")
    return []


def semantic_errors(handoff: dict) -> list[str]:
    errors: list[str] = []

    outputs = handoff.get("outputs") if isinstance(handoff.get("outputs"), list) else []
    inputs = handoff.get("inputs") if isinstance(handoff.get("inputs"), list) else []
    for label, entries in (("inputs", inputs), ("outputs", outputs)):
        for index, entry in enumerate(entries):
            if isinstance(entry, dict):
                errors.extend(_artifact_errors(entry, f"$.{label}[{index}]"))

    # Owned files feed the lease layer, so they follow the same path discipline as
    # evidence: a lease on an absolute path or a traversal is not enforceable.
    owned = handoff.get("ownedFiles") if isinstance(handoff.get("ownedFiles"), list) else []
    for index, entry in enumerate(owned):
        if isinstance(entry, str):
            errors.extend(workspace_relative_errors(entry, f"$.ownedFiles[{index}]"))
    if len(set(owned)) != len(owned):
        errors.append("$.ownedFiles repeats a path; a lease scope is a set")

    output_refs = {o.get("ref") for o in outputs if isinstance(o, dict)}
    criteria = handoff.get("acceptanceCriteria") if isinstance(handoff.get("acceptanceCriteria"), list) else []
    for index, criterion in enumerate(criteria):
        if not isinstance(criterion, dict):
            continue
        path = f"$.acceptanceCriteria[{index}]"
        if criterion.get("met") is True:
            ref = criterion.get("evidenceRef")
            if not ref:
                errors.append(f"{path} is met but cites no evidenceRef; a claim without evidence is not a handoff")
            elif ref not in output_refs:
                errors.append(f"{path}.evidenceRef '{ref}' is not listed in $.outputs")

    # A run that produced nothing and claims nothing is a silent no-op; make it say so.
    if not outputs and not owned and not handoff.get("openLoops"):
        errors.append(
            "$ declares no outputs, no owned files and no open loops - "
            "state at least one open loop explaining what the run actually did"
        )

    blocking = [
        loop for loop in (handoff.get("openLoops") or [])
        if isinstance(loop, dict) and loop.get("blocking") is True
    ]
    if blocking and handoff.get("nextRole") not in (None, "owner"):
        errors.append(
            "$ has a blocking open loop but hands to "
            f"'{handoff.get('nextRole')}' - a blocked handoff goes to the owner"
        )

    return errors


def expectation_errors(handoff: dict, expect_agent: str | None, expect_ticket: str | None) -> list[str]:
    errors: list[str] = []
    if expect_agent and handoff.get("agent") != expect_agent:
        errors.append(f"$.agent is '{handoff.get('agent')}', expected '{expect_agent}'")
    if expect_ticket is not None and str(handoff.get("ticketId")) != str(expect_ticket):
        errors.append(f"$.ticketId is '{handoff.get('ticketId')}', expected '{expect_ticket}'")
    return errors


def extract_handoff(comment: str) -> tuple[dict | None, list[str]]:
    """Pull the handoff out of a ticket comment. Last marker wins."""
    markers = list(MARKER_RE.finditer(comment))
    if not markers:
        return None, ["comment has no 'GIGACLAW-HANDOFF v1 <agent> ticket-<id> run-<runId>' marker line"]
    marker = markers[-1]
    fence = FENCE_RE.search(comment, marker.end())
    if not fence:
        return None, ["comment has a handoff marker but no ```json handoff block after it"]
    try:
        payload = json.loads(fence.group("body"))
    except json.JSONDecodeError as error:
        return None, [f"handoff block is not valid JSON: {error.msg} (line {error.lineno})"]
    if not isinstance(payload, dict):
        return None, ["handoff block must be a JSON object"]

    errors = []
    for field, expected in (
        ("agent", marker.group("agent")),
        ("ticketId", marker.group("ticket")),
        ("runId", marker.group("run")),
    ):
        if str(payload.get(field)) != expected:
            errors.append(f"marker line says {field}={expected} but the handoff block says {payload.get(field)}")
    return payload, errors


def load_schema(explicit: Path | None = None) -> dict:
    path = explicit or (Path(__file__).resolve().parent / SCHEMA_NAME)
    return json.loads(path.read_text(encoding="utf-8"))


def validate_handoff(
    handoff: Any,
    schema: dict | None = None,
    expect_agent: str | None = None,
    expect_ticket: str | None = None,
    transport_errors: list[str] | None = None,
) -> dict[str, Any]:
    schema = schema or load_schema()
    errors = list(transport_errors or [])
    if not isinstance(handoff, dict):
        errors.append("$ must be a JSON object")
        return {"valid": False, "errors": errors}

    errors.extend(validate_schema(handoff, schema, schema))
    if not errors:
        errors.extend(semantic_errors(handoff))
    errors.extend(expectation_errors(handoff, expect_agent, expect_ticket))

    return {
        "valid": not errors,
        "agent": handoff.get("agent"),
        "ticketId": handoff.get("ticketId"),
        "runId": handoff.get("runId"),
        "nextRole": handoff.get("nextRole"),
        "ownedFiles": handoff.get("ownedFiles", []),
        "errors": errors,
    }


def self_test() -> None:
    schema = load_schema()
    base = {
        "schemaVersion": 1,
        "agent": "programmer",
        "ticketId": 42,
        "runId": "9f2c1a",
        "summary": "Added the verdict gate condition; reviewer can now run against it.",
        "inputs": [{"kind": "path", "ref": "doc/verdict-contract.md"}],
        "outputs": [{"kind": "path", "ref": "GigaClaw.Core/Automation/ConditionEvaluators.cs"}],
        "ownedFiles": ["GigaClaw.Core/Automation/ConditionEvaluators.cs"],
        "assumptions": ["Reviewers emit verdicts; prose reviewers are handled by the MISSING outcome."],
        "openLoops": [],
        "acceptanceCriteria": [
            {
                "statement": "Condition gates ticket exit",
                "met": True,
                "evidenceRef": "GigaClaw.Core/Automation/ConditionEvaluators.cs",
            }
        ],
        "nextRole": "qa-tester",
        "producedAtUtc": "2026-07-30T12:00:00Z",
    }
    assert validate_handoff(base, schema)["valid"]

    def rejected(mutate: dict, needle: str) -> None:
        candidate = dict(base)
        candidate.update(mutate)
        result = validate_handoff(candidate, schema)
        assert not result["valid"], f"expected rejection for {mutate}"
        assert any(needle in error for error in result["errors"]), result["errors"]

    rejected({"schemaVersion": 2}, "must be 1")
    rejected({"agent": "Programmer"}, "must match")
    rejected({"nextRole": "QA Tester"}, "must match")
    rejected({"ownedFiles": ["/etc/passwd"]}, "workspace-relative")
    rejected({"ownedFiles": ["../other/repo/file.cs"]}, "traverse")
    rejected({"ownedFiles": ["a.cs", "a.cs"]}, "repeats a path")
    rejected({"producedAtUtc": "2026-07-30 12:00"}, "must match")
    rejected({"notAField": 1}, "unknown property")
    rejected(
        {"acceptanceCriteria": [{"statement": "Gate works", "met": True}]},
        "cites no evidenceRef",
    )
    rejected(
        {"acceptanceCriteria": [{"statement": "Gate works", "met": True, "evidenceRef": "nope.cs"}]},
        "not listed in $.outputs",
    )
    rejected({"outputs": [], "ownedFiles": [], "openLoops": []}, "state at least one open loop")
    rejected(
        {"openLoops": [{"statement": "Migration not written", "blocking": True}]},
        "blocked handoff goes to the owner",
    )

    # A blocking loop handed to the owner is legitimate.
    blocked = dict(base)
    blocked.update({
        "openLoops": [{"statement": "Migration not written", "blocking": True}],
        "nextRole": None,
    })
    assert validate_handoff(blocked, schema)["valid"]

    comment = (
        "Work done.\n\n"
        f"GIGACLAW-HANDOFF v1 programmer ticket-42 run-9f2c1a\n\n"
        "```json\n" + json.dumps(base) + "\n```\n"
    )
    payload, transport = extract_handoff(comment)
    assert payload is not None and not transport, transport
    assert validate_handoff(payload, schema, transport_errors=transport)["valid"]

    _, missing = extract_handoff("no handoff here")
    assert missing and "marker line" in missing[0]
    _, mismatch = extract_handoff(comment.replace("ticket-42", "ticket-43"))
    assert any("marker line says" in error for error in mismatch)

    assert schema["properties"]["schemaVersion"]["const"] == 1
    print("[OK] handoff_contract self-test passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("path", nargs="?")
    parser.add_argument("--extract", action="store_true", help="read a ticket comment body and pull the handoff out of it")
    parser.add_argument("--out", help="with --extract, write the extracted handoff JSON here")
    parser.add_argument("--expect-agent")
    parser.add_argument("--expect-ticket")
    parser.add_argument("--schema")
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
        payload, transport = extract_handoff(text)
    else:
        try:
            payload = json.loads(text)
        except json.JSONDecodeError as error:
            payload, transport = None, [f"handoff is not valid JSON: {error.msg} (line {error.lineno})"]

    result = validate_handoff(payload, schema, args.expect_agent, args.expect_ticket, transport)
    if result["valid"] and args.extract and args.out:
        Path(args.out).write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    if args.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"=== HANDOFF CONTRACT: {args.path} ===")
        if result["valid"]:
            print(f"[OK] {result['agent']} on ticket {result['ticketId']} → {result['nextRole'] or 'owner'}")
            print(f"owns {len(result['ownedFiles'])} path(s) · run {result['runId']}")
        else:
            for error in result["errors"]:
                print(f"[FAIL] {error}")
            print(f"[FAIL] {len(result['errors'])} violation(s) - treat this as no handoff")
    return 0 if result["valid"] else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
