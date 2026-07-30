---
name: severity
description: The severity ladder every security-assurance lane scores against, and the definition of "Critical" that triggers the hard veto.
---

# Severity

One ladder, shared by `security-auditor`, `threat-modeler`, `supply-chain-reviewer` and
`secrets-reviewer`, so a Critical means the same thing in four verdicts.

Severity is a function of **who can reach it** and **what they get**, never of how ugly the code is.

| Severity | Reachability | Effect | Verdict |
|---|---|---|---|
| **Critical** | Reachable by an unauthenticated caller, or by any authenticated user outside the intended role. | Arbitrary code or command execution, authentication bypass, credential or key disclosure, cross-tenant data read or write, or destruction of data. | `BLOCK` — always, no exceptions |
| **High** | Reachable by an authenticated user, possibly needing a non-default sequence. | Privilege escalation within a tenant, disclosure of another user's non-credential data, persistent stored injection. | `FIX`, or `BLOCK` on the second unfixed cycle |
| **Medium** | Reachable only with local access, a race, or a precondition the attacker cannot set. | Information leak of internals (stack traces, paths, versions), denial of service with a bounded blast radius, weakened-but-not-absent control. | `FIX` |
| **Low** | Defence-in-depth only; no path to an effect. | Missing hardening header, non-constant-time compare on a non-secret, redundant validation. | `notes` only — never a veto item |

## Rules the ladder is worth nothing without

1. **Unknown reachability is not Low.** If you cannot determine whether an entry point reaches the
   sink, that is `cannot-review-change` → `BLOCK`, not a Medium. The ladder grades what you proved,
   and "I could not tell" is a different answer from "it is fine".
2. **Compensating controls must be in the artifact you reviewed.** A WAF rule, a network policy or a
   convention someone described in a comment does not downgrade a Critical. If the mitigation is not
   in the bytes covered by `inputDigest`, it does not exist for this verdict.
3. **Severity is per finding, not per ticket.** Three Highs do not add up to a Critical, and one
   Critical is not diluted by twenty clean files.
4. **Every Critical and High becomes a `vetoItems` entry**, with `evidenceRefs` pointing at evidence
   you actually listed. Mediums live in category `notes`. This is what makes the ladder
   machine-checkable rather than a mood.
5. **A veto item forbids `SHIP`.** That is enforced by `verdict_contract.py`, not by your judgement,
   which is the whole point: the hard veto cannot be argued away in prose.
