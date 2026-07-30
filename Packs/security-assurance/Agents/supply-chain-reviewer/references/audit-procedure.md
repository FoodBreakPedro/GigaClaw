---
name: audit-procedure
description: Inventory recipe, advisory queries, the composite-digest rule, version-range arithmetic and the reachability call.
---

# Audit procedure

## 1. Inventory

```bash
python3 .agents/scripts/sbom_diff.py --root . --out doc/security/sbom/current.json
python3 .agents/scripts/sbom_diff.py --root . --baseline doc/security/sbom/previous.json --format markdown
```

The script enumerates lockfiles and manifests it recognizes and reports `added`, `removed`,
`upgraded` and `downgraded` packages. It is deliberately dependency-free and offline: it reads files
and prints JSON, so it cannot itself be the thing that reaches the network.

Anything the script does **not** recognize — a vendored directory, a git submodule, a binary checked
into the tree, a container base image named in a Dockerfile — is still part of the supply chain.
Enumerate it by hand and say so in the Inventory completeness notes. Silent omission is the failure
mode this dimension exists to catch.

## 2. Advisory queries

Declared hosts only, `GET` only, package coordinates only.

| Feed | Host | Use it for |
|---|---|---|
| OSV | `api.osv.dev` | Primary lookup across ecosystems, by name + version. |
| GitHub Security Advisories | `api.github.com` | Severity, affected ranges, fixed versions, exploited status. |
| NVD | `services.nvd.nist.gov` | CVE detail when OSV/GHSA disagree or lack a range. |

Record, for the whole run, one **query set timestamp** in UTC. Two feeds disagreeing is a finding
worth writing down, not something to average.

Never send: source, lockfile contents, ticket text, workspace paths, project or owner identity.

## 3. The composite digest

Your verdict binds to lockfiles *and* an advisory snapshot, so build the digest deterministically:

1. Sort the lockfile paths ordinal.
2. For each, append `<workspace-relative path>\n<sha256 of its bytes>\n`.
3. Append `advisory-query-set\n<query set timestamp, ISO-8601 UTC ending in Z>\n`.
4. Append, sorted ordinal, one line per advisory id you relied on: `<feed>:<advisory id>\n`.
5. `sha256` over the UTF-8 bytes of the whole thing.

Write those component lines verbatim into the audit report so the digest is reproducible. Cite the
result as the single `hash` evidence entry and cite **no `path` evidence** — see the SKILL section
that explains why.

## 4. Version-range arithmetic

This is where a wrong answer is silent, so be mechanical:

- Compare against the **resolved** version in the lockfile, never the range in the manifest.
- An advisory's affected range is half-open unless it says otherwise: `>= 1.2.0, < 1.4.3` excludes
  `1.4.3`. Read the operators; do not infer them from the fixed-version field.
- Pre-release and build metadata order below the release. If the resolved version carries either,
  say so — most advisories do not state their intent for pre-releases.
- Multiple resolutions of the same package name are common. Each resolution is its own row.
- A package with **no** fixed release is not "unaffected". It is a finding whose remediation is a
  mitigation or a removal.

## 5. The reachability call

For each advisory hit, answer one of exactly three:

| Answer | What it requires |
|---|---|
| **Reached** | Name the call path or configuration in this project that reaches the vulnerable function or feature. |
| **Not reached** | Name the vulnerable entry point and show this project never calls it — the import that is absent, the feature flag that is off, the platform that is not targeted. |
| **Undetermined** | Say why: generated code, dynamic dispatch, a plugin loader, a build-time-assembled call graph. |

**Undetermined is an honest outcome and it costs points in Exploitability triage — that is
intentional.** It must never be recorded as "not reached". If Critical hits are undetermined, the
verdict is at best `FIX`, because the ticket cannot be shown safe.

## 6. Remediation

Per finding: target version (or pin, or mitigation), the breaking changes it implies, and the
`programmer` ticket id that will do it. If the safe move is to remove a dependency, say what replaces
it. Never apply the change yourself — proposing is the boundary that keeps this lane a review.
