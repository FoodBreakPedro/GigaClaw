# GigaClaw static agent eval

Run the complete committed catalog:

```bash
dotnet run --project GigaClaw.Eval -- all
```

Run one catalog agent by slug:

```bash
dotnet run --project GigaClaw.Eval -- programmer
```

The default **baseline** mode fails unreadable or malformed inputs, missing/drifted
baselines, and other integrity errors. Matching policy findings remain visible but
do not fail. `--strict` also fails every policy warning or error.

Reviewed per-agent snapshots live in `GigaClaw.Eval/baselines/`. Regenerate them
for review with `all --update-baselines`, then regenerate the system catalog so
its `EvalBaselinePresent` fields stay current. Normal run reports overwrite
`artifacts/eval/<agent|all>.json`; that configured artifact root is gitignored.

Prompt budget source, units, and thresholds are versioned in `evalconfig.json`.
The console prints actual elapsed time; reports and baselines omit timestamps and
timings so identical inputs produce identical committed/output JSON.
