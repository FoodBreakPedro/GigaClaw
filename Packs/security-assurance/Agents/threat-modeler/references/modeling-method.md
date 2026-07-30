---
name: modeling-method
description: How to canonicalize the design text for hashing, which boundaries to look for, and the six threat classes with the questions that produce concrete threats.
---

# Modelling method

## Canonicalizing the design text (this is what you hash)

The verdict binds to a digest, so the digest has to be reproducible by anyone re-reading the ticket.
Build the canonical text exactly this way:

1. The ticket **description**, verbatim.
2. Then, in ticket order, every **comment that amends the design** — a comment that adds, removes or
   changes a requirement. Prefix each with `\n\n--- comment by <author> ---\n`.
3. Exclude: verdict comments, receipts, automation notices, and comments that only discuss process.
4. Normalize line endings to `\n` and strip trailing whitespace on each line, then a single trailing
   `\n`.
5. `sha256` over the UTF-8 bytes. That is `inputDigest` and the one `hash` evidence entry.

List, in the threat-model document, which comment ids you included. That is what makes the digest
reproducible without re-deriving your judgement.

## Boundary catalogue

Look for all of these, and say explicitly when one is absent:

| Boundary | The question that finds it |
|---|---|
| Process / machine | What runs in a different process or host, and what does it trust from the caller? |
| Network | Which hops leave the trusted network, and is each one authenticated in both directions? |
| Tenant / customer | Where is the tenant identity established, and is it re-checked at the data access, or only at the edge? |
| Privilege | Where does the code act with more authority than the caller — service accounts, background jobs, admin paths? |
| Third party | What data leaves to a provider, what does the provider send back, and what happens if it lies? |
| Human | Who can act out of band — operators, support tooling, anyone with database access — and is that act recorded? |
| Time | What is checked once and used later — a token minted at login, a permission cached, a signed URL? |

A design with one boundary is usually one that has not been described, not one that is simple.

## The six classes, with the question that makes the threat concrete

Ask each per boundary. Write the answer as *actor · capability · outcome*, or drop it.

| Class | Question |
|---|---|
| **Spoofing** | Who can claim to be someone else here, and what does the system use to tell the difference? |
| **Tampering** | What can be modified in flight or at rest by someone who should only read, and what would detect it? |
| **Repudiation** | If this went wrong, could you prove who did it a week later, from what record? |
| **Information disclosure** | What is readable by someone one step outside the intended audience — adjacent tenant, lower-privileged role, log reader, backup holder? |
| **Denial of service** | What is unbounded — allocation, iteration, external call, storage, cost — and who controls the bound? |
| **Elevation of privilege** | Where can a lower-privileged actor cause a higher-privileged one to act on their behalf? |

## What makes a mitigation testable

A mitigation earns points only if a reader can name the test. Write it in the shape:

> **Control:** *where it lives* — *what it asserts* — *what happens when it fails*.

Testable: "At the repository boundary, every query filters on the tenant claim from the validated
token; a query without a tenant filter throws at construction; covered by a test that asserts a
cross-tenant read returns not-found."

Not testable: "Validate input." · "Use secure defaults." · "The token is unguessable." · "We will be
careful in the implementation."

## Residual risk

For each mitigated threat, state what is still true afterwards, and for each accepted risk name the
condition under which it stops being acceptable. Two lines each. The point is that the owner accepts
a stated risk as a decision, rather than discovering it as an incident — so vagueness here is a
`residual-risk-unstated` finding against you, not a courtesy.
