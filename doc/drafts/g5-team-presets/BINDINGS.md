# Binding Manifest — Team Preset Agents (Task G5)

This manifest specifies the exact five binding artifacts for the 6 proposed agents (`hypothesis-investigator`, `debug-lead`, `performance-reviewer`, `architecture-reviewer`, `accessibility-reviewer`, `coverage-reviewer`). Apply these entries to `ProjectTemplate/Agents/` and core files when Agent A merges `contracts.json` and `automations.json`.

---

## Roster Summary

| Agent | Team | Model Tier | Tier Criterion |
|---|---|---|---|
| `hypothesis-investigator` | `hypothesis-debug` | `sonnet` | Specialized code & log investigation (medium ambiguity) |
| `debug-lead` | `hypothesis-debug` | `opus` | Root-cause arbitration & synthesis (high judgment / gate) |
| `performance-reviewer` | `parallel-review` | `sonnet` | Code & benchmark audit (medium ambiguity) |
| `architecture-reviewer` | `parallel-review` | `sonnet` | Dependency & boundary audit (medium ambiguity) |
| `accessibility-reviewer` | `parallel-review` | `sonnet` | DOM & ARIA compliance audit (medium ambiguity) |
| `coverage-reviewer` | `parallel-review` | `haiku` | Test coverage & diff analysis (mechanical / low ambiguity) |

---

## 1. `contracts.json` Entries

Add under `contracts` object in `ProjectTemplate/Agents/contracts.json`:

```json
"hypothesis-investigator": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["logs/**", "reports/debug/**", ".agents/hypothesis-investigator/**"],
  "riskClass": "medium",
  "maxReviewCycles": 2
},
"debug-lead": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["doc/specs/**", "reports/debug/**", ".agents/debug-lead/**"],
  "riskClass": "high",
  "maxReviewCycles": 2
},
"performance-reviewer": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["reports/perf/**", ".agents/performance-reviewer/**"],
  "riskClass": "medium",
  "maxReviewCycles": 2
},
"architecture-reviewer": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["reports/arch/**", ".agents/architecture-reviewer/**"],
  "riskClass": "medium",
  "maxReviewCycles": 2
},
"accessibility-reviewer": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["reports/a11y/**", ".agents/accessibility-reviewer/**"],
  "riskClass": "medium",
  "maxReviewCycles": 2
},
"coverage-reviewer": {
  "dispatches": true,
  "ticketExit": "Review",
  "allowedWriteGlobs": ["reports/coverage/**", ".agents/coverage-reviewer/**"],
  "riskClass": "low",
  "maxReviewCycles": 2
}
```

---

## 2. `models.json` Mappings

Add under `mappings` in `ProjectTemplate/Agents/models.json`:

```json
"hypothesis-investigator": "sonnet",
"debug-lead": "opus",
"performance-reviewer": "sonnet",
"architecture-reviewer": "sonnet",
"accessibility-reviewer": "sonnet",
"coverage-reviewer": "haiku"
```

---

## 3. Team Membership Definitions

Add to `GigaClaw.Core/Services/AgentTeamService.cs` (or team seed data once B lands):

```csharp
// Team: hypothesis-debug
new TeamDefinition(
    Slug: "hypothesis-debug",
    Name: "Hypothesis Debug Team",
    Members: new[] { "hypothesis-investigator", "debug-lead" },
    Synthesizer: "debug-lead"
),

// Team: parallel-review
new TeamDefinition(
    Slug: "parallel-review",
    Name: "Parallel Review Team",
    Members: new[] { "performance-reviewer", "architecture-reviewer", "accessibility-reviewer", "coverage-reviewer" },
    Synthesizer: "qa-tester"
)
```

---

## 4. `automations.json` Dispatching Automations

Add to `ProjectTemplate/Agents/automations.json`:

```json
{
  "id": "hypothesis-investigator-on-debug",
  "name": "Investigate assigned debug hypothesis",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "InProgress" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "hypothesis-investigator" }],
  "action": { "type": "dispatchAgent", "agentSlug": "hypothesis-investigator" }
},
{
  "id": "debug-lead-on-review",
  "name": "Synthesize debug hypotheses and arbitrate root cause",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "Review" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "debug-lead" }],
  "action": { "type": "dispatchAgent", "agentSlug": "debug-lead" }
},
{
  "id": "performance-reviewer-on-review",
  "name": "Audit performance budgets on review",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "Review" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "performance-reviewer" }],
  "action": { "type": "dispatchAgent", "agentSlug": "performance-reviewer" }
},
{
  "id": "architecture-reviewer-on-review",
  "name": "Audit architecture boundaries on review",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "Review" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "architecture-reviewer" }],
  "action": { "type": "dispatchAgent", "agentSlug": "architecture-reviewer" }
},
{
  "id": "accessibility-reviewer-on-review",
  "name": "Audit WCAG accessibility on review",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "Review" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "accessibility-reviewer" }],
  "action": { "type": "dispatchAgent", "agentSlug": "accessibility-reviewer" }
},
{
  "id": "coverage-reviewer-on-review",
  "name": "Audit test coverage on review",
  "enabled": true,
  "trigger": { "type": "statusChange", "toStatus": "Review" },
  "conditions": [{ "field": "assignedTo", "operator": "equals", "value": "coverage-reviewer" }],
  "action": { "type": "dispatchAgent", "agentSlug": "coverage-reviewer" }
}
```

---

## 5. Eval Fixtures & Baseline Requirements

When staging to `ProjectTemplate/Agents/`, create:
1. Scenario fixture JSON files in `GigaClaw.Eval/fixtures/scenarios/` for each of the 6 agents.
2. Initial static evaluation baseline JSON entries in `GigaClaw.Eval/baselines/`.
3. Run `dotnet run --project GigaClaw.Eval -c Release -- all --update-baselines` to record initial baselines.
