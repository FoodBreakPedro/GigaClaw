#!/usr/bin/env python3
"""
lint_prose.py - Deterministic prose linting helper for GigaClaw content agents.

Audits:
  - Banned AI clichés and fluff phrases
  - Passive voice indicators
  - Sentence length distribution
  - Flesch Reading Ease score estimation
"""

from __future__ import annotations

import math
import re
import sys
from pathlib import Path
from typing import Any

# These scripts print non-ASCII (arrows, middots, dashes, accents) and the host reads their
# stdout as UTF-8. Python on Windows still defaults stdout to the ANSI code page (cp1252), where
# those characters raise UnicodeEncodeError *after* the work succeeded -- turning a clean pass
# into a crash. Pin the stream instead of degrading the output to ASCII.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

BANNED_CLICHES = [
    r"in today's digital landscape",
    r"in the ever-evolving world of",
    r"it's important to note",
    r"at its core",
    r"deep dive",
    r"dive into",
    r"game-changer",
    r"revolutionize",
    r"paradigm shift",
    r"seamlessly",
    r"empower",
    r"harness the power of",
    r"\btapestry\b",
    r"\bleverage\b",
    r"seamless integration",
]

PASSIVE_PATTERNS = [
    r"\bis being\b",
    r"\bwas being\b",
    r"\bhas been\b",
    r"\bhave been\b",
    r"\bwill be\b",
    r"\bis [a-z]+ed by\b",
    r"\bwas [a-z]+ed by\b",
]

def count_syllables(word: str) -> int:
    word = word.lower()
    if len(word) <= 3:
        return 1
    word = re.sub(r'(?:[^laeiouy]|ed|es|e)$', '', word)
    word = re.sub(r'^y', '', word)
    matches = re.findall(r'[aeiouy]{1,2}', word)
    return max(1, len(matches))


def strip_non_prose(text: str) -> str:
    """Drop frontmatter, fenced code, embedded scripts, and table rows so
    readability metrics reflect the prose only."""
    text = re.sub(r'\A---\n.*?\n---\n', '', text, flags=re.DOTALL)
    text = re.sub(r'```.*?```', '', text, flags=re.DOTALL)
    text = re.sub(r'<script\b.*?</script>', '', text, flags=re.DOTALL | re.IGNORECASE)
    lines = [ln for ln in text.splitlines() if not ln.lstrip().startswith('|')]
    return '\n'.join(lines)


def analyze_prose(text: str) -> dict[str, Any]:
    text = strip_non_prose(text)
    sentences = [s.strip() for s in re.split(r'[.!?]+', text) if s.strip()]
    words = re.findall(r'\b[A-Za-z]+\b', text)
    
    word_count = len(words)
    sentence_count = len(sentences)
    if word_count == 0 or sentence_count == 0:
        return {"error": "Empty text"}
    
    syllable_count = sum(count_syllables(w) for w in words)
    
    # Flesch Reading Ease: 206.835 - 1.015*(words/sentences) - 84.6*(syllables/words)
    asl = word_count / sentence_count
    asw = syllable_count / word_count
    flesch_score = round(206.835 - (1.015 * asl) - (84.6 * asw), 1)
    
    found_cliches = []
    text_lower = text.lower()
    for pattern in BANNED_CLICHES:
        matches = re.findall(pattern, text_lower)
        if matches:
            found_cliches.append((pattern, len(matches)))
            
    passive_matches = 0
    for pattern in PASSIVE_PATTERNS:
        passive_matches += len(re.findall(pattern, text_lower))
        
    sentence_lengths = [len(re.findall(r'\b\w+\b', s)) for s in sentences]
    avg_len = sum(sentence_lengths) / len(sentence_lengths)
    variance = sum((x - avg_len) ** 2 for x in sentence_lengths) / len(sentence_lengths)
    std_dev = math.sqrt(variance)
    burstiness = round(std_dev / avg_len, 2) if avg_len > 0 else 0
    
    return {
        "word_count": word_count,
        "sentence_count": sentence_count,
        "flesch_reading_ease": flesch_score,
        "avg_sentence_length": round(avg_len, 1),
        "burstiness": burstiness,
        "cliches_found": found_cliches,
        "passive_indicators": passive_matches
    }


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python3 lint_prose.py <path-to-markdown-file>")
        sys.exit(1)

    filepath = sys.argv[1]
    try:
        content = Path(filepath).read_text(encoding='utf-8')
    except Exception as e:
        print(f"Error reading file {filepath}: {e}")
        sys.exit(1)

    # Curly quotes defeat apostrophe-based cliché patterns — normalize first.
    content = content.replace('’', "'").replace('‘', "'")

    res = analyze_prose(content)
    if 'error' in res:
        print(f"=== PROSE LINT REPORT ===\n{res['error']} — nothing to analyze.")
        sys.exit(0)
    print("=== PROSE LINT REPORT ===")
    print(f"Word Count: {res['word_count']}")
    print(f"Sentence Count: {res['sentence_count']}")
    print(f"Flesch Reading Ease: {res['flesch_reading_ease']} (Target: 60-70)")
    print(f"Avg Sentence Length: {res['avg_sentence_length']} words")
    print(f"Burstiness (Variation): {res['burstiness']}")
    print(f"Passive Voice Count: {res['passive_indicators']}")
    
    if res['cliches_found']:
        print("\n[WARNING] Banned AI Clichés Found:")
        for phrase, count in res['cliches_found']:
            print(f"  - '{phrase}': {count} instance(s)")
    else:
        print("\n[OK] Zero Banned AI Clichés Found!")


if __name__ == "__main__":
    main()
