# Security review in this workspace

The **Security Assurance** pack is installed. It adds four review agents, one team, and seven
automations. This file is the workspace-level guide to what they do and what they will refuse to do.
It is a pack-owned root file: the pack never edits `CLAUDE.md` or the shared agent preamble.

## The four lanes

| Agent | Answers | Fires on |
|---|---|---|
| `security-auditor` | Can an attacker reach this change, and what do they get? | `code`-labeled ticket entering **Review** |
| `secrets-reviewer` | Did this change put a credential somewhere it does not belong? | `code`-labeled ticket entering **Review** |
| `threat-modeler` | What does this design make possible that its author did not intend? | `design`- or `security`-labeled ticket entering **Review** |
| `supply-chain-reviewer` | Which dependency is known-vulnerable, and is the vulnerable path reached here? | Weekly, Mondays 04:00 (`0 4 * * 1`) |

They also form the `security-review` team, a member filter today and the four parallel lanes of an
executable preset once the join-and-synthesizer half of executable teams lands. The upgrade is
additive under the same team slug.

## BLOCK is a veto, and it is structural

Every lane emits a typed verdict (`doc/verdict-contract.md`). A Critical finding becomes a
`vetoItems` entry, and the verdict validator **rejects `SHIP` whenever `vetoItems` is non-empty**. So
a reviewer cannot weigh a Critical finding against a good score and let the ticket through — the
document that would say so is invalid, and `verdictIs` resolves an invalid verdict exactly like a
`BLOCK`.

Each lane has an escalation automation (`*-verdict-escalate`) that parks the ticket in **Blocked** and
posts an owner-facing receipt on `BLOCK`, `INVALID` or `STALE`. A reviewer that answers in prose
instead of a verdict reads as `MISSING`, so the ticket stalls visibly rather than advancing.

**Clearing a BLOCK means fixing the finding and earning a fresh verdict against the new bytes.**
Moving the ticket by hand does not clear it; the next comment re-fires the gate.

## Freshness: two lanes are file-based, two are not

`verdictIs` re-hashes the files a verdict lists as `path` evidence and calls the verdict `STALE`
unless one still matches its `inputDigest`. That works only when the thing reviewed *is* a file:

| Lane | Input | Evidence it cites | `requireFreshArtifact` |
|---|---|---|---|
| `security-auditor` | the changed source files | `path` (primary file first) + `hash` | `true` |
| `secrets-reviewer` | the changed files | `path` (primary file first) + `hash` | `true` |
| `threat-modeler` | the ticket's design text (board state) | `hash` only, plus `link` | `false` |
| `supply-chain-reviewer` | lockfile set + advisory snapshot | `hash` only, plus `link` | `false` |

The bottom two must cite **no `path` evidence at all** — not even the report they wrote. Pointing at
their own output would make the verdict read as stale the moment it is saved.

## What these agents will not do

- **They never fix anything.** Write scope is `doc/security/**` plus each agent's own memory
  directory. Remediation — the patch, the dependency upgrade, the credential rotation — is a
  `programmer` ticket. A reviewer that repairs its own findings has reviewed nothing.
- **They never move a ticket to `Done`.**
- **They never approve what they could not read.** Unreviewable is `BLOCK`, never "probably fine".

## Outbound network: reads yes, mutations no

`supply-chain-reviewer` has outbound access to exactly three advisory hosts — `api.osv.dev`,
`api.github.com`, `services.nvd.nist.gov` — declared in the pack manifest under
`permissions.networkHosts`.

That grant covers **retrieval only**, and the distinction is the pack's design, not a convention:

- **Reading an advisory feed changes nothing outside this workspace.** It is idempotent, and denying
  it would not make the pack safer — it would make a dependency audit report yesterday's truth with
  today's confidence.
- **Mutating anything outside the workspace is a different act, and the pack cannot perform it.** No
  automation carries an `httpRequest` action, and `httpRequest` is deliberately absent from the
  manifest's `permissions.actions`, so the composition gate rejects the pack if one is ever added
  without the owner revisiting the decision. Remediation stays **dry-run by default and by
  construction**: proposals land in `doc/security/**` and on the board, never in a pull request, a
  webhook or a notification.
- Queries carry **package coordinates only** — name, ecosystem, version. Never source, never lockfile
  contents, never ticket text.

If the feeds cannot be reached, the audit is a `BLOCK` (`advisory-feed-unreachable`). Reporting
"no known vulnerabilities" without having asked is the worst output this lane can produce.

## Where things land

```
doc/security/audits/ticket-{id}.md          security-auditor
doc/security/audits/dependencies-{date}.md  supply-chain-reviewer
doc/security/secrets/ticket-{id}.md         secrets-reviewer
doc/security/threat-models/ticket-{id}.md   threat-modeler
doc/security/sbom/current.json              scripts/sbom_diff.py inventory
```

## Turning a lane off

Set `enabled: false` on the automation in `.agents/automations.json`. Disabling a *gate* without
disabling its `*-verdict-escalate` twin is safe — the escalation only fires on a verdict that exists.
Disabling an escalation while its gate still runs is not: the lane will keep producing `BLOCK`
verdicts that nothing acts on, which is a security suggestion rather than a security gate.
