#!/usr/bin/env python3
"""Top Agent Cost & Activity dashboard tile: prints a leaderboard JSON array to stdout.

Parses .agents/channel/cost-log*.jsonl files and ranks agents by total cost and run count.
"""
import glob
import json
import os


def main():
    log_files = glob.glob(".agents/channel/cost-log*.jsonl")
    agent_stats = {}

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
                        agent = data.get("Agent", "unknown") or "unknown"
                        cost = float(data.get("UsdCost", 0))
                        stats = agent_stats.get(agent, {"cost": 0.0, "runs": 0})
                        stats["cost"] += cost
                        stats["runs"] += 1
                        agent_stats[agent] = stats
                    except Exception:
                        pass
        except Exception:
            pass

    if not agent_stats:
        entries = [{"label": "No agent runs recorded", "score": "$0.00"}]
    else:
        sorted_agents = sorted(agent_stats.items(), key=lambda x: (x[1]["cost"], x[1]["runs"]), reverse=True)
        entries = []
        for agent, stats in sorted_agents[:10]:
            runs_suffix = f"({stats['runs']} run{'s' if stats['runs'] != 1 else ''})"
            entries.append({
                "label": f"{agent} {runs_suffix}",
                "score": f"${stats['cost']:.2f}"
            })

    print(json.dumps(entries))


if __name__ == "__main__":
    main()
