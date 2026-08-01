#!/usr/bin/env python3
"""Deterministic OpenMontage provider worker for GigaClaw local-media jobs.

This script does not make creative or routing decisions. It executes a provider/model
choice already locked in an approved execution spec. Video specs must also prove that
they belong to an approved OpenMontage pipeline checkpoint.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from media_common import load_object


def atomic_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def required_string(value: dict[str, Any], name: str) -> str:
    result = value.get(name)
    if not isinstance(result, str) or not result.strip():
        raise ValueError(f"execution spec field {name!r} is required")
    return result.strip()


def is_under(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def validate_spec(spec: dict[str, Any], spec_path: Path, workspace: Path) -> dict[str, Any]:
    if spec.get("version") != 1:
        raise ValueError("execution spec version must be 1")
    kind = required_string(spec, "kind").lower()
    provider = required_string(spec, "provider").lower()
    if kind not in {"image", "clip"}:
        raise ValueError("kind must be image or clip")
    if provider == "auto":
        raise ValueError("provider auto is forbidden; lock a provider before queueing")
    if kind == "image" and provider != "comfyui":
        raise ValueError("image specs currently require provider comfyui")
    if kind == "clip" and provider not in {"comfyui", "phosphene"}:
        raise ValueError("clip specs require provider comfyui or phosphene")
    prompt = required_string(spec, "prompt")

    governance = spec.get("governance")
    if not isinstance(governance, dict):
        raise ValueError("governance object is required")
    if str(governance.get("approvalStatus", "")).lower() != "approved":
        raise ValueError("execution spec is not approved")
    required_string(governance, "approvedBy")
    required_string(governance, "licenseNotes")
    recorded_skills = governance.get("layer3SkillsRead")
    if not isinstance(recorded_skills, list) or not all(
        isinstance(skill, str) and skill for skill in recorded_skills
    ):
        raise ValueError("governance.layer3SkillsRead must list the provider skills read")

    openmontage = spec.get("openMontage")
    openmontage_path: Path | None = None
    project_root: Path | None = None
    if isinstance(openmontage, dict):
        openmontage_path = Path(required_string(openmontage, "path")).expanduser().resolve()
        if not (openmontage_path / "AGENT_GUIDE.md").is_file():
            raise ValueError(f"invalid OpenMontage checkout: {openmontage_path}")
        project_id = required_string(openmontage, "projectId")
        project_root = (openmontage_path / "projects" / project_id).resolve()

    output = Path(required_string(spec, "outputPath")).expanduser()
    if not output.is_absolute():
        output = (workspace / output).resolve()
    else:
        output = output.resolve()
    if not is_under(output, workspace) and not (
        project_root is not None and is_under(output, project_root)
    ):
        raise ValueError("outputPath must stay inside the workspace or locked OpenMontage project")
    if kind == "image" and output.suffix.lower() not in {".png", ".jpg", ".jpeg", ".webp"}:
        raise ValueError("image output must use png, jpg, jpeg, or webp")
    if kind == "clip" and output.suffix.lower() not in {".mp4", ".mov", ".webm"}:
        raise ValueError("clip output must use mp4, mov, or webm")

    if kind == "clip":
        if not isinstance(openmontage, dict) or openmontage_path is None:
            raise ValueError("clip specs require an OpenMontage project contract")
        if required_string(openmontage, "stage") != "assets":
            raise ValueError("clip jobs may execute only at the OpenMontage assets stage")
        project_id = required_string(openmontage, "projectId")
        pipeline = required_string(openmontage, "pipeline")
        checkpoint = Path(required_string(openmontage, "checkpointPath")).expanduser()
        if not checkpoint.is_absolute():
            checkpoint = (openmontage_path / checkpoint).resolve()
        else:
            checkpoint = checkpoint.resolve()
        if not is_under(checkpoint, openmontage_path / "projects" / project_id):
            raise ValueError("checkpointPath must stay inside the locked OpenMontage project")
        checkpoint_data = load_object(checkpoint, "OpenMontage checkpoint")
        if checkpoint_data.get("status") != "completed":
            raise ValueError("clip generation requires a completed prior OpenMontage checkpoint")
        if checkpoint_data.get("human_approved") is not True:
            raise ValueError("clip generation requires a human-approved OpenMontage checkpoint")
        if checkpoint_data.get("pipeline_type") != pipeline:
            raise ValueError("checkpoint pipeline does not match the execution spec")
        if checkpoint_data.get("project_id") != project_id:
            raise ValueError("checkpoint project does not match the execution spec")

    return {
        "kind": kind,
        "provider": provider,
        "prompt": prompt,
        "governance": governance,
        "openmontage_path": openmontage_path,
        "output": output,
        "parameters": spec.get("parameters") if isinstance(spec.get("parameters"), dict) else {},
        "runtime": spec.get("runtime") if isinstance(spec.get("runtime"), dict) else {},
        "spec_sha256": hashlib.sha256(spec_path.read_bytes()).hexdigest(),
    }


def probe_json(url: str, path: str, timeout: float = 3.0) -> bool:
    try:
        with urllib.request.urlopen(url.rstrip("/") + path, timeout=timeout) as response:
            return 200 <= response.status < 300
    except (OSError, urllib.error.URLError, ValueError):
        return False


def resolve_phosphene_url(configured: str) -> str:
    candidates = [configured.rstrip("/")]
    parsed = urllib.parse.urlsplit(configured)
    if parsed.hostname in {"localhost", "127.0.0.1", "::1"} and parsed.port in {8198, 8199}:
        other_port = 8199 if parsed.port == 8198 else 8198
        host = f"[{parsed.hostname}]" if ":" in (parsed.hostname or "") else parsed.hostname
        candidates.append(urllib.parse.urlunsplit(
            (parsed.scheme or "http", f"{host}:{other_port}", "", "", "")
        ))
    for candidate in candidates:
        if probe_json(candidate, "/status"):
            return candidate
    return candidates[0]


def configure_runtime(validated: dict[str, Any]) -> Path:
    openmontage_path = validated["openmontage_path"]
    if openmontage_path is None:
        configured = os.environ.get("OPENMONTAGE_PATH")
        if not configured:
            raise ValueError("OpenMontage path is missing from the spec and OPENMONTAGE_PATH")
        openmontage_path = Path(configured).expanduser().resolve()
    if not (openmontage_path / "AGENT_GUIDE.md").is_file():
        raise ValueError(f"invalid OpenMontage checkout: {openmontage_path}")

    runtime = validated["runtime"]
    comfy = runtime.get("comfyuiServerUrl") or os.environ.get("COMFYUI_SERVER_URL")
    phosphene = runtime.get("phospheneServerUrl") or os.environ.get("PHOSPHENE_SERVER_URL")
    if comfy:
        os.environ["COMFYUI_SERVER_URL"] = str(comfy)
    if phosphene:
        os.environ["PHOSPHENE_SERVER_URL"] = resolve_phosphene_url(str(phosphene))
    os.environ["OPENMONTAGE_PATH"] = str(openmontage_path)
    sys.path.insert(0, str(openmontage_path))
    return openmontage_path


def build_inputs(validated: dict[str, Any]) -> dict[str, Any]:
    parameters = validated["parameters"]
    inputs: dict[str, Any] = {
        "prompt": validated["prompt"],
        "output_path": str(validated["output"]),
    }
    allowed = {
        "image": {
            "width", "height", "steps", "guidance", "seed", "workflow_json",
            "workflow_path", "output_node", "workflow_name", "workflow_model",
            "workflow_model_stack",
        },
        "clip": {
            "operation", "negative_prompt", "width", "height", "frames", "num_frames",
            "seed", "quality", "reference_image_path", "reference_image_url",
            "video_path", "extend_frames", "extend_direction", "start_image", "end_image",
            "loras", "hdr", "enhance", "upscale", "workflow_json", "workflow_path",
            "output_node", "workflow_name", "workflow_model", "workflow_model_stack",
        },
    }[validated["kind"]]
    for key in allowed:
        if key in parameters:
            inputs[key] = parameters[key]
    if validated["provider"] == "phosphene":
        frames = int(inputs.get("frames", 121))
        if frames % 8 != 1:
            raise ValueError("Phosphene frames must satisfy frames % 8 == 1")
        if inputs.get("loras") and inputs.get("enhance"):
            raise ValueError("Phosphene prompt enhancement must be disabled when LoRAs are used")
    return inputs


def execute(args: argparse.Namespace) -> int:
    spec_path = Path(args.spec).resolve()
    receipt_path = Path(args.receipt).resolve()
    workspace = Path(args.workspace_root).resolve()
    receipt: dict[str, Any] = {
        "version": 1,
        "success": False,
        "spec_path": str(spec_path),
    }
    try:
        if sys.version_info < (3, 10):
            raise ValueError(
                f"OpenMontage requires Python 3.10+; worker is {sys.version.split()[0]}"
            )
        spec = load_object(spec_path, "execution spec")
        validated = validate_spec(spec, spec_path, workspace)
        configure_runtime(validated)
        inputs = build_inputs(validated)
        Path(inputs["output_path"]).parent.mkdir(parents=True, exist_ok=True)

        if validated["kind"] == "image":
            from tools.graphics.comfyui_image import ComfyUIImage
            tool = ComfyUIImage()
        elif validated["provider"] == "comfyui":
            from tools.video.comfyui_video import ComfyUIVideo
            tool = ComfyUIVideo()
        else:
            from tools.video.phosphene_video import PhospheneVideo
            tool = PhospheneVideo()

        required_skills = set(getattr(tool, "agent_skills", []))
        recorded_skills = set(validated["governance"]["layer3SkillsRead"])
        missing_skills = sorted(required_skills - recorded_skills)
        if missing_skills:
            raise ValueError(
                "execution spec does not record required Layer 3 skills: "
                + ", ".join(missing_skills)
            )

        result = tool.execute(inputs)
        if not result.success:
            raise RuntimeError(result.error or "OpenMontage provider returned failure")
        data = result.data if isinstance(result.data, dict) else {}
        output_path = data.get("output") or (
            result.artifacts[0] if result.artifacts else inputs["output_path"]
        )
        provenance = {
            "provider": data.get("provider") or validated["provider"],
            "model": result.model or data.get("model"),
            "seed": result.seed,
            "cost_usd": result.cost_usd,
            "duration_seconds": result.duration_seconds,
            "workflow": data.get("workflow_provenance"),
            "license_notes": validated["governance"]["licenseNotes"],
            "layer3_skills_read": sorted(recorded_skills),
        }
        receipt.update({
            "success": True,
            "kind": validated["kind"],
            "provider": validated["provider"],
            "spec_sha256": validated["spec_sha256"],
            "output_path": str(Path(output_path).resolve()),
            "artifacts": result.artifacts,
            "provenance": provenance,
        })
        atomic_json(receipt_path, receipt)
        print(json.dumps({"success": True, "receipt": str(receipt_path)}))
        return 0
    except Exception as error:
        receipt["error"] = str(error)
        try:
            if spec_path.is_file():
                receipt["spec_sha256"] = hashlib.sha256(spec_path.read_bytes()).hexdigest()
            atomic_json(receipt_path, receipt)
        finally:
            print(json.dumps({"success": False, "error": str(error), "receipt": str(receipt_path)}))
        return 1


def probe(args: argparse.Namespace) -> int:
    spec: dict[str, Any] | None = None
    spec_checkout: str | None = None
    if args.spec:
        spec = load_object(Path(args.spec).resolve(), "execution spec")
        governance = spec.get("governance")
        if not isinstance(governance, dict) or str(
            governance.get("approvalStatus", "")
        ).lower() != "approved":
            print(json.dumps({"success": False, "error": "probe spec is not approved"}))
            return 1
        openmontage = spec.get("openMontage")
        if isinstance(openmontage, dict):
            candidate = openmontage.get("path")
            if isinstance(candidate, str) and candidate.strip():
                spec_checkout = candidate

    raw_checkout = args.openmontage or spec_checkout or os.environ.get("OPENMONTAGE_PATH")
    if not raw_checkout:
        print(json.dumps({"success": False, "error": "OPENMONTAGE_PATH is not configured"}))
        return 1
    checkout = Path(raw_checkout).expanduser().resolve()
    if not (checkout / "AGENT_GUIDE.md").is_file():
        print(json.dumps({"success": False, "error": f"invalid OpenMontage checkout: {checkout}"}))
        return 1
    sys.path.insert(0, str(checkout))
    from tools.tool_registry import registry
    registry.discover()
    summary = registry.provider_menu_summary()
    locked_provider = spec.get("provider") if spec else None
    provider_ready: bool | None = None
    if spec:
        capability = "image_generation" if spec.get("kind") == "image" else "video_generation"
        capability_rows = summary.get("capabilities", [])
        row = next(
            (item for item in capability_rows if item.get("capability") == capability),
            {},
        )
        provider_ready = locked_provider in row.get("available_providers", [])
    python_ready = sys.version_info >= (3, 10)
    success = python_ready and provider_ready is not False
    result = {
        "success": success,
        "openmontage_path": str(checkout),
        "python": sys.version.split()[0],
        "python_ready": python_ready,
        "provider_menu": summary,
        "locked_provider": locked_provider,
        "locked_provider_ready": provider_ready,
    }
    if not success:
        reasons = []
        if not python_ready:
            reasons.append("Python 3.10+ is required")
        if provider_ready is False:
            reasons.append(f"locked provider {locked_provider!r} is unavailable")
        result["error"] = "; ".join(reasons)
    if args.receipt:
        atomic_json(Path(args.receipt).resolve(), result)
    print(json.dumps(result))
    return 0 if success else 1


def self_test() -> int:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        spec_path = root / "spec.json"
        output = root / "media" / "renders" / "candidate.png"
        valid = {
            "version": 1,
            "kind": "image",
            "provider": "comfyui",
            "prompt": "A sufficiently detailed local image generation test prompt",
            "outputPath": str(output),
            "governance": {
                "approvalStatus": "approved",
                "approvedBy": "owner",
                "licenseNotes": "Test-only generated asset",
                "layer3SkillsRead": ["comfyui", "flux-best-practices"],
            },
        }
        spec_path.write_text(json.dumps(valid), encoding="utf-8")
        parsed = validate_spec(valid, spec_path, root)
        assert parsed["provider"] == "comfyui"
        invalid = dict(valid, provider="auto")
        try:
            validate_spec(invalid, spec_path, root)
        except ValueError as error:
            assert "auto is forbidden" in str(error)
        else:
            raise AssertionError("provider auto was accepted")
    print(json.dumps({"success": True, "tests": 2}))
    return 0


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    subparsers = result.add_subparsers(dest="command", required=True)
    execute_parser = subparsers.add_parser("execute")
    execute_parser.add_argument("--spec", required=True)
    execute_parser.add_argument("--receipt", required=True)
    execute_parser.add_argument("--workspace-root", required=True)
    probe_parser = subparsers.add_parser("probe")
    probe_parser.add_argument("--openmontage")
    probe_parser.add_argument("--spec")
    probe_parser.add_argument("--receipt")
    subparsers.add_parser("self-test")
    return result


def main() -> int:
    args = parser().parse_args()
    if args.command == "execute":
        return execute(args)
    if args.command == "probe":
        return probe(args)
    return self_test()


if __name__ == "__main__":
    raise SystemExit(main())
