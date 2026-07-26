#!/usr/bin/env python3
"""
ai_citation_score.py - Heuristic AI Citation Readiness & GEO (Generative Engine Optimization) score helper.

Evaluates content structure for LLM retrieval and citation:
  - Self-contained definitions
  - Comparison tables
  - Bulleted summary lists
  - Schema JSON-LD presence
  - Direct Q&A FAQ headers
"""

import sys
import re

def evaluate_geo_citability(text):
    score = 0
    breakdown = {}
    
    # 1. Heading Hierarchy & Clarity (20 pts)
    headers = re.findall(r'^#{1,4}\s+(.+)$', text, re.MULTILINE)
    if len(headers) >= 3:
        score += 15
        breakdown['headers'] = "Good header structure (15/20)"
        # Extra points if headers contain query phrases (How, What, Why, Best)
        if any(re.search(r'\b(how|what|why|best|vs|guide)\b', h, re.IGNORECASE) for h in headers):
            score += 5
            breakdown['headers'] += " + Query-matched headings (+5)"
    else:
        breakdown['headers'] = "Weak header hierarchy (0/20)"
        
    # 2. Extractable Structural Data: Tables & Lists (25 pts)
    # Table = a markdown separator row (handles both `|---|---|` and `| --- | --- |`).
    has_table = bool(re.search(r'^(?=.*\|)\s*\|?[\s:|-]*-{3,}[\s:|-]*\|?\s*$', text, re.MULTILINE))
    # List = dash/star/plus bullets or numbered items.
    has_list = bool(re.search(r'^\s*(?:[-*+]|\d{1,3}\.)\s+', text, re.MULTILINE))
    
    table_score = 15 if has_table else 0
    list_score = 10 if has_list else 0
    score += (table_score + list_score)
    breakdown['structural_data'] = f"Tables ({table_score}/15), Lists ({list_score}/10)"
    
    # 3. Schema JSON-LD (20 pts) — spacing- and protocol-tolerant.
    has_schema = ('<script type="application/ld+json">' in text
                  or bool(re.search(r'"@context"\s*:\s*"https?://schema\.org"', text)))
    schema_score = 20 if has_schema else 0
    score += schema_score
    breakdown['schema'] = f"JSON-LD Schema ({schema_score}/20)"
    
    # 4. Definition Clarity & TL;DR Box (20 pts)
    has_tldr = bool(re.search(r'\b(tldr|key takeaways|summary|quick overview)\b', text, re.IGNORECASE))
    tldr_score = 10 if has_tldr else 0
    
    # Check for direct definition patterns ("X is a ...", "X refers to ...")
    has_definition = bool(re.search(r'\b(is a|refers to|defined as|consists of)\b', text, re.IGNORECASE))
    def_score = 10 if has_definition else 0
    
    score += (tldr_score + def_score)
    breakdown['definition_tldr'] = f"TL;DR Callout ({tldr_score}/10), Direct Definitions ({def_score}/10)"
    
    # 5. Citations & Code Evidence (15 pts)
    has_links = bool(re.search(r'\[.+\]\(https?://.+\)', text))
    has_code = '```' in text
    link_score = 10 if has_links else 0
    code_score = 5 if has_code else 0
    score += (link_score + code_score)
    breakdown['evidence'] = f"External Links ({link_score}/10), Code Blocks ({code_score}/5)"
    
    return {
        "score": min(100, score),
        "breakdown": breakdown
    }

def main():
    if len(sys.argv) < 2:
        print("Usage: python3 ai_citation_score.py <path-to-markdown-file>")
        sys.exit(1)
        
    filepath = sys.argv[1]
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()
    except Exception as e:
        print(f"Error reading file {filepath}: {e}")
        sys.exit(1)
        
    res = evaluate_geo_citability(content)
    print("=== AI CITATION & GEO READINESS REPORT ===")
    print(f"Overall GEO Score: {res['score']} / 100")
    print("\nBreakdown:")
    for k, v in res['breakdown'].items():
        print(f"  - {k.capitalize()}: {v}")

if __name__ == "__main__":
    main()
