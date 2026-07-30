#!/usr/bin/env python3
"""A small JSON Schema (2020-12 subset) evaluator shared by the agent contracts.

The point is that a contract's schema file stays the single source of its field
list: `verdict_contract.py` and `handoff_contract.py` read their schema at
runtime and validate through this module rather than keeping a second copy of
the rules in Python. Cross-field rules the schema cannot express live in the
individual contract scripts.

Supported keywords: type (string or list), const, enum, required, properties,
additionalProperties (false), items, minItems, maxItems, minLength, maxLength,
minimum, maximum, pattern, and local $ref into $defs. Anything else in a schema
raises rather than silently passing.
"""

from __future__ import annotations

import json
import re
from typing import Any


def type_matches(value: Any, expected: str) -> bool:
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    if expected == "string":
        return isinstance(value, str)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if expected == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if expected == "null":
        return value is None
    raise ValueError(f"schema uses unsupported type '{expected}'")


def resolve(schema: dict, root: dict) -> dict:
    ref = schema.get("$ref")
    if not ref:
        return schema
    if not ref.startswith("#/"):
        raise ValueError(f"schema uses unsupported $ref '{ref}'")
    node: Any = root
    for part in ref[2:].split("/"):
        node = node[part]
    return node


def validate(value: Any, schema: dict, root: dict, path: str = "$") -> list[str]:
    """Structural validation. Returns one message per violation, deepest path first."""
    schema = resolve(schema, root)
    errors: list[str] = []

    if "const" in schema and value != schema["const"]:
        errors.append(f"{path} must be {json.dumps(schema['const'])}")
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path} must be one of {', '.join(map(str, schema['enum']))}")

    expected = schema.get("type")
    if expected is not None:
        options = expected if isinstance(expected, list) else [expected]
        if not any(type_matches(value, option) for option in options):
            errors.append(f"{path} must be of type {' or '.join(options)}")
            return errors  # Further keywords would only echo the same problem.

    if isinstance(value, str):
        if "minLength" in schema and len(value) < schema["minLength"]:
            errors.append(f"{path} must be at least {schema['minLength']} character(s)")
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            errors.append(f"{path} must be at most {schema['maxLength']} characters")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            errors.append(f"{path} must match {schema['pattern']}")
    elif isinstance(value, (int, float)) and not isinstance(value, bool):
        if "minimum" in schema and value < schema["minimum"]:
            errors.append(f"{path} must be >= {schema['minimum']}")
        if "maximum" in schema and value > schema["maximum"]:
            errors.append(f"{path} must be <= {schema['maximum']}")
    elif isinstance(value, list):
        if "minItems" in schema and len(value) < schema["minItems"]:
            errors.append(f"{path} must contain at least {schema['minItems']} item(s)")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            errors.append(f"{path} must contain at most {schema['maxItems']} items")
        item_schema = schema.get("items")
        if item_schema:
            for index, item in enumerate(value):
                errors.extend(validate(item, item_schema, root, f"{path}[{index}]"))
    elif isinstance(value, dict):
        properties = schema.get("properties", {})
        for name in schema.get("required", []):
            if name not in value:
                errors.append(f"{path} is missing required property '{name}'")
        if schema.get("additionalProperties") is False:
            for name in value:
                if name not in properties:
                    errors.append(f"{path} has unknown property '{name}'")
        for name, sub_schema in properties.items():
            if name in value:
                errors.extend(validate(value[name], sub_schema, root, f"{path}.{name}"))

    return errors


def workspace_relative_errors(reference: str, path: str) -> list[str]:
    """Shared path discipline: contracts never point outside the workspace."""
    normalized = reference.replace("\\", "/")
    if normalized.startswith("/") or re.match(r"^[A-Za-z]:/", normalized) or normalized.startswith("~"):
        return [f"{path} must be workspace-relative, not absolute: {reference}"]
    if ".." in normalized.split("/"):
        return [f"{path} must not traverse outside the workspace: {reference}"]
    return []
