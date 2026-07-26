#!/usr/bin/env python3
"""Checked, idempotent GigaClaw ticket operations using only the stdlib.

Examples:
  python3 .agents/scripts/agent_ticket.py --project demo --ticket 42 \
    --author blog-writer comment --content-file ./delivery.md \
    --marker "BLOG-DRAFT artifact-sha256:..."
  python3 .agents/scripts/agent_ticket.py --project demo --ticket 42 \
    --author blog-writer handoff --assignee blog-reviewer --status Todo \
    --expected-status Review
  python3 .agents/scripts/agent_ticket.py --project demo --ticket 42 \
    --author groomer labels --add 3 7

Exit codes: 0 = success (including an idempotent skip), 1 = requested marker
not found, 2 = invalid input/network/API failure.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


def combined_digest(paths: list[str]) -> str:
    """Hash relative path names and bytes in stable lexical order."""
    digest = hashlib.sha256()
    resolved = sorted((Path(path) for path in paths), key=lambda p: p.as_posix())
    if not resolved:
        raise ValueError("at least one artifact path is required")
    for path in resolved:
        if not path.is_file():
            raise ValueError(f"artifact is missing or unreadable: {path}")
        name = path.as_posix().encode("utf-8")
        data = path.read_bytes()
        digest.update(len(name).to_bytes(8, "big"))
        digest.update(name)
        digest.update(len(data).to_bytes(8, "big"))
        digest.update(data)
    return digest.hexdigest()


class ApiError(RuntimeError):
    pass


class Client:
    def __init__(self, base_url: str, project: str, ticket: int, author: str):
        root = base_url.rstrip("/")
        if root.endswith("/api"):
            root = root[:-4]
        self.root = root
        self.project = urllib.parse.quote(project, safe="")
        self.ticket = ticket
        self.author = author

    @property
    def ticket_path(self) -> str:
        return f"/api/projects/{self.project}/tickets/{self.ticket}"

    def request(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any:
        url = f"{self.root}{path}"
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            url,
            data=data,
            method=method,
            headers={"Content-Type": "application/json", "Accept": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                raw = response.read()
                if not 200 <= response.status < 300:
                    raise ApiError(f"{method} {url} returned HTTP {response.status}")
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as exc:
            body_text = exc.read().decode("utf-8", errors="replace")[:1000]
            raise ApiError(
                f"{method} {url} returned HTTP {exc.code}: {body_text}"
            ) from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise ApiError(f"{method} {url} failed: {exc}") from exc

    def get(self) -> dict[str, Any]:
        result = self.request("GET", self.ticket_path)
        if not isinstance(result, dict):
            raise ApiError("ticket response was not a JSON object")
        return result

    @staticmethod
    def comments(ticket: dict[str, Any]) -> list[dict[str, Any]]:
        value = ticket.get("comments", ticket.get("Comments", []))
        return value if isinstance(value, list) else []

    @classmethod
    def marker_present(cls, ticket: dict[str, Any], marker: str) -> bool:
        for comment in cls.comments(ticket):
            content = comment.get("content", comment.get("Content", ""))
            if marker in str(content):
                return True
        return False

    def comment(self, content: str, marker: str | None) -> bool:
        if marker:
            ticket = self.get()
            if self.marker_present(ticket, marker):
                print(f"[IDEMPOTENT] comment marker already present: {marker}")
                return False
            if marker not in content:
                content = f"{content.rstrip()}\n\n{marker}"
        self.request(
            "POST",
            f"{self.ticket_path}/comments",
            {"content": content, "author": self.author},
        )
        print("[OK] comment created")
        return True

    def assign(self, assignee: str) -> None:
        result = self.request(
            "PATCH", self.ticket_path, {"assignedTo": assignee, "author": self.author}
        )
        actual = result.get("assignedTo", result.get("AssignedTo")) if isinstance(result, dict) else None
        if actual != assignee:
            raise ApiError(f"assignment verification failed: expected {assignee!r}, got {actual!r}")
        print(f"[OK] assignedTo={assignee}")

    def status(self, status: str) -> None:
        result = self.request(
            "PATCH",
            f"{self.ticket_path}/status",
            {"status": status, "author": self.author},
        )
        actual = result.get("status", result.get("Status")) if isinstance(result, dict) else None
        if actual != status:
            raise ApiError(f"status verification failed: expected {status!r}, got {actual!r}")
        print(f"[OK] status={status}")

    def transition(self, status: str, assignee: str, expected_status: str | None) -> None:
        body: dict[str, Any] = {
            "status": status,
            "assignedTo": assignee,
            "author": self.author,
        }
        if expected_status is not None:
            body["expectedStatus"] = expected_status
        result = self.request("PATCH", f"{self.ticket_path}/transition", body)
        actual_status = result.get("status", result.get("Status")) if isinstance(result, dict) else None
        actual_assignee = result.get("assignedTo", result.get("AssignedTo")) if isinstance(result, dict) else None
        if actual_status != status or actual_assignee != assignee:
            raise ApiError(
                "transition verification failed: "
                f"expected {status!r}/{assignee!r}, got {actual_status!r}/{actual_assignee!r}"
            )
        print(f"[OK] atomic transition status={status}, assignedTo={assignee}")

    def labels(self, add: list[int], remove: list[int]) -> None:
        result = self.request(
            "PATCH",
            f"{self.ticket_path}/labels",
            {"addLabelIds": add, "removeLabelIds": remove, "author": self.author},
        )
        if not isinstance(result, list):
            raise ApiError("label patch response was not a JSON list")
        print(f"[OK] labels patched atomically (+{add}, -{remove})")


def read_content(args: argparse.Namespace) -> str:
    if args.content is not None:
        return args.content
    if args.content_file is not None:
        return Path(args.content_file).read_text(encoding="utf-8")
    raise ValueError("provide --content or --content-file")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", default=os.environ.get("GIGACLAW_API_URL"))
    parser.add_argument("--project")
    parser.add_argument("--ticket", type=int)
    parser.add_argument("--author")
    commands = parser.add_subparsers(dest="command", required=True)

    commands.add_parser("get")
    digest = commands.add_parser("digest")
    digest.add_argument("files", nargs="+")

    marker = commands.add_parser("has-marker")
    marker.add_argument("marker")

    comment = commands.add_parser("comment")
    source = comment.add_mutually_exclusive_group(required=True)
    source.add_argument("--content")
    source.add_argument("--content-file")
    comment.add_argument("--marker")

    assign = commands.add_parser("assign")
    assign.add_argument("--to", required=True)

    status = commands.add_parser("status")
    status.add_argument("--to", required=True)

    handoff = commands.add_parser("handoff")
    handoff.add_argument("--assignee", required=True)
    handoff.add_argument("--status", default="Todo")
    handoff.add_argument(
        "--expected-status",
        required=True,
        help="optimistic concurrency guard passed to the atomic transition endpoint",
    )
    source = handoff.add_mutually_exclusive_group()
    source.add_argument("--content")
    source.add_argument("--content-file")
    handoff.add_argument("--marker")

    labels = commands.add_parser("labels")
    labels.add_argument("--add", type=int, nargs="*", default=[])
    labels.add_argument("--remove", type=int, nargs="*", default=[])
    return parser


def self_test() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as directory:
        first = Path(directory) / "a.txt"
        second = Path(directory) / "b.txt"
        first.write_text("alpha", encoding="utf-8")
        second.write_text("beta", encoding="utf-8")
        assert combined_digest([str(second), str(first)]) == combined_digest(
            [str(first), str(second)]
        )
        ticket = {"comments": [{"content": "ROUND3 artifact-sha256:abc"}]}
        assert Client.marker_present(ticket, "artifact-sha256:abc")
        assert not Client.marker_present(ticket, "artifact-sha256:def")
        calls: list[tuple[str, str, dict[str, Any] | None]] = []

        class FakeClient(Client):
            def request(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any:
                calls.append((method, path, body))
                if path.endswith("/transition"):
                    return {"status": body["status"], "assignedTo": body["assignedTo"]}
                if path.endswith("/labels"):
                    return []
                return {}

        fake = FakeClient("http://localhost:1", "demo", 7, "tester")
        fake.transition("Todo", "writer", "InProgress")
        fake.labels([2], [3])
        assert calls[0][1].endswith("/transition")
        assert calls[0][2] == {
            "status": "Todo",
            "assignedTo": "writer",
            "author": "tester",
            "expectedStatus": "InProgress",
        }
        assert calls[1][1].endswith("/labels")
    print("[OK] agent_ticket self-test passed")


def main() -> int:
    if sys.argv[1:] == ["--self-test"]:
        self_test()
        return 0
    args = build_parser().parse_args()
    if args.command == "digest":
        print(f"artifact-sha256:{combined_digest(args.files)}")
        return 0
    if not args.api or not args.project or args.ticket is None or not args.author:
        raise ValueError("--api (or GIGACLAW_API_URL), --project, --ticket, and --author are required")
    client = Client(args.api, args.project, args.ticket, args.author)
    if args.command == "get":
        print(json.dumps(client.get(), indent=2, sort_keys=True))
    elif args.command == "has-marker":
        found = client.marker_present(client.get(), args.marker)
        print("[FOUND]" if found else "[NOT FOUND]", args.marker)
        return 0 if found else 1
    elif args.command == "comment":
        client.comment(read_content(args), args.marker)
    elif args.command == "assign":
        client.assign(args.to)
    elif args.command == "status":
        client.status(args.to)
    elif args.command == "handoff":
        ticket = client.get()
        if args.marker and client.marker_present(ticket, args.marker):
            print(f"[IDEMPOTENT] handoff marker already present: {args.marker}")
            return 0
        actual_assignee = ticket.get("assignedTo", ticket.get("AssignedTo"))
        actual_status = ticket.get("status", ticket.get("Status"))
        if actual_assignee == args.assignee and actual_status == args.status:
            print("[IDEMPOTENT] desired assignee/status already set")
        elif actual_assignee == args.assignee and actual_status != args.expected_status:
            # A prior transition succeeded and the downstream worker already
            # advanced it before this receipt could be written. Never roll back.
            print(
                "[IDEMPOTENT] atomic handoff already progressed downstream; "
                f"current status={actual_status}"
            )
        else:
            client.transition(args.status, args.assignee, args.expected_status)
        # The receipt is written last. A marker therefore proves both state
        # writes were verified; a failed state write never leaves a false receipt.
        if args.content is not None or args.content_file is not None:
            client.comment(read_content(args), args.marker)
    elif args.command == "labels":
        client.labels(args.add, args.remove)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ApiError, OSError, ValueError) as error:
        print(f"[ERROR] {error}", file=sys.stderr)
        raise SystemExit(2)
