---
name: review-checklist
description: The dimension-by-dimension audit checklist — entry points, sinks, and the questions that turn a suspicion into a machine-checkable finding.
---

# Audit checklist

Work it per changed file. A line you cannot answer with a concrete reference is either an
observation for `notes` or, when it blocks the judgement itself, `cannot-review-change` → `BLOCK`.

## 1. Untrusted input & injection sinks (25)

**Entry points to enumerate:** HTTP routes and handlers · CLI arguments · file and blob reads ·
queue and webhook payloads · environment variables set outside the process · database rows written
by another actor · anything deserialized.

**Sinks to search for by name, not by feel:**

| Class | What to grep for | Safe shape |
|---|---|---|
| SQL | string concatenation or interpolation into a command text | parameters/bound values only |
| Shell / process | `Process.Start`, `exec`, `system`, `subprocess`, backticks | argument array, no shell, allow-listed program |
| Path | `Path.Combine` with caller data, `../`, raw file names | canonicalize, then assert the result stays under a fixed root |
| Template / expression | server-side template render, expression evaluators | data passed as values, never spliced into the template |
| Deserialization | polymorphic or type-name-driven deserializers | fixed schema, no type resolution from payload |
| Redirect / SSRF | outbound URL built from caller data | allow-list of hosts, no redirect following into private ranges |

For each candidate write the chain: `entry point → transformation(s) → sink`. No chain, no veto item.

## 2. AuthN / AuthZ & data exposure (25)

- Does every **new or changed** route carry an identity requirement, and is it the *route* that
  carries it rather than a comment saying the gateway will?
- For every identifier arriving in a request — does the code check that the caller owns the object,
  or only that the object exists? The second is `broken-object-authorization`.
- Did any field newly reach a response, a projection, an export or a log line? Walk the model, not
  just the endpoint: adding a property to a shared DTO changes every response using it.
- Is any authorization decision made on the client side, or on a value the client can set
  (role in a token claim the service issues without verification, `isAdmin` in a body)?

## 3. Secrets & configuration handling (25)

- Any literal credential, key, token or connection string introduced by the change — including in
  tests, fixtures, sample config and comments. Cite it and let `secrets-reviewer`'s veto stand.
- Any credential written to a log, an error response, a metric label, an exception message, or a
  file outside the secret store.
- Configuration that weakens a boundary: permissive CORS (`*` with credentials), disabled TLS or
  certificate validation, cookies losing `HttpOnly`/`Secure`/`SameSite`, debug or developer
  endpoints reachable in a non-development configuration, authentication disabled behind a flag.
- Default values that are safe only when someone remembers to override them.

## 4. Failure, logging & resource limits (25)

- Does the error path **fail closed**? A `catch` that returns "allowed" or swallows an authorization
  failure is a Critical, not a code smell.
- Does an error response carry internals — stack traces, SQL text, file paths, executed command
  lines, library versions?
- Can a hostile input make the change loop, allocate or call out without bound: unbounded page size,
  no timeout on an outbound call, recursion driven by payload depth, a regex whose backtracking is
  input-driven, an upload with no size cap?
- Is anything that must be atomic actually atomic — check-then-act on a shared resource, a
  double-submit that creates two records, a token consumed twice?

## Writing it up

For each veto item:

1. **`code`** from the table in `SKILL.md`. Do not invent codes; a code nobody can grep for is not
   machine-checkable.
2. **`statement`** — one checkable fact, naming the symbol and the effect. Present tense, no hedging.
3. **`evidenceRefs`** — refs that appear in `evidence`. The validator rejects a dangling ref, so a
   veto item that cites nothing you read fails the contract rather than shipping.

Findings below High go in the category `notes` with the same concreteness. The report at
`doc/security/audits/ticket-{id}.md` carries the long form: the full chain per finding, what you
checked and found clean, and what you could not determine.
