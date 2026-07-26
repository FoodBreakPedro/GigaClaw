#!/usr/bin/env python3
"""
reddit_trends.py - API-free Reddit trend & pain point research helper for GigaClaw agents.
"""

import sys
import json
import urllib.request

NICHE_SUBREDDIT_MAP = {
    "solopreneur": ["entrepreneur", "freelance", "sidehustle", "smallbusiness"],
    "marketing": ["marketing", "socialmedia", "content_marketing", "entrepreneur"],
    "ai": ["artificial", "ChatGPT", "automation", "MachineLearning"],
    "fitness": ["fitness", "loseit", "personaltraining", "nutrition"],
    "finance": ["personalfinance", "financialindependence", "investing"],
    "career": ["careerguidance", "antiwork", "findapath", "Entrepreneur"]
}

def fetch_subreddit_top(subreddit):
    url = f"https://www.reddit.com/r/{subreddit}/top.json?t=month&limit=25"
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "GigaClaw-TrendResearch/1.0 (Mozilla/5.0)"}
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            if response.status == 200:
                data = json.loads(response.read().decode('utf-8'))
                posts = []
                for child in data.get("data", {}).get("children", []):
                    p = child.get("data", {})
                    posts.append({
                        "title": p.get("title", ""),
                        "score": p.get("score", 0),
                        "num_comments": p.get("num_comments", 0),
                        "permalink": f"https://reddit.com{p.get('permalink', '')}"
                    })
                return posts
            print(f"  [HTTP {response.status}] r/{subreddit} - treating as no data")
    except Exception as e:
        print(f"  [FETCH ERROR] r/{subreddit}: {e}")
    return []

def main():
    if len(sys.argv) < 2:
        print("Usage: python3 reddit_trends.py <niche-or-subreddit>")
        sys.exit(1)

    niche_input = sys.argv[1].lower().strip()
    if niche_input in NICHE_SUBREDDIT_MAP:
        subreddits = NICHE_SUBREDDIT_MAP[niche_input]
    else:
        subreddits = [niche_input]
        print(f"[NOTE] '{niche_input}' is not a mapped niche - treating it as a literal subreddit name.")

    print(f"=== REDDIT TREND REPORT: {niche_input.upper()} ===")
    all_posts = []
    seen = set()

    for sub in subreddits:
        posts = fetch_subreddit_top(sub)
        print(f"\nSubreddit r/{sub}: fetched {len(posts)} top posts this month")
        for p in posts:
            if p["permalink"] not in seen:
                seen.add(p["permalink"])
                all_posts.append(p)

    if not all_posts:
        # No fallback exists. Exit non-zero so callers can tell failure from an empty niche.
        print("\n[ERROR] No live Reddit data could be fetched (rate limited, blocked, or offline).")
        print("Do NOT fabricate trends from this run - report the data gap instead.")
        sys.exit(2)

    # Sort by comments (engagement resonance)
    sorted_by_comments = sorted(all_posts, key=lambda x: x["num_comments"], reverse=True)

    print("\n--- Top 5 Highest Engagement Discussions (by comment count) ---")
    for idx, p in enumerate(sorted_by_comments[:5], 1):
        print(f"{idx}. {p['title']} ({p['num_comments']} comments, {p['score']} upvotes)")
        print(f"   source: {p['permalink']}")

if __name__ == "__main__":
    main()
