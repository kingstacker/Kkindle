#!/usr/bin/env python3
"""Refresh <lastmod> in website/sitemap.xml from the site files' mtimes.

Google ignores a sitemap whose lastmod never moves: the file looks unchanged,
so the crawl of that URL is deprioritised. Run this after the manual and
release snapshots have been regenerated, before the artifact is copied.
"""

from __future__ import annotations

import re
import sys
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WEBSITE = ROOT / "website"
SITEMAP = WEBSITE / "sitemap.xml"
BASE = "https://kkindle.stacker.beauty/"

LOC_RE = re.compile(r"(<loc>)(?P<url>[^<]+)(</loc>)")
LASTMOD_RE = re.compile(r"\s*<lastmod>[^<]*</lastmod>")


def url_to_file(url: str) -> Path | None:
    if not url.startswith(BASE):
        return None
    relative = url[len(BASE):].split("#")[0].split("?")[0]
    if relative in {"", "index.html"}:
        return WEBSITE / "index.html"
    if relative.endswith("/"):
        relative += "index.html"
    return WEBSITE / relative


def main() -> int:
    if not SITEMAP.exists():
        print(f"missing sitemap: {SITEMAP}", file=sys.stderr)
        return 1

    text = SITEMAP.read_text(encoding="utf-8")
    updated = 0
    missing: list[str] = []

    def stamp(match: re.Match[str]) -> str:
        nonlocal updated
        block = match.group(0)
        loc = LOC_RE.search(block)
        if not loc:
            return block
        url = loc.group("url")
        path = url_to_file(url)
        if path is None or not path.exists():
            missing.append(url)
            return block
        date = datetime.fromtimestamp(path.stat().st_mtime, tz=timezone.utc).date().isoformat()
        # Drop every existing lastmod first so the element never ends up with two.
        block = re.sub(r"\s*<lastmod>[^<]*</lastmod>", "", block)
        block = block.replace(loc.group(0), f"{loc.group(0)}\n    <lastmod>{date}</lastmod>", 1)
        updated += 1
        return block

    text = re.sub(r"<url>.*?</url>", stamp, text, flags=re.S)
    SITEMAP.write_text(text, encoding="utf-8")

    print(f"sitemap: refreshed {updated} lastmod entries")
    for url in missing:
        print(f"  warning: no local file for {url}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
