#!/usr/bin/env python3
"""Agent Execution Performance dashboard tile: prints a status-grid JSON array to stdout.

Parses .agents/channel/cost-log*.jsonl files and evaluates success rates, average duration, and error counts.
"""
import glob
import json
import os


def main():
    log_files = glob.glob(".agents/channel/cost-log*.jsonl")
    total_runs = 0
    success_runs = 0
    failed_runs = 0
    total_duration = 0.0

    for path in log_files:
        if not os.path.isfile(path):
            continue
        try:
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        data = json.loads(line)
                        total_runs += 1
                        exit_code = int(data.get("ExitCode", 0))
                        duration = float(data.get("DurationSeconds", 0))
                        total_duration += duration
                        if exit_code == 0:
                            success_runs += 1
                        else:
                            failed_runs += 1
                    except Exception:
                        pass
        except Exception:
            pass

    if total_runs == 0:
        cells = [
            {"label": "Success Rate", "status": "ok", "detail": "100% (No runs)"},
            {"label": "Avg Duration", "status": "ok", "detail": "0s"},
            {"label": "Failures", "status": "ok", "detail": "0 errors"},
            {"label": "Total Runtime", "status": "ok", "detail": "0s"}
        ]
    else:
        success_pct = (success_runs / total_runs) * 100
        avg_dur = total_duration / total_runs
        status_code = "ok" if success_pct >= 90 else ("warn" if success_pct >= 70 else "err")

        mins = int(total_duration // 60)
        secs = int(total_duration % 60)
        runtime_str = f"{mins}m {secs}s" if mins > 0 else f"{secs}s"

        cells = [
            {"label": "Success Rate", "status": status_code, "detail": f"{success_pct:.1f}% ({success_runs}/{total_runs})"},
            {"label": "Avg Duration", "status": "ok", "detail": f"{avg_dur:.1f}s per run"},
            {"label": "Failures", "status": "err" if failed_runs > 0 else "ok", "detail": f"{failed_runs} failed run{'s' if failed_runs != 1 else ''}"},
            {"label": "Total Runtime", "status": "ok", "detail": runtime_str}
        ]

    print(json.dumps(cells))


if __name__ == "__main__":
    main()
