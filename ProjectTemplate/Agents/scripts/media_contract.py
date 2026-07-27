#!/usr/bin/env python3
"""Validate local-media artifacts and their reproducibility receipts."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import tempfile
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


def ffprobe(path: Path, executable: str) -> dict[str, Any]:
    completed = subprocess.run(
        [
            executable, "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", str(path),
        ],
        check=False,
        capture_output=True,
        text=True,
        timeout=30,
    )
    if completed.returncode != 0:
        raise ValueError(completed.stderr.strip() or "ffprobe failed")
    return json.loads(completed.stdout)


def check(args: argparse.Namespace) -> int:
    try:
        spec_path = Path(args.spec).resolve()
        receipt_path = Path(args.receipt).resolve()
        spec = load_object(spec_path, "execution spec")
        receipt = load_object(receipt_path, "generation receipt")
        if receipt.get("success") is not True:
            raise ValueError("generation receipt is not successful")
        expected_hash = hashlib.sha256(spec_path.read_bytes()).hexdigest()
        if receipt.get("spec_sha256") != expected_hash:
            raise ValueError("execution spec digest does not match the receipt")

        artifact = Path(receipt.get("output_path", "")).resolve()
        if not artifact.is_file() or artifact.stat().st_size == 0:
            raise ValueError(f"artifact is missing or empty: {artifact}")
        kind = spec.get("kind")
        if kind == "image" and artifact.suffix.lower() not in {".png", ".jpg", ".jpeg", ".webp"}:
            raise ValueError("image artifact has an unsupported extension")
        if kind == "clip" and artifact.suffix.lower() not in {".mp4", ".mov", ".webm"}:
            raise ValueError("clip artifact has an unsupported extension")

        provenance = receipt.get("provenance")
        if not isinstance(provenance, dict):
            raise ValueError("receipt provenance is missing")
        required = {"provider", "model", "seed", "duration_seconds", "license_notes", "layer3_skills_read"}
        missing = sorted(key for key in required if key not in provenance or provenance[key] in (None, ""))
        if missing:
            raise ValueError("receipt provenance is incomplete: " + ", ".join(missing))

        technical: dict[str, Any] = {}
        if kind == "clip":
            technical = ffprobe(artifact, args.ffprobe)
            video_streams = [
                stream for stream in technical.get("streams", [])
                if stream.get("codec_type") == "video"
            ]
            if not video_streams:
                raise ValueError("clip contains no video stream")
            parameters = spec.get("parameters") if isinstance(spec.get("parameters"), dict) else {}
            expected_width = parameters.get("width")
            expected_height = parameters.get("height")
            stream = video_streams[0]
            if expected_width and stream.get("width") != expected_width:
                raise ValueError(f"clip width {stream.get('width')} does not match {expected_width}")
            if expected_height and stream.get("height") != expected_height:
                raise ValueError(f"clip height {stream.get('height')} does not match {expected_height}")

        result = {
            "success": True,
            "artifact": str(artifact),
            "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
            "spec_sha256": expected_hash,
            "provenance": provenance,
            "technical": technical,
        }
        print(json.dumps(result))
        return 0
    except Exception as error:
        print(json.dumps({"success": False, "error": str(error)}))
        return 1


def extract_frames(args: argparse.Namespace) -> int:
    try:
        if args.count < 1 or args.count > 20:
            raise ValueError("frame count must be between 1 and 20")
        source = Path(args.input).resolve()
        if not source.is_file():
            raise ValueError(f"input clip does not exist: {source}")
        output = Path(args.output_dir).resolve()
        output.mkdir(parents=True, exist_ok=True)
        technical = ffprobe(source, args.ffprobe)
        duration = float(technical.get("format", {}).get("duration", 0))
        if duration <= 0:
            raise ValueError("ffprobe did not report a positive clip duration")

        frames: list[str] = []
        for index in range(args.count):
            position = duration * (index + 1) / (args.count + 1)
            frame = output / f"frame-{index + 1:02d}.png"
            completed = subprocess.run(
                [
                    args.ffmpeg, "-y", "-ss", f"{position:.6f}", "-i", str(source),
                    "-frames:v", "1", str(frame),
                ],
                check=False,
                capture_output=True,
                text=True,
                timeout=120,
            )
            if completed.returncode != 0 or not frame.is_file():
                raise ValueError(completed.stderr.strip() or "ffmpeg frame extraction failed")
            frames.append(str(frame))
    except Exception as error:
        print(json.dumps({"success": False, "error": str(error)}))
        return 1
    print(json.dumps({"success": True, "frames": frames}))
    return 0


def self_test() -> int:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        artifact = root / "candidate.png"
        artifact.write_bytes(b"\x89PNG\r\n\x1a\ncontract-test")
        spec = root / "spec.json"
        spec.write_text(json.dumps({"kind": "image"}), encoding="utf-8")
        digest = hashlib.sha256(spec.read_bytes()).hexdigest()
        receipt = root / "receipt.json"
        receipt.write_text(json.dumps({
            "success": True,
            "spec_sha256": digest,
            "output_path": str(artifact),
            "provenance": {
                "provider": "comfyui",
                "model": "test",
                "seed": 1,
                "duration_seconds": 1,
                "license_notes": "test",
                "layer3_skills_read": ["comfyui"],
            },
        }), encoding="utf-8")
        args = argparse.Namespace(spec=str(spec), receipt=str(receipt), ffprobe="ffprobe")
        assert check(args) == 0
    print(json.dumps({"success": True, "tests": 1}))
    return 0


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    subparsers = result.add_subparsers(dest="command", required=True)
    check_parser = subparsers.add_parser("check")
    check_parser.add_argument("--spec", required=True)
    check_parser.add_argument("--receipt", required=True)
    check_parser.add_argument("--ffprobe", default="ffprobe")
    frames_parser = subparsers.add_parser("frames")
    frames_parser.add_argument("--input", required=True)
    frames_parser.add_argument("--output-dir", required=True)
    frames_parser.add_argument("--count", type=int, default=3)
    frames_parser.add_argument("--ffmpeg", default="ffmpeg")
    frames_parser.add_argument("--ffprobe", default="ffprobe")
    subparsers.add_parser("self-test")
    return result


def main() -> int:
    args = parser().parse_args()
    if args.command == "check":
        return check(args)
    if args.command == "frames":
        return extract_frames(args)
    return self_test()


if __name__ == "__main__":
    raise SystemExit(main())
