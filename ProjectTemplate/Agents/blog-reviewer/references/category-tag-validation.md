# Category & Tag Validation Reference

Full detail for the **Category & Tag Validation Protocol** summarised in `SKILL.md`. Read this before
scoring any draft whose `categorySlug` is not an obvious match for an already-enabled category.

## 1. CMS Category Discovery

Discover enabled categories for the target venture using anonymous REST calls (using the CMS URL from
`.agents/BRAND.md`, defaulting to `https://zabalazone.com`):

1. `GET https://zabalazone.com/api/ventures?where[slug][equals]=<venture-slug>&limit=1` -> resolve venture ID
2. `GET https://zabalazone.com/api/categories?where[ventures.venture][in]=<venture-id>&limit=100` -> fetch `availableCategories` (`[{ name, slug }]`)

## 2. Category Resolution & Escalation

- **Valid Category**: Proposed frontmatter `categorySlug` matches an entry in `availableCategories`. Proceed with review.
- **Near-Miss / Typo / Alias**: If the proposed `categorySlug` is a minor typo or alias of an available category (e.g. `affiliate-reviews` -> `product-review`, `themed-cruises` -> `experience`), self-correct it to the valid matching category slug.
- **Invalid / New Category**: **New categories ALWAYS require human approval.** Do not auto-create new categories. Instead:
  1. Set verdict to `FIX` or `BLOCK`.
  2. Add machine-checkable veto item `unresolved-category-escalation`.
  3. Include actionable options for the owner in the review report:
     - **Option 1**: Create new category `<proposed-slug>` in CMS taxonomy.
     - **Option 2**: Map to an existing available category: `[list of availableCategories]`.
     - **Option 3**: Reject draft.
  4. Confirm the ticket description already holds this exact draft (`blog-writer` syncs it at step 7 of its own procedure; re-run `sync_draft_to_description.py` yourself only if the description is missing or stale) — the owner must be able to read the content they are deciding about, not just the error.
  5. Handoff ticket to `owner` in status `Blocked`. This is a genuine taxonomy decision — never auto-create the category and never guess a mapping.

## 3. Tag Validation

- **Tags**: Verify tags are formatted as kebab-case slugs. Unknown tags do **NOT** escalate — the CMS intake automatically creates new tags with `needsReview: true`. Only category additions require human escalation.
