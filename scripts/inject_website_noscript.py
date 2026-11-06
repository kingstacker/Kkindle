#!/usr/bin/env python3
"""Inject a no-JavaScript download fallback into the built index.html.

The homepage renders its release table with app.js. Crawlers and browsers
without JS then see nothing but marketing copy, so the artifact gets a static
<noscript> block generated from website/_release.json (the same-origin release
snapshot the page itself uses). Idempotent: any previously injected block is
replaced.
"""

from __future__ import annotations

import argparse
import html
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_RELEASE = ROOT / "website" / "_release.json"
START = "<!-- kkindle-noscript:start -->"
END = "<!-- kkindle-noscript:end -->"
BASE = "https://kkindle.stacker.beauty/"

LABELS = (
    ("win-x64-setup.exe", "Windows 安装包"),
    ("win-x64-portable.zip", "Windows 便携版"),
    ("linux-arm64", "Linux arm64 压缩包"),
    ("linux-x64", "Linux x64 压缩包"),
    ("amd64.deb", "Debian / Ubuntu amd64"),
    ("arm64.deb", "Debian / Ubuntu arm64"),
    ("osx-arm64", "macOS Apple 芯片"),
    ("osx-x64", "macOS Intel"),
    (".apk", "Android APK"),
    (".ipa", "iOS IPA"),
)


PACKAGE_EXT = (
    ".tar.gz",
    ".tgz",
    ".zip",
    ".exe",
    ".deb",
    ".rpm",
    ".dmg",
    ".appimage",
    ".apk",
    ".ipa",
)


def is_package(name: str) -> bool:
    """Only installer packages belong in the fallback list.

    The release snapshot also carries SHA256SUMS.txt and update-manifest.json;
    those are machine artefacts, not things a reader can install.
    """
    return name.lower().endswith(PACKAGE_EXT)


def label_for(name: str) -> str:
    for needle, label in LABELS:
        if needle in name:
            return label
    return name


def human_size(size: int) -> str:
    return f"{round(size / 1024 / 1024)} MB"


def build_block(release: dict) -> str:
    tag = release.get("tag_name") or ""
    published = (release.get("published_at") or "")[:10]
    assets = release.get("assets") or []
    rows = []
    for asset in assets:
        name = asset.get("name") or ""
        if not is_package(name):
            continue
        size = int(asset.get("size") or 0)
        url = f"{BASE}dl/{tag}/{name}"
        rows.append(
            f'        <li><a href="{html.escape(url)}">{html.escape(label_for(name))}</a>'
            f" —— {html.escape(name)}（{human_size(size)}）</li>"
        )
    headline = f"Kkindle {tag} 下载" + (f"（发布于 {published}）" if published else "")
    return "\n".join(
        [
            START,
            "    <noscript>",
            '      <div class="wrap">',
            f"        <h2>{html.escape(headline)}</h2>",
            "        <p>Kkindle 是一款面向 Windows、Linux、macOS、Android 和 iOS 的开源电子书与 Kindle 设备管理器："
            "书库管理、阅读与批注、Kindle 设备连接，本地优先、数据自持，GPL-3.0 开源，免费下载。</p>",
            "        <ul>",
            *rows,
            "        </ul>",
            '        <p>代码仓库：<a href="https://github.com/kingstacker/Kkindle">github.com/kingstacker/Kkindle</a>；'
            "本页「更新日志」一节有逐版更新说明。</p>",
            "      </div>",
            "    </noscript>",
            END,
        ]
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("index", type=Path, help="built index.html to patch")
    parser.add_argument("--release", type=Path, default=DEFAULT_RELEASE)
    args = parser.parse_args()

    text = args.index.read_text(encoding="utf-8")
    release = json.loads(args.release.read_text(encoding="utf-8"))
    block = build_block(release)

    existing = re.search(
        re.escape(START) + r".*?" + re.escape(END), text, flags=re.S
    )
    if existing:
        text = text[: existing.start()] + block + text[existing.end():]
    else:
        marker = re.search(r"<body[^>]*>", text)
        if not marker:
            raise SystemExit("no <body> tag in index.html")
        text = text[: marker.end()] + "\n    " + block + text[marker.end():]

    args.index.write_text(text, encoding="utf-8")
    print(f"noscript: injected {block.count('<li>')} download links into {args.index}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
