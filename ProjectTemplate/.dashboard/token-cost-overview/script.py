#!/usr/bin/env python3
"""Token & Cost Overview dashboard tile: prints a kpi-grid JSON array to stdout.

Parses .agents/channel/cost-log*.jsonl files in the workspace directory.
"""
import glob
import json
import os


def main():
    log_files = glob.glob(".agents/channel/cost-log*.jsonl")
    total_cost = 0.0
    input_tokens = 0
    output_tokens = 0
    cache_read = 0
    cache_write = 0
    runs = 0

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
                        total_cost += float(data.get("UsdCost", 0))
                        input_tokens += int(data.get("InputTokens", 0))
                        output_tokens += int(data.get("OutputTokens", 0))
                        cache_read += int(data.get("CacheReadTokens", 0))
                        cache_write += int(data.get("CacheWriteTokens", 0))
                        runs += 1
                    except Exception:
                        pass
        except Exception:
            pass

    total_tokens = input_tokens + output_tokens
    total_input_context = input_tokens + cache_read
    cache_savings_pct = (cache_read / total_input_context * 100) if total_input_context > 0 else 0.0

    cards = [
        {"label": "Total Cost", "value": f"${total_cost:.2f}", "unit": "USD"},
        {"label": "Total Tokens", "value": f"{total_tokens:,}"},
        {"label": "Cache Savings", "value": f"{cache_savings_pct:.1f}%"},
        {"label": "Agent Runs", "value": str(runs)}
    ]
    print(json.dumps(cards))


if __name__ == "__main__":
    main()
