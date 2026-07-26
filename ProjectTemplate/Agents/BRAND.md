# Brand Identity & Content Guidelines

This file defines the shared brand context, target audience, value proposition, and publishing principles for all content agents.

## Core Brand Identity

> Fill in the placeholders below when setting up your project — content agents read this file verbatim.

- **Brand Name**: `<Your Brand>`
- **Mission**: Produce high-value, evidence-backed, search-optimized, and AI-citable content that answers reader queries cleanly and accurately.
- **Target Audience**: Technical builders, software engineers, product managers, and digital creators.
- **Canonical domain**: `<https://example.com>` (blog-writer uses this for canonical URLs; flag if unset)
- **Target locales**: `<none>` (e.g. `es, de, fr` — blog-translator blocks when no locales are specified here or on the ticket)
- **Default niche**: `<your niche>` (e.g. `ai`, `marketing` — trend-researcher's fallback when a ticket names none; see `NICHE_SUBREDDIT_MAP` in `.agents/scripts/reddit_trends.py` for mapped values)

## Key Content Pillars

1. **Practical Utility**: Every article must solve a clear task or explain a concept with actionable steps.
2. **Evidence-Backed Claims**: Support assertions with benchmark data, official documentation references, or explicit code examples.
3. **Structured Clarity**: Use clean heading hierarchy (`#`, `##`, `###`), contrast tables, ordered lists, and concise TL;DR summaries.
4. **AI Citation Readiness**: Self-contained sections with clear entity definitions suitable for AI search engines (ChatGPT, Perplexity, Claude, Google AI Overviews).

## Publishing Rules

- Always include complete frontmatter metadata (`title`, `description`, `date`, `author`, `tags`).
- Ensure all inline links are descriptive and point to authoritative sources.
- Include schema JSON-LD markup (`BlogPosting`, `FAQPage`, etc.) for every article.
