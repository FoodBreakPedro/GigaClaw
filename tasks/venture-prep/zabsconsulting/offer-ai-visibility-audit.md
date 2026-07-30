# ZabsConsulting — Offer Brief: AI Visibility Audit (GEO/AEO)

> **Status: OFFER DEFINED — awaiting scope/spec session (Phase 3 gate).**
> Pedro greenlit this direction 2026-07-03 after market research. This activates the
> "consulting-services hat" in `brand.md`. Per the roadmap, a scope definition session
> precedes any build. Current active phase is 1.5 (Hyperlane content pilot); Pedro decides
> when ZC work is sequenced in.

## The offer (working hypothesis — refine in spec session)

Productized **AI visibility audit + fix packages** for local/professional service
businesses (plumbers, dentists, lawyers, contractors), answering one question:
**"Does ChatGPT / Perplexity / Google AI recommend your business — and if not, why?"**

- **Tier 1 — Audit report** (largely automatable): AI-citation check across ChatGPT,
  Perplexity, Gemini, AI Overviews; entity recognition; schema/E-E-A-T/technical gaps;
  prioritized fix list. Pays on delivery — no ranking promises, no SEO retainer.
- **Tier 2 — Fix package**: JSON-LD schema (LocalBusiness, HowTo, FAQ), author/E-E-A-T
  pages, llms.txt, GEO-optimized service pages, GBP description rewrite.
- **Differentiator — bilingual delivery (EN/ES)**: target Hispanic-owned service
  businesses; deliver audit + fixes in both languages. Near-zero direct competition found.

## Why this market (research 2026-07-03)

- 45% of consumers now use an AI assistant to find local services, up from 6% a year
  prior; ChatGPT recommends only ~1.2% of local businesses
  (https://studiomeyer.io/en/blog/local-ai-discovery-2026).
- 77–88% of businesses on Google page 1 are invisible in ChatGPT
  (https://omnieclipse.ai/blog/ai-search-visibility-report-2026).
- GEO services market: $1.48B (2026) → projected $17B by 2034, 45.5% CAGR
  (https://www.intelmarketresearch.com/generative-engine-optimization-services-market-36546).
- Agency pricing starts at $1,500–4,000/mo (https://www.icecubedigital.com/blog/generative-engine-optimization-cost-2026/);
  the sub-$500 productized tier is nearly empty. Fiverr's GEO/AEO category is new with
  only ~10 notable sellers (https://cybernaira.com/best-fiverr-geo-freelancers/).
- Typical gaps are mechanical/automatable: 78% of audited service businesses have stub
  GBP descriptions; 0/9 had author pages or HowTo schema
  (https://tristarmarketingsolutions.net/research/2026-ai-visibility-study/).
- US Hispanic market ($3.4T, 68M people) is described as linguistically underserved;
  Spanish CPCs run 30–50% cheaper
  (https://www.hispanicmarketadvisors.com/blog/top-hispanic-marketing-strategies-to-reach-us-latino-consumers-in-2026/).

**Rejected alternatives:** generic AI blog-writing gigs (Upwork writing down 32% YoY,
race to the bottom); blog-to-podcast repurposing (SaaS does it at ~$0.05/min, no margin).

**Window risk:** early but heating — agency-enablement guides and tools (Otterly, Insites)
already exist. Estimated 6–18 months before audits commoditize the way SEO audits did.
Speed matters more than polish.

## Tooling map (what already exists)

| Need | Existing asset |
|---|---|
| GEO/citation analysis | `claude-blog` skill: `blog-geo`, `blog-audit` |
| Schema generation | `blog-schema` (XSS-safe JSON-LD) |
| Real Google data (PSI, CrUX, GSC, NLP) | `blog-google` (11 scripts, 4 tiers) |
| Bilingual delivery | `blog-multilingual`, `blog-translate`, `blog-localize` |
| Report rendering (md → html → pdf) | `blog_render.py` |
| Order intake / delivery automation | n8n + approval queue (Hard Rule 2 preserved) |

## Open questions for the spec session

1. Jurisdiction/entity confirmation (brand.md legal items still UNVERIFIED) — must close
   before ZC signs clients or takes payment.
2. Pricing: audit $99–249? fix package $300–750? bundle?
3. Distribution channel: Fiverr GEO category vs. direct outreach (n8n-queued, Pedro-approved)
   vs. zabsconsulting.com landing page — or staged combination.
4. Audit pipeline definition: which checks are deterministic scripts vs. agent judgment;
   what the deliverable PDF looks like.
5. ZC-Lead agent spec (roadmap Phase 3 item) — same approval-gate pattern as Hyperlane-Lead.
6. HITL cost per order — must fit the 30–60 min/day budget alongside Hyperlane.
