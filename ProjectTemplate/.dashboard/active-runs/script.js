const path = require("path");

const apiUrl = (process.env.GIGACLAW_API_URL || "http://localhost:5230").replace(/\/$/, "");
const slug = path.basename(process.cwd())
  .toLowerCase()
  .replace(/[^a-z0-9]+/g, "-")
  .replace(/^-+|-+$/g, "");

function formatElapsed(startedAt) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000));
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

(async () => {
  try {
    const res = await fetch(`${apiUrl}/api/projects/${slug}/runs`, { signal: AbortSignal.timeout(3000) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const runs = await res.json();

    const active = (runs || [])
      .filter((r) => r.status === "Running")
      .sort((a, b) => new Date(b.startedAt) - new Date(a.startedAt))
      .map((r) => ({
        Ticket: r.ticketId != null ? `#${r.ticketId}` : "—",
        Agent: r.agentName,
        Started: new Date(r.startedAt).toLocaleTimeString(),
        Elapsed: formatElapsed(r.startedAt),
      }));

    const rows = active.length > 0 ? active : [{ Status: "No active runs" }];
    process.stdout.write(JSON.stringify(rows));
  } catch (_err) {
    process.stdout.write(JSON.stringify([{ Status: "No active runs" }]));
  }
})();
