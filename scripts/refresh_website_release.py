#!/usr/bin/env python3
"""Refresh website/_release.json from the latest GitHub release.

The website reads this same-origin snapshot first, so the download section works
without hitting GitHub's unauthenticated API rate limit (60 requests/hour/IP).
Set GITHUB_TOKEN to raise the limit when running in CI.
"""

from __future__ import annotations

import argparse
import json
import os
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = ROOT / "website" / "_release.json"


def fetch_release(repository: str, timeout: float) -> dict:
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}/releases/latest",
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "kkindle-website-release-refresh",
            **(
                {"Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}"}
                if os.environ.get("GITHUB_TOKEN")
                else {}
            ),
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="kingstacker/Kkindle")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--input",
        type=Path,
        help="Read a saved GitHub release API payload instead of calling the API",
    )
    parser.add_argument("--timeout", type=float, default=20.0)
    parser.add_argument(
        "--allow-stale",
        action="store_true",
        help="Keep the existing snapshot if GitHub cannot be reached",
    )
    args = parser.parse_args()

    if args.input:
        release = json.loads(args.input.expanduser().read_text(encoding="utf-8"))
    else:
        try:
            release = fetch_release(args.repo, args.timeout)
        except Exception as error:  # noqa: BLE001 - any network/HTTP failure
            output = args.output.expanduser().resolve()
            if not args.allow_stale or not output.exists():
                raise
            print(f"warning: could not refresh {output} ({error}); keeping the existing snapshot")
            return
    payload = {
        "tag_name": release.get("tag_name", ""),
        "name": release.get("name", ""),
        "published_at": release.get("published_at"),
        "html_url": release.get("html_url"),
        "assets": [
            {
                "name": asset.get("name"),
                "size": asset.get("size"),
                "browser_download_url": asset.get("browser_download_url"),
            }
            for asset in release.get("assets", [])
            if asset.get("name")
        ],
    }

    output = args.output.expanduser().resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {payload['tag_name']} ({len(payload['assets'])} assets) to {output}")


if __name__ == "__main__":
    main()
