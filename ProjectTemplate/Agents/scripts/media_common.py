#!/usr/bin/env python3
"""Shared helpers for the local-media execution/validation scripts.

`load_object` is used by both `media_generate.py` (to read execution specs and
OpenMontage checkpoints) and `media_contract.py` (to read execution specs and
generation receipts), so it lives here rather than as two verbatim copies.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any


def load_object(path: Path, description: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise ValueError(f"{description} does not exist: {path}") from error
    except json.JSONDecodeError as error:
        raise ValueError(f"{description} is not valid JSON: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{description} must contain one JSON object")
    return value
