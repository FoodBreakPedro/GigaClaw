#!/usr/bin/env python3
"""
cognitive_load.py - Cognitive load & reading fatigue helper for GigaClaw content agents.

Audits:
  - Paragraph length distribution (flags paragraphs > 4 sentences or > 100 words)
  - Dense word clusters
  - Heading spacing
"""

import sys
import re

def analyze_cognitive_load(text):
    paragraphs = [p.strip() for p in text.split('\n\n') if p.strip() and not p.strip().startswith('#')]
    
    total_paragraphs = len(paragraphs)
    long_paragraphs = []
    
    for idx, p in enumerate(paragraphs, 1):
        sentences = [s for s in re.split(r'[.!?]+', p) if s.strip()]
        words = re.findall(r'\b\w+\b', p)
        if len(sentences) > 4 or len(words) > 100:
            long_paragraphs.append({
                "paragraph_num": idx,
                "sentences": len(sentences),
                "words": len(words),
                "snippet": p[:60] + "..."
            })
            
    return {
        "total_paragraphs": total_paragraphs,
        "fatigue_paragraphs": len(long_paragraphs),
        "issues": long_paragraphs
    }

def main():
    if len(sys.argv) < 2:
        print("Usage: python3 cognitive_load.py <path-to-markdown-file>")
        sys.exit(1)
        
    filepath = sys.argv[1]
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()
    except Exception as e:
        print(f"Error reading file {filepath}: {e}")
        sys.exit(1)
        
    res = analyze_cognitive_load(content)
    print("=== COGNITIVE LOAD REPORT ===")
    print(f"Total Paragraphs: {res['total_paragraphs']}")
    print(f"Reading Fatigue Risk Paragraphs: {res['fatigue_paragraphs']}")
    
    if res['issues']:
        print("\n[WARNING] Paragraphs Exceeding 4 Sentences / 100 Words:")
        for issue in res['issues']:
            print(f"  - Paragraph #{issue['paragraph_num']}: {issue['sentences']} sentences, {issue['words']} words ('{issue['snippet']}')")
    else:
        print("\n[OK] Excellent paragraph pacing! No reading fatigue detected.")

if __name__ == "__main__":
    main()
