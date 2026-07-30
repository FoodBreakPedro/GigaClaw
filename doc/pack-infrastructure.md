# Pack infrastructure (O7) — design spec

**Status:** drafted by lane CL (task C9), **approved 2026-07-30** (§10). Implemented by lane CX-T (task T6) — see [§11](#11-implementation-status-t6) for what has landed.
**Scope:** the manifest, the composition rules, the versioning model, the core-pack extraction, and the CI gate that keeps packs from becoming a 203-agent catalog. Not the pack contents — those are GM authoring work, sequenced in [packs-and-later.md](./roadmap/packs-and-later.md).

Everything asserted here about current behaviour was read out of the code at `lane/claude-orch` `f4dd69a` (and, for `GigaClaw.Catalog`/`GigaClaw.Eval`, `lane/cx-tooling`). Where a decision could go two ways, both are stated, one is recommended, and the reason is given. Four questions are escalated to the owner in §9 — they are the only ones left open.

---

## 1. What a pack is

A pack is a **versioned, self-contained bundle of agent capability** — skills, contracts, model defaults, teams, automations, eval fixtures — that composes into a workspace at Initialize. Today there is exactly one implicit pack: `ProjectTemplate/`, embedded into `GigaClaw.Core.dll` and written to `<workspace>/.agents/` + workspace root by `AgentsTemplateService.InitializeAsync`. O7 makes that bundle explicit, names it `core`, and lets others sit beside it.

A pack is **not** a plugin: it ships no code, no assemblies, no new automation vocabulary. It is data composed by the host. A pack that needed a new trigger, condition or action type would be a core change first (lane CL owns that vocabulary) and a pack second.

## 2. On-disk layout

**Decision D1 — core stays in place; other packs live under `Packs/<id>/`.**

```
ProjectTemplate/            → pack `core` (unmoved)
  pack.json                   NEW: the core manifest
  Agents/                     33 agent dirs, scripts/, contracts.json, models.json,
                              automations.json, teams.json (NEW), preamble.md, …
  CLAUDE.md .gitignore .dashboard/     → workspace root

Packs/<pack-id>/            → every other pack, identical internal layout
  pack.json
  Agents/<slug>/SKILL.md, memory/MEMORY.md
  Agents/scripts/**           optional shared helpers this pack contributes
  Agents/{contracts,models,automations,teams}.json    fragments
  eval/fixtures/*.json, eval/fixtures/scenarios/*.ndjson
  <anything else>             → workspace root
```

Composition rule for a pack directory: `pack.json` is the manifest, `Agents/**` composes into `<workspace>/.agents/`, `eval/**` is build-time only and is never written to a workspace, and everything else composes into the workspace root. That is exactly the rule `GigaClaw.Core.csproj` already encodes with its two `LogicalName` prefixes, generalized per pack.

*Alternative considered:* `git mv ProjectTemplate Packs/core` for symmetry. **Rejected.** `ProjectTemplate/Agents` is hardcoded in `CatalogGenerator.Generate`, four times in `StaticEvalRunner`, in `TemplateAutomationContractTests`, `TemplateHandoffContractTests`, `TemplateVerdictContractTests`, `ReceiptChainTests`, `tools/check-automation-drift.sh`, `CLAUDE.md` and `doc/project-template.md` — and `GigaClaw.Eval/evalconfig.json` pins the literal string `"ProjectTemplate/Agents/{agent}/SKILL.md"`, which `StaticEvalRunner.ValidateConfig` *throws* on if it differs. Moving the directory turns a composition change into a fifteen-file rename across three lanes and makes §5's byte-identity proof harder to trust, not easier. The asymmetry costs one sentence of documentation; the move costs a merge window.

Embedding: a third `LogicalName` prefix, `GigaClaw.Core.Packs/<pack-id>/…`, added to `GigaClaw.Core.csproj`. The existing root glob `..\ProjectTemplate\**\*.*` (Exclude `Agents`) is untouched — which is the second reason packs live at `Packs/` rather than `ProjectTemplate/packs/`: under `ProjectTemplate/` they would be swept into the *root* prefix and written to the workspace root.

## 3. Manifest schema (`pack.json`, schemaVersion 1)

| Field | Type | Req | Meaning |
|---|---|---|---|
| `schemaVersion` | int | ✓ | `1`. A pack with a higher value is refused, not best-effort parsed. |
| `id` | string | ✓ | `^[a-z][a-z0-9-]{1,38}$`. Must equal the directory name. Globally unique. |
| `name` · `description` | string | ✓ | Human labels for the install UI. |
| `version` | string | ✓ | Semver `MAJOR.MINOR.PATCH`, no pre-release/build metadata (see D6). |
| `kind` | `"core"` \| `"specialist"` | ✓ | Exactly one pack may declare `core`. |
| `removable` | bool | ✓ | `false` for `core`; uninstall refuses. |
| `requiresRuntime` | `{min: int, max: int}` | ✓ | Bounds on the **pack-runtime version** (§5). Inclusive. |
| `dependsOn[]` | `{id: string, minVersion: string}` | | Other packs this one references. Minimum only — no ranges (D7). |
| `provides` | object | ✓ | The declared inventory, **verified against the tree at compose time** (below). |
| `provides.agents[]` | string[] | ✓ | Agent slugs. Must equal the set of `Agents/*/SKILL.md` directories. |
| `provides.scripts[]` | string[] | | Paths relative to `Agents/`, e.g. `scripts/sbom_diff.py`. |
| `provides.teams[]` | string[] | | New team slugs defined in `Agents/teams.json`. |
| `provides.automations[]` | string[] | | Automation ids defined in `Agents/automations.json`. |
| `provides.rootFiles[]` | string[] | | Workspace-root-relative paths this pack writes. |
| `teamMembership` | `{teamSlug: string[]}` | | **Additive** membership in teams owned by core or a dependency. Never removes. |
| `automationPatches[]` | `{automation, op, slugs[] \| labels[]}` | | Additive edits to another pack's automations. `op` ∈ `addAssignees`, `addLabels`. Nothing else in v1 (D4). |
| `receiptEmitters` | `{family: string[]}` | | Receipt marker families this pack's agents emit. Unions into the core emitter table (§6). |
| `permissions` | object | ✓ | See below. Ceiling, not grant. |
| `permissions.riskClasses[]` | string[] | ✓ | New `contracts.json` `riskClass` values this pack introduces. Unknown risk classes fail closed in P3 enforcement, so they must be declared. |
| `permissions.actions[]` | string[] | ✓ | Automation action types this pack's automations use. A pack whose automations use an action not listed is rejected. |
| `permissions.network` | `"none"` \| `"declared"` | ✓ | `declared` requires `networkHosts`. |
| `permissions.networkHosts[]` | string[] | | Hostnames the pack's `httpRequest` actions may target. Enforced by the `ActionExecutor` preflight (U17), not by this manifest — the manifest is the *declaration* the preflight reads. |
| `permissions.allowedWriteGlobs[]` | string[] | ✓ | Union ceiling. Every per-agent `allowedWriteGlobs` in the pack's `contracts.json` must be a subset; a pack cannot smuggle `**` past a reviewer reading only the manifest. |
| `evalFixtures[]` | string[] | ✓ | Fixture ids under `eval/fixtures/`. Must cover every slug in `provides.agents` (§6). |

**`provides` is declared *and* verified.** The composer walks the tree, computes the actual inventory, and fails on any mismatch in either direction. Declaring it (rather than deriving it) is what makes a manifest reviewable on its own and makes uninstall an explicit set rather than a re-walk of a tree that may have drifted.

## 4. Composition

One implementation, `GigaClaw.Core/Services/PackComposer.cs`, used by **both** `AgentsTemplateService.InitializeAsync` and `GigaClaw.Catalog`. This is not a style preference: if the catalog composes independently, a green catalog is not evidence about the bytes Initialize writes, and the gate in §6 is decorative.

**Order.** `core` first, then the remaining selected packs in topological dependency order, ties broken by `id` ascending ordinal. Deterministic ordering is required by both the byte-identity invariant (§5) and `CatalogGenerator`'s stable-output contract.

### Merge rules, by artifact

| Artifact | Rule | Collision |
|---|---|---|
| `Agents/<slug>/**` | Union by slug | **Hard error, install refused** (D2) |
| `Agents/scripts/*` | Union by filename | Hard error |
| `contracts.json` `agents` | Merge by key; a pack may only contract for agents it provides | Hard error |
| `contracts.json` `defaults` | **Core only.** A non-core pack carrying `defaults` is rejected | — |
| `models.json` | Merge by key; own agents only | Hard error |
| `automations.json` `automations[]` | Concatenate in composition order | Duplicate `id` = hard error |
| `teams.json` | Union by team slug; `teamMembership` applied after, additive | Duplicate team slug = hard error |
| Root files | Union by relative path | Hard error |

**Decision D2 — agent slugs are one flat global namespace; collisions are refused, never namespaced.** The slug is simultaneously the `Member.Slug` row, the `contracts.json` key, the `models.json` key, the `runAgent.agent` value, the `.agents/<slug>/memory/` directory, the `assignedTo` condition value, and the receipt-chain emitter identity. Namespacing (`security-assurance/security-auditor`) would require a resolver at all seven sites, and `Member.ToSlug` — `Regex.Replace(name, @"[\s_]+", "-")` — has no notion of a separator. Two packs claiming one slug is a packaging bug; the installer says so.

**Decision D3 — a pack may not modify `CLAUDE.md` or the shared preamble.** Root-file collision is a hard error, and `preamble.md` is core-owned. A pack that wants workspace-level guidance ships its own root file (e.g. `SECURITY-REVIEW.md`) and references it from its SKILLs. Rejecting append-to-a-shared-file now avoids inventing a merge-conflict model for prose later.

**Decision D4 — cross-pack references are allowed, but only through a declared dependency.**
At compose time every `runAgent.agent` and every slug in an `assignedTo` condition must resolve to an agent provided by the referencing pack, by `core`, or by a pack named in `dependsOn`. Unresolvable = hard error. This generalizes the check `TemplateAutomationContractTests.Template_deserializes_with_unique_ids_and_resolvable_agent_slugs` already performs against a single template.

*Alternative considered:* forbid cross-pack references entirely. **Rejected** — it makes half the approved packs unimplementable. Pack 2 (Incident & Debug) is specified to escalate on repeated `qa-tester` BLOCK verdicts, and `qa-tester` is a core agent.

`automationPatches` exists because three core automations — `assignee-dispatch` (28 slugs), `assignee-resume` (27), `owner-feedback` (28) — carry explicit `assignedTo` rosters. A pack whose agents are not in those rosters ships agents nobody can assign work to. Both v1 ops are **set additions**, which is what makes uninstall reversible as a set subtraction. Reordering, removal, and trigger edits are refused; a pack that needs them ships its own automation.

### Partial install

**Decision D5 — validate fully in memory, stage, merge per file, commit with the lockfile last.**

Today `InitializeAsync` writes file by file with no rollback and no record of what it wrote. The install becomes:

1. **Compose and validate entirely in memory.** Every rule in this section runs before a single byte is written, so a rejected install leaves the disk untouched.
2. **Stage** into `<workspace>/.agents.staging-<installId>/`.
3. **Merge per file** into `<workspace>/.agents/` and the workspace root, capturing a pre-image of each file overwritten. Any failure mid-merge restores the pre-images and deletes the staging directory.
4. **Write `<workspace>/.agents/packs.lock.json` last.** Its presence with a matching `installId` is what makes the install committed. An interrupted install leaves a stale staging directory and an unchanged lockfile; the next Initialize sweeps the staging directory and re-runs.

*Alternative considered:* rename-swap the whole `.agents` directory. **Rejected** — `.agents/**` also holds live runtime state: per-topic memory files written by the consolidation pass, `evaluator/memory/scores.json`, `documentalist/memory/state.json`, and the owner's edited `automations.json` (the automation editor saves straight back to `<workspace>/.agents/automations.json` via `AutomationStore.SaveAsync`). A wholesale swap destroys all of it. The merge must be per-file and must never touch a path no manifest claims.

### The lockfile

`<workspace>/.agents/packs.lock.json`, schemaVersion 1, the authoritative record of what is installed:

```jsonc
{
  "schemaVersion": 1,
  "installId": "<guid>",
  "installedAtUtc": "2026-07-30T12:00:00Z",
  "packRuntimeVersion": 1,
  "packs": [
    {
      "id": "security-assurance", "version": "1.0.0",
      "agents": ["security-auditor", "…"],
      "automations": ["security-gate-on-review", "dependency-audit-weekly"],
      "teams": ["security-review"],
      "contractKeys": ["security-auditor", "…"],
      "modelKeys": ["security-auditor", "…"],
      "automationPatches": [{ "automation": "assignee-dispatch", "op": "addAssignees", "slugs": ["…"] }],
      "fileHashes": { ".agents/security-auditor/SKILL.md": "sha256:…" }
    }
  ]
}
```

**Decision D6 — the lockfile lives in the workspace, not the registry DB.** The workspace is the artifact that gets cloned, copied and moved between machines; `.agents/` is already the system of record for agent state; and `ProjectTemplate/.gitignore` does not ignore `.agents/`, so the lockfile is committed and reviewable alongside the workspace. A registry column desyncs the first time a workspace moves.

`fileHashes` is the mechanism that makes uninstall safe.

### Uninstall

1. Refuse if `removable: false`, or if any installed pack `dependsOn` this one.
2. For each file in `fileHashes`: hash the file on disk. **Matches** → pack-owned and untouched → delete. **Differs** → the owner edited it → leave it, and report it as orphaned. Never silently delete owner work.
3. Remove the pack's `contracts.json` and `models.json` keys, its team definitions, and reverse its `automationPatches` set-additions — but only entries still byte-identical to what was installed.
4. A pack automation the owner has edited is **set `enabled: false` and reported**, not deleted — an edited automation is owner work, and a dangling automation referencing a removed agent would fire and fail.
5. **Members are not deleted.** `Member` rows carry `DefaultModel` and are referenced by run history and by `assignedTo` on historical tickets. Uninstall leaves them and the UI marks them orphaned ("skill no longer installed"). `MemberService.DeleteMemberAsync` already exists for the owner to do it deliberately.
6. Ticket data, run logs and agent memory directories are never touched.

## 5. Versioning and compatibility

Three versions, deliberately not conflated:

| Version | Type | Owner | Meaning |
|---|---|---|---|
| `pack.json` `schemaVersion` | int | this spec | The manifest field list. Bump = new required/renamed fields. |
| `pack.json` `version` | semver | pack author | The pack's content. Read by humans and by `dependsOn.minVersion`. |
| **pack-runtime version** | int, starts at `1` | `GigaClaw.Core` | The composition contract: manifest fields the installer honours, the automation trigger/condition/action vocabulary, and the contract/verdict/handoff schema versions. Exported as a constant and written into the lockfile. |

**Decision D7 — compatibility is an integer floor/ceiling against the runtime, and a bare minimum-version for pack-to-pack.** `requiresRuntime: {min, max}` and `dependsOn: [{id, minVersion}]`. No range grammar (`>=1.2 <2.0`, carets, tildes) anywhere. Rationale: a range parser is a dependency and a bug surface, there is exactly one producer of the runtime version, and `GigaClaw.Core/Services/VersionCompare.cs` already gives a `System.Version`-based comparison that handles the `minVersion` case without new code. Semver is kept for `version` because humans read it; ranges are the part that earns its keep only in a public registry, which O7 is not (§10 Q1).

**When an installed pack is older than the core expects** (`requiresRuntime.max < currentRuntime`), or newer (`min > currentRuntime`):

The pack is **quarantined, not auto-upgraded and not auto-removed.** Its files stay on disk, its automations are force-disabled at config load with a receipt, its agents are refused at dispatch, and the board surfaces the pack as "needs update". Install of a future-runtime pack is refused outright.

The reason quarantine must be manifest-driven rather than deserialization-driven: `AutomationStore.JsonOptions` sets no `UnmappedMemberHandling`, so System.Text.Json's default (`Skip`) applies — an automation written against a *newer* action vocabulary deserializes cleanly with the unknown field silently dropped, and then runs with different semantics than its author intended. Nothing would throw. The declared `requiresRuntime` bound is the only place that mismatch is visible.

Upgrade of a pack = uninstall then install inside one staged transaction, with the lockfile rewritten once and the §4 owner-edit protections applying to the uninstall half.

## 6. Core-pack extraction — the invariant T6 must prove

`ProjectTemplate/` becomes pack `core`: `kind: "core"`, `removable: false`, `provides.agents` = the 33 slugs, `provides.rootFiles` = `CLAUDE.md`, `.gitignore`, `.dashboard/content-health/{output.json,script.py,tile.yaml}`.

> **Invariant.** For a fixed commit, `InitializeAsync(workspace, overwrite: true)` with the selection `["core"]` produces a set of `(workspace-relative path, sha256)` pairs equal to the set produced by the pre-T6 implementation at the same commit, **plus exactly two new paths**: `.agents/packs.lock.json` and `.agents/teams.json`.

**Test shape.** A committed golden manifest, `GigaClaw.Core.Tests/Fixtures/core-init-manifest.json`, mapping path → `sha256`. It must be generated from the **pre-refactor** build and committed as T6's *first* commit, before any composition code exists — a manifest generated after the refactor proves nothing.

`.agents/teams.json` is the one deliberate addition, and it is not optional:

**Decision D8 — team definitions become template data in T6.** Today the nine built-in teams are C# constants in `AgentTeamService.DefaultTeams` (on `claude-orch/c4-executable-teams`, `DefaultDefinitions` built from `TeamDefinition.FilterOnly(...)` — still constants), and `CatalogGenerator` reads them by calling `new AgentTeamService().GetTeams()`. A pack therefore **cannot add a team or a team membership without recompiling `GigaClaw.Core`**. That makes one of the five binding rules structurally unenforceable for packs, so it has to move. The nine built-ins are extracted verbatim into `ProjectTemplate/Agents/teams.json`; `AgentTeamService` reads composed data, keeping the C# list only as the fallback for already-initialized workspaces that have no `teams.json`; and a test asserts the C# fallback and the parsed file are equal so the two cannot drift during the transition.

*Alternative considered:* keep teams in C# for T6, let packs declare `teams` in the manifest for the catalog's benefit only. **Rejected** — a pack agent would then satisfy the catalog's team check while being invisible in the runtime team filter. A gate that passes on something that does not work is worse than no gate.

**Hazard that threatens the invariant, present right now.** The embedded set is computed by MSBuild globs over the *working directory*, not over git. `..\ProjectTemplate\Agents\scripts\**\*.*` currently embeds 22 files, two of which are `__pycache__/schema_check.cpython-314.pyc` and `__pycache__/verdict_contract.cpython-314.pyc` — gitignored since `e846f79`, still on disk, still shipped into `GigaClaw.Core.dll` and written into every new workspace. Verified by `dotnet msbuild -getItem:EmbeddedResource`. T6 must add `Exclude="..\ProjectTemplate\**\__pycache__\**"` and generate the golden manifest on a clean checkout; otherwise the byte-identity invariant is unprovable by construction, because it depends on whatever untracked files happen to sit in the developer's tree.

(A second, milder version of the same hazard: `%(RecursiveDir)` yields backslashes on Windows and forward slashes on macOS/Linux, so embedded `LogicalName`s differ by build OS. `AgentsTemplateService.ReadAsset` already probes both separators; `PackComposer` must too, and the golden manifest must key on the *destination* path, never on the resource name.)

## 7. The binding rule as a CI gate

**Rule.** A pack agent ships with (1) a contract entry, (2) a model mapping with a stated criterion, (3) a team membership, (4) at least one enabled dispatching automation, and (5) an eval fixture — or the catalog rejects the pack.

What enforces each **today**, and what is missing:

| Binding | Enforced today by | Gap |
|---|---|---|
| Contract entry | `CatalogGenerator.FindBindingGaps` → `"contract"`; `TemplateAutomationContractTests.Shared_contract_manifest_covers_every_template_agent`; `StaticEvalRunner.CheckContract` (also cross-checks `riskClass` against the catalog) | Both read the single `ProjectTemplate/Agents/contracts.json`. Pack-blind, not absent. |
| Model mapping | `FindBindingGaps` → `"model mapping"` (explicit **or** action-level); `StaticEvalRunner.CheckModel` | **The criterion is not checked at all, anywhere.** `models.json` is `slug → modelId` and nothing records *why* an agent is on Haiku rather than Opus. This half of the rule does not exist. |
| Team membership | `FindBindingGaps` → `"team"` | Teams are compiled C# constants (D8). Structurally unavailable to a pack. |
| Dispatching automation | `FindBindingGaps` → `"enabled dispatching automation"`; enabled and disabled reported separately | Pack-blind. `{assignee}` expansion is also duplicated between `CatalogGenerator.ReadAutomations` and `TemplateAutomationContractTests`; the pack-aware composer should become the single implementation. |
| **Eval fixture** | **Nothing.** | `AgentCatalogEntry.EvalBaselinePresent` is computed (does `GigaClaw.Eval/baselines/<slug>.json` exist) but is **not** one of `FindBindingGaps`' four reasons. And a *baseline* is not a *fixture*: baselines are the reviewed static-check snapshot (33 of them), fixtures are replay inputs (6 today, each naming one `Agent`). "Eval fixture" as written in the roadmap is enforced by no check that exists. |

Six changes make the gate real:

1. **`FindBindingGaps` gains a fifth reason, `"eval fixture"`**, computed as `fixtures.Any(f => f.Agent == slug)` — the shape `ReplayRunner.ReadFixture` already validates against the catalog.
2. **`models.json` values become `string | {model: string, criterion: string}`.** Note this is *not* a free extension: `AgentsTemplateService.DefaultModels()` currently does `if (prop.Value.ValueKind == JsonValueKind.String)` and **silently skips** anything else, so an object-valued entry would quietly leave the agent with no default model. `DefaultModels()` must learn the object form in the same commit. Core's 12 existing mappings get criteria retro-fitted by GM.
   *Alternative considered:* a sibling `models.criteria.json`. Rejected — two files that must agree is exactly the drift the catalog exists to prevent.
3. **`ReceiptChainTests.Emitters` moves out of the test into pack data.** It is a hardcoded `Dictionary<string, string[]>` in `GigaClaw.Core.Tests/Automation/ReceiptChainTests.cs` listing which agents may emit `BLOG-REVIEW`, `GIGACLAW-VERDICT`, `GIGACLAW-HANDOFF`, etc. A pack introducing a new emitter of `GIGACLAW-VERDICT` fails that test until a human edits core's test file. Each pack declares `receiptEmitters`; the test composes the union across packs.
4. **`--strict` becomes the CI default.** `.github/workflows/ci.yml` runs `dotnet run --project GigaClaw.Catalog -- check` with no `--strict`; binding gaps are printed to stderr and ignored. Per T2 this flips at SP-1. For packs it is unconditional: **a pack is gated in strict mode from its first commit**, whatever mode core is in. A pack that cannot meet the bar on day one is the anti-pattern the rule exists to prevent.
5. **`permissions.allowedWriteGlobs` subset check** — each per-agent `allowedWriteGlobs` in the pack's `contracts.json` must be a subset of the manifest ceiling. Nothing checks write scope against anything today.
6. **`permissions.actions` closure check** — every action type used by the pack's automations must appear in `permissions.actions`.

## 8. Worked manifest — Security Assurance

The first pack, and the one that proves the infrastructure. Four agents, one team, two automations, four fixtures.

```jsonc
// Packs/security-assurance/pack.json
{
  "schemaVersion": 1,
  "id": "security-assurance",
  "name": "Security Assurance",
  "description": "Adversarial security review as a four-lane parallel preset: code audit, threat model, supply chain, secrets.",
  "version": "1.0.0",
  "kind": "specialist",
  "removable": true,
  "requiresRuntime": { "min": 1, "max": 1 },
  "dependsOn": [{ "id": "core", "minVersion": "1.0.0" }],

  "provides": {
    "agents": ["security-auditor", "threat-modeler", "supply-chain-reviewer", "secrets-reviewer"],
    "scripts": ["scripts/sbom_diff.py"],
    "teams": ["security-review"],
    "automations": ["security-gate-on-review", "security-verdict-escalate", "dependency-audit-weekly"],
    "rootFiles": ["SECURITY-REVIEW.md"]
  },

  "teamMembership": {
    "software-engineering": ["security-auditor"],
    "governance-ops": ["threat-modeler"]
  },

  "automationPatches": [
    { "automation": "assignee-dispatch", "op": "addAssignees",
      "slugs": ["security-auditor", "threat-modeler", "supply-chain-reviewer", "secrets-reviewer"] },
    { "automation": "assignee-resume", "op": "addAssignees",
      "slugs": ["security-auditor", "threat-modeler", "supply-chain-reviewer", "secrets-reviewer"] },
    { "automation": "owner-feedback", "op": "addAssignees",
      "slugs": ["security-auditor", "threat-modeler", "supply-chain-reviewer", "secrets-reviewer"] }
  ],

  "receiptEmitters": {
    "GIGACLAW-VERDICT": ["security-auditor", "supply-chain-reviewer", "secrets-reviewer"],
    "GIGACLAW-HANDOFF": ["threat-modeler", "security-auditor"]
  },

  "permissions": {
    "riskClasses": ["security-review", "security-design"],
    "actions": ["runAgent", "addComment", "moveTicketStatus", "setLabels",
                "consolidateAgentMemory", "commitAgentMemory"],
    "network": "none",
    "allowedWriteGlobs": ["doc/security/**", ".agents/*/memory/**"]
  },

  "evalFixtures": [
    "security-injection-in-review",
    "security-threat-model-auth",
    "security-supply-chain-advisory",
    "security-secret-in-diff"
  ]
}
```

`permissions.network` is **`none`**, deliberately. `supply-chain-reviewer` wants advisory data (OSV, GHSA), which means an `httpRequest` action — and per the roadmap's own codebase validation, Claude `PreToolUse` hooks cannot govern GigaClaw's host-side `httpRequest`; that needs the U17 `ActionExecutor` preflight, which is not built. v1.0.0 of the pack works from lockfiles and the repository's own manifests. Network is a `1.1.0` change gated on U17, and it is escalation Q4.

### Bindings, all five, per agent

| Agent | Contract (`riskClass`, `allowedWriteGlobs`) | Model + criterion | Team | Dispatching automation | Eval fixture |
|---|---|---|---|---|---|
| `security-auditor` | `security-review`, `["doc/security/**", ".agents/security-auditor/memory/**"]` | `claude-opus-4-8` — *adversarial reasoning over untrusted-input paths; emits a gating verdict with veto power, so a miss is a shipped vulnerability* | `security-review`, `software-engineering` | `security-gate-on-review` (enabled) | `security-injection-in-review` |
| `threat-modeler` | `security-design`, `["doc/security/**", ".agents/threat-modeler/memory/**"]` | `claude-opus-4-8` — *open-ended system decomposition with no checklist to fall back on; output quality is dominated by breadth of hypothesis generation* | `security-review`, `governance-ops` | `assignee-dispatch` via patch (enabled) | `security-threat-model-auth` |
| `supply-chain-reviewer` | `security-review`, `["doc/security/**", ".agents/supply-chain-reviewer/memory/**"]` | `claude-sonnet-4-6` — *mechanical diffing of lockfiles against advisory data; the judgement is lookup-and-compare, not reasoning* | `security-review` | `dependency-audit-weekly` (enabled) | `security-supply-chain-advisory` |
| `secrets-reviewer` | `security-review`, `["doc/security/**", ".agents/secrets-reviewer/memory/**"]` | `claude-haiku-4-5` — *`scripts/privacy_guard.py` does the detection; the agent triages a bounded, pre-filtered candidate list* | `security-review` | `security-gate-on-review` (enabled) | `security-secret-in-diff` |

Every `allowedWriteGlobs` above is a subset of the manifest ceiling — these agents write findings and memory, never code. Remediation is a `programmer` ticket, which is what keeps the reviewer honest.

### Team

```jsonc
// Packs/security-assurance/Agents/teams.json
[{
  "slug": "security-review",
  "name": "Security Review",
  "description": "Four-lane adversarial review: code audit, threat model, supply chain, secrets.",
  "icon": "🔐",
  "agentSlugs": ["security-auditor", "threat-modeler", "supply-chain-reviewer",
                 "secrets-reviewer", "producer", "evaluator", "documentalist"]
}]
```

Filter-only at v1.0.0. The executable four-lane `TeamDefinition` — parallel task graph, quorum join, `security-auditor` as synthesizer — plugs into C8's `parallel-review` machinery and lands once C4 part 3 (join + synthesizer) is merged. Shipping the filter first means the pack is usable before C8, and the upgrade is additive within the same team slug.

### Automations

```jsonc
// Packs/security-assurance/Agents/automations.json
{ "automations": [
  {
    "id": "security-gate-on-review",
    "name": "Security: audit code tickets entering Review",
    "enabled": true,
    "trigger": { "type": "statusChange", "pollSeconds": 30, "to": "Review" },
    "conditions": [{ "type": "labels", "labels": ["code"] }],
    "actions": [
      { "type": "runAgent", "agent": "security-auditor", "maxTurns": 80,
        "concurrencyGroup": "security-auditor", "mutuallyExclusiveWith": [], "env": {},
        "model": "claude-opus-4-8", "restoreStatusOnFail": false },
      { "type": "runAgent", "agent": "secrets-reviewer", "maxTurns": 30,
        "concurrencyGroup": "secrets-reviewer", "mutuallyExclusiveWith": [], "env": {},
        "model": "claude-haiku-4-5", "restoreStatusOnFail": false },
      { "type": "consolidateAgentMemory", "agent": "security-auditor" },
      { "type": "commitAgentMemory", "agent": "security-auditor" }
    ]
  },
  {
    "id": "security-verdict-escalate",
    "name": "Security: any BLOCK, invalid or stale verdict stops the ticket",
    "enabled": true,
    "trigger": { "type": "ticketCommentAdded", "pollSeconds": 30 },
    "conditions": [
      { "type": "verdictIs", "verdicts": ["BLOCK", "INVALID", "STALE"], "agent": "security-auditor" }
    ],
    "actions": [
      { "type": "moveTicketStatus", "status": "Blocked" },
      { "type": "addComment", "author": "security-auditor",
        "body": "Security review blocked this ticket. See the GIGACLAW-VERDICT comment above." }
    ]
  },
  {
    "id": "dependency-audit-weekly",
    "name": "Security: weekly supply-chain audit",
    "enabled": true,
    "trigger": { "type": "interval", "cron": "0 4 * * 1" },
    "conditions": [],
    "actions": [
      { "type": "runAgent", "agent": "supply-chain-reviewer", "maxTurns": 40,
        "concurrencyGroup": "git", "mutuallyExclusiveWith": [], "env": {},
        "model": "claude-sonnet-4-6", "restoreStatusOnFail": false },
      { "type": "consolidateAgentMemory", "agent": "supply-chain-reviewer" },
      { "type": "commitAgentMemory", "agent": "supply-chain-reviewer" }
    ]
  }
]}
```

Note what the verdict machinery buys here: `BLOCK` is a **hard veto** because `verdictIs` resolves `BLOCK`, `INVALID`, `STALE` and `MISSING` as distinct outcomes — a security agent that answers in prose reads as `MISSING` and the ticket stalls visibly rather than advancing. That is the difference between a security gate and a security suggestion, and it is why this pack is gated on SP-2 rather than shippable now.

## 9. Integration points

### `GigaClaw.Catalog`

- `CatalogGenerator.Generate(repositoryRoot)` hardcodes `Path.Combine(root, "ProjectTemplate", "Agents")` and reads exactly three JSON files from it. It becomes: discover manifests (`ProjectTemplate/pack.json` + `Packs/*/pack.json`) → call `PackComposer` → run today's logic over the composed result. No other logic changes.
- It also calls `new AgentTeamService().GetTeams()` for the team dimension — after D8 it reads the composed `teams.json`.
- `AgentCatalogEntry` gains `Pack` (string) and `EvalFixturePresent` (bool). `SystemCatalog` gains `Packs: [{Id, Version, DependsOn, AgentCount}]` and bumps `Version` from `2` to `3`. The committed `catalog.json` / `doc/catalog.md` regenerate; the drift check in CI is what proves the bump was intentional.
- `FindBindingGaps` gains the fifth reason (§7.1) and prefixes each message with the owning pack. Strict mode still fails the whole build on any gap in any pack — the catalog is one artifact, and per-pack partial greenness would let a broken pack sit in the tree.
- `CheckReadmeCounts` is unaffected: the opt-in marker is a total, and totals stay totals.

### `GigaClaw.Eval`

- `StaticEvalRunner` hardcodes `ProjectTemplate/Agents/<slug>` for the SKILL path, the memory stub, and the script-existence check, plus `contracts.json` in `ReadContracts()`. All four resolve through the composed catalog's new `Pack` field instead.
- **`evalconfig.json` is a blocker in its current form:** `ValidateConfig` throws unless `PromptBudget.Source` equals the literal `"ProjectTemplate/Agents/{agent}/SKILL.md"`. That literal becomes `"{packRoot}/Agents/{agent}/SKILL.md"` and the validator relaxes from string equality to a placeholder-shape check. Config `Version` bumps to `2`.
- `Replay.FixtureRoot` (singular, `EnumerateFiles(..., "*.json")`, non-recursive) becomes a list: core's `GigaClaw.Eval/fixtures` plus each pack's `Packs/<id>/eval/fixtures`. **Decision: pack fixtures ship with the pack**, not in the core eval project — a pack has to be reviewable and removable as one directory. Fixture ids stay globally unique (`ReadFixture` already requires `<Id>.json` as the filename, and `ResolveFixtures` matches on id/family/agent, so a duplicate id would silently double-run).
- **Baselines stay flat** under `GigaClaw.Eval/baselines/<slug>.json`, not sharded per pack. Agent slugs are globally unique by D2, `BaselinePath` stays a one-liner, and the baselines are the *reviewed* snapshot — a core-owned review artifact about pack content, which is the right ownership split.
- `EvalConfig.ArtifactRoot`/`BaselineRoot` keep their repository-relative escape check; pack fixture roots satisfy it unchanged.
- `GigaClaw.Eval` already project-references `GigaClaw.Catalog` and reads `catalog.json` from the repo root, so per-pack eval falls out of the composed catalog with no new discovery logic in the eval project.

### CI

`.github/workflows/ci.yml` gains, after the existing catalog drift check:

```yaml
- name: Pack composition and strict binding gate
  run: |
    dotnet run --project GigaClaw.Catalog -- check --strict-packs
    dotnet run --project GigaClaw.Eval -c Release --no-build -- all --strict
```

`--strict-packs` is strict for pack agents and baseline for core agents, so the pack gate can land before core's `content-writer` gap closes at SP-1 without either blocking the other.

## 10. Owner decisions (Approved 2026-07-30)

| # | Question | Decision & Resolution |
|---|---|---|
| **Q1** | **Can packs ever come from outside the repo?** | **Repo-only for O7.** Packs ship inside the GigaClaw repo and are embedded into `GigaClaw.Core.dll`; selected at Initialize. |
| **Q2** | **May the Security pack land while core's `content-writer` binding gap is open?** | **Yes, land it.** Uses `--strict-packs` so the Security pack lands without waiting for core's `content-writer` gap. |
| **Q3** | **Model tier for security-auditor & threat-modeler in Security pack?** | **Use Sonnet 4-6.** `security-auditor` uses `claude-sonnet-4-6` to manage recurring ticket review costs. |
| **Q4** | **Should `supply-chain-reviewer` be allowed network access before U17 lands?** | **Yes, enable network access immediately.** Outbound network access is enabled for live vulnerability advisory queries. |

---

## 11. Implementation status (T6)

Landed in `GigaClaw.Core/Packs/`:

| File | Covers |
|---|---|
| `PackManifest.cs` | The §3 record model plus `PackRuntime` (the §5 pack-runtime constant) and `PackCompatibility`. |
| `PackManifestParser.cs` | §3 parsing and every rule decidable from one manifest. Hand-parsed, so a renamed or unknown field can never be silently dropped. |
| `IPackSource.cs` | `DirectoryPackSource` (working tree, for the build-time tools) and `EmbeddedPackSource` (Q1's production shape). The core-pack extraction plugs in as one more `EmbeddedPackSource`. |
| `PackComposer.cs` | §4 composition: declared-and-verified `provides`, D2 collisions, D4 references, core-first topological order, the §4 merge table, §7.5/§7.6 permission closure. |
| `PackInstaller.cs` · `PackInstaller.Uninstall.cs` · `WorkspaceMergeTransaction.cs` | D5's staged install and §4's uninstall. |
| `PackLock.cs` · `PackFileHash.cs` | `packs.lock.json` and the single hash implementation install and uninstall must agree on. |

Three details the §4 sketch leaves implicit, resolved as follows:

- **Merge artifacts are not opaque files.** `automations.json`, `contracts.json`, `models.json` and `teams.json` are read from the workspace, merged in memory and written back — they are never copied over, because the owner's automation edits land in the same file via `AutomationStore.SaveAsync`. They are therefore tracked by the lockfile's `automations`/`contractKeys`/`modelKeys`/`teams` key lists, not by `fileHashes`, which is exactly why §4's lockfile carries both.
- **Lock entries carry the data uninstall's own rules require**: `kind`, `removable` and `dependsOn` (steps 1's two refusals), `requiresRuntime` (quarantine after a runtime bump), and `mergeEntryHashes` — the per-entry hash that answers step 3's "still byte-identical to what was installed?" for merge artifacts, which the key lists alone cannot.
- **`teamMembership` reversal** is a set subtraction of the pack's own agent slugs from every surviving team, matching the `automationPatches` reversal in step 3.

Not yet landed, and sequenced after the teams change: the core-pack extraction (§6), `AgentTeamService` reading composed `teams.json` (D8), and the catalog/eval integration and CI gate (§7, §9). Until core is a pack, `PackComposeOptions.HostProvidedAgents` lets D4's reference resolution see the `ProjectTemplate` agents that belong to no manifest; the installer fills it from the workspace, the catalog passes nothing, and it is empty at every call site once §6 lands.

---

Related: [project template](./project-template.md) · [verdict contract](./verdict-contract.md) · [handoff contract](./handoff-contract.md) · [automation engine](./automation-engine.md) · [roadmap: packs](./roadmap/packs-and-later.md) · [lane CL](./roadmap/lane-claude-orchestration.md) · [lane CX-T](./roadmap/lane-codex-tooling.md).
