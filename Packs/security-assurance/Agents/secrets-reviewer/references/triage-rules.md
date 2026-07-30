---
name: triage-rules
description: How to classify a detector hit as live credential, test fixture or placeholder — without ever using the value — and what rotation guidance each class requires.
---

# Triage rules

The detector tells you *where*. These rules tell you *what it is*. Classify from the surrounding
lines and the file's role, never by trying the value.

## The three classes

### Live credential → `live-credential-in-diff` (BLOCK)

Any of these is enough:

- The value is read at runtime — assigned to a config object, a client constructor, a header, a
  connection string — on a path that is not test-only.
- It sits in a file that ships or deploys: application config, container image, deployment manifest,
  CI secret written inline instead of referenced.
- It has a real prefix *and* real entropy: a provider prefix (`sk-ant-`, `ghp_`, `AKIA`, `xox`) with
  a full-length random remainder, not a short or repeating tail.
- Private key material of any kind — always live, always `private-key-material`, regardless of where
  it sits or what a comment claims.

When in doubt between live and fixture, it is **live**. The asymmetry is deliberate: a false BLOCK
costs one ticket, a false SHIP costs a rotation you did not know you needed.

### Test fixture → not a veto item, note it

All of these must hold:

- The file's role is unambiguously test data — a fixture directory, a test project, a mock server
  configuration used only under test.
- The value is a documented dummy (for example the vendor's own published example key) or is
  visibly synthetic: repeating characters, a `0000…`/`AAAA…` tail, a length that does not match the
  real credential format.
- Nothing outside tests reads it, and no deployment artifact includes the file.

Record it in the Candidate triage notes with the reason. A fixture that a deployment artifact
happens to include is not a fixture.

### Placeholder → `secret-in-tracked-config` (FIX) or a note

A named blank — `<your-api-key>`, `CHANGEME`, an empty value with a comment — is not a leak. But if
the *sample* file has drifted into a real value, or a tracked config file carries a credential-shaped
string, that is `secret-in-tracked-config`: a `FIX`, because the next person to copy the file inherits
it.

## What you may never do

- **Never write the value.** Not into the report, not into a comment, not into a shell command, not
  into your memory. `<path>:<line>` plus the detector's truncated excerpt is the whole permitted
  citation, and it is sufficient for a human to find it.
- **Never verify by use.** Calling the service to see whether the key works *is* the exposure, and it
  may appear in an audit log you do not control.
- **Never git-log a value out of history and paste it forward.** If history matters, say which commit
  range needs scrubbing and let a human with the credentials do it.
- **Never guess.** A candidate you cannot place is `unclassifiable-candidate` → `BLOCK`. This is the
  rule that makes it safe to run this lane on a cheap model tier: the model classifies what the
  evidence supports and escalates everything else.

## Rotation guidance (dimension 3)

For every live credential, state three things, in this order:

1. **What it grants** — the account, the scope, and the blast radius if used by someone else.
2. **Where it is valid** — which environments and which services accept it right now.
3. **The rotation step** — revoke first, then issue, then update the secret store, then redeploy.
   Name the order explicitly: issuing a new key without revoking the old one leaves the exposure open.

Add the standing line every escalation carries: **removing the line from the file does not un-leak
the value.** It was on disk, it is in a diff, it may be in a log or a build artifact. Treat it as
compromised from the moment it was written.

## Coverage beyond the diff (dimension 4)

Run the detector over these too whenever the change touches them:

`.env`, `.env.*`, `appsettings*.json`, CI and pipeline definitions, `Dockerfile` and compose files,
Kubernetes and deployment manifests, `*.pem`/`*.key`/`*.pfx`, editor and tool config committed to the
repository, and any sample or template file the project tells people to copy.

If the change *moves* a credential — out of source into config, or between files — check both ends.
A secret that left the source file but arrived in a tracked config file has not been removed.
