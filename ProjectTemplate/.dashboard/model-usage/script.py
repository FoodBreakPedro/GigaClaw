#!/usr/bin/env python3
"""Model Cost Breakdown dashboard tile: prints a donut JSON array to stdout.

Parses .agents/channel/cost-log*.jsonl files and groups total cost by model.
"""
import glob
import json
import os

PALETTE = [
    "#22c55e", "#3b82f6", "#f59e0b", "#ef4444", "#a855f7",
    "#06b6d4", "#ec4899", "#84cc16", "#f97316", "#14b8a6",
]


def main():
    log_files = glob.glob(".agents/channel/cost-log*.jsonl")
    model_costs = {}

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
                        model = data.get("Model", "default") or "default"
                        cost = float(data.get("UsdCost", 0))
                        model_costs[model] = model_costs.get(model, 0.0) + cost
                    except Exception:
                        pass
        except Exception:
            pass

    if not model_costs:
        slices = [{"label": "No Runs", "value": 1.0, "color": "#6b7280"}]
    else:
        sorted_models = sorted(model_costs.items(), key=lambda x: x[1], reverse=True)
        slices = []
        for idx, (model, cost) in enumerate(sorted_models):
            color = PALETTE[idx % len(PALETTE)]
            val = round(cost, 4) if cost > 0 else 0.0001
            slices.append({
                "label": f"{model} (${cost:.2f})" if cost > 0 else model,
                "value": val,
                "color": color
            })

    print(json.dumps(slices))


if __name__ == "__main__":
    main()
