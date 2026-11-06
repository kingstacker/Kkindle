#!/usr/bin/env python3
"""Generate the static Chinese user manual from docs/manual/zh-CN."""

from __future__ import annotations

import html
import re
import shutil
from pathlib import Path
from urllib.parse import quote


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs" / "manual" / "zh-CN"
OUTPUT = ROOT / "website" / "manual"

PAGES = [
    ("index", "开始使用", "index.md"),
    ("install", "安装与初次启动", "install.md"),
    ("library", "书库与导入", "library.md"),
    ("reader", "阅读与批注", "reader.md"),
    ("kindle", "Kindle 与传书", "kindle.md"),
    ("ai", "AI 阅读助手", "ai.md"),
    ("data", "备份与同步", "data.md"),
    ("settings", "设置与词典", "settings.md"),
    ("help", "常见问题", "help.md"),
]
SOURCE_TO_SLUG = {source: slug for slug, _, source in PAGES}
INLINE_TOKEN = re.compile(r"`[^`]+`|\*\*.+?\*\*|!\[[^\]]*\]\([^)]+\)|\[[^\]]+\]\([^)]+\)")


def image_dimensions(path: Path) -> tuple[int, int] | None:
    """Read the intrinsic size from a local PNG, JPEG or WebP file."""
    try:
        data = path.read_bytes()
    except OSError:
        return None

    if data.startswith(b"\x89PNG\r\n\x1a\n") and len(data) >= 24:
        return int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big")

    if data[:2] == b"\xff\xd8":
        offset = 2
        while offset + 9 < len(data):
            if data[offset] != 0xFF:
                offset += 1
                continue
            marker = data[offset + 1]
            if marker in {0xD8, 0xD9}:
                offset += 2
                continue
            length = int.from_bytes(data[offset + 2 : offset + 4], "big")
            if 0xC0 <= marker <= 0xCF and marker not in {0xC4, 0xC8, 0xCC}:
                height = int.from_bytes(data[offset + 5 : offset + 7], "big")
                width = int.from_bytes(data[offset + 7 : offset + 9], "big")
                return width, height
            offset += 2 + length
        return None

    if data[:4] == b"RIFF" and data[8:12] == b"WEBP" and len(data) >= 30:
        chunk = data[12:16]
        if chunk == b"VP8X":
            width = 1 + int.from_bytes(data[24:27], "little")
            height = 1 + int.from_bytes(data[27:30], "little")
            return width, height
        if chunk == b"VP8L":
            bits = data[21:25]
            width = 1 + (((bits[1] & 0x3F) << 8) | bits[0])
            height = 1 + (((bits[3] & 0x0F) << 10) | (bits[2] << 2) | ((bits[1] & 0xC0) >> 6))
            return width, height
        if chunk == b"VP8 ":
            width = int.from_bytes(data[26:28], "little") & 0x3FFF
            height = int.from_bytes(data[28:30], "little") & 0x3FFF
            return width, height

    return None


def local_image_dimensions(src: str) -> tuple[int, int] | None:
    if src.startswith(("http://", "https://", "//", "data:")):
        return None
    target = (SOURCE / src.split("#", 1)[0]).resolve()
    source_root = SOURCE.resolve()
    if source_root not in target.parents:
        return None
    return image_dimensions(target)


def rewrite_href(href: str) -> str:
    if href.startswith(("http://", "https://", "mailto:", "#")):
        return href
    target, separator, fragment = href.partition("#")
    if target.endswith(".md"):
        slug = SOURCE_TO_SLUG.get(Path(target).name)
        if slug:
            target = f"{slug}.html"
    return target + (separator + quote(fragment, safe="-._~") if separator else "")


def render_inline(value: str) -> str:
    output: list[str] = []
    cursor = 0
    for match in INLINE_TOKEN.finditer(value):
        output.append(html.escape(value[cursor : match.start()]))
        token = match.group(0)
        if token.startswith("` ") or token.startswith("`"):
            output.append(f"<code>{html.escape(token[1:-1])}</code>")
        elif token.startswith("**"):
            output.append(f"<strong>{html.escape(token[2:-2])}</strong>")
        elif token.startswith("!["):
            image = re.fullmatch(r"!\[([^\]]*)\]\(([^)]+)\)", token)
            assert image is not None
            src = rewrite_href(image.group(2))
            size = local_image_dimensions(src)
            dimensions = f' width="{size[0]}" height="{size[1]}"' if size else ""
            output.append(
                f'<img src="{html.escape(src, quote=True)}" alt="{html.escape(image.group(1), quote=True)}"'
                f'{dimensions} loading="lazy" decoding="async" />'
            )
        else:
            link = re.fullmatch(r"\[([^\]]+)\]\(([^)]+)\)", token)
            assert link is not None
            label, href = link.groups()
            href = rewrite_href(href)
            external = href.startswith(("http://", "https://", "mailto:"))
            attributes = ' target="_blank" rel="noreferrer"' if external else ""
            output.append(f'<a href="{html.escape(href, quote=True)}"{attributes}>{html.escape(label)}</a>')
        cursor = match.end()
    output.append(html.escape(value[cursor:]))
    return "".join(output)


def render_markdown(source: str) -> str:
    lines = source.splitlines()
    output: list[str] = []
    paragraph: list[str] = []
    list_kind: str | None = None
    quote: list[str] = []

    def flush_paragraph() -> None:
        if paragraph:
            output.append(f"<p>{render_inline(' '.join(line.strip() for line in paragraph))}</p>")
            paragraph.clear()

    def close_list() -> None:
        nonlocal list_kind
        if list_kind:
            output.append(f"</{list_kind}>")
            list_kind = None

    def flush_quote() -> None:
        if quote:
            output.append(f"<blockquote><p>{render_inline(' '.join(quote))}</p></blockquote>")
            quote.clear()

    for raw_line in lines:
        line = raw_line.rstrip()
        heading = re.match(r"^(#{1,6})\s+(.+?)\s*#*\s*$", line)
        unordered = re.match(r"^\s*[-*]\s+(.+)$", line)
        ordered = re.match(r"^\s*\d+[.)]\s+(.+)$", line)
        if not line.strip():
            flush_paragraph()
            close_list()
            flush_quote()
        elif heading:
            flush_paragraph()
            close_list()
            flush_quote()
            level = len(heading.group(1))
            output.append(f"<h{level}>{render_inline(heading.group(2))}</h{level}>")
        elif line.strip() in {"---", "***"}:
            flush_paragraph()
            close_list()
            flush_quote()
            output.append("<hr />")
        elif line.lstrip().startswith("> "):
            flush_paragraph()
            close_list()
            quote.append(line.lstrip()[2:].strip())
        elif unordered or ordered:
            flush_paragraph()
            flush_quote()
            kind = "ul" if unordered else "ol"
            if list_kind != kind:
                close_list()
                output.append(f"<{kind}>")
                list_kind = kind
            item = unordered.group(1) if unordered else ordered.group(1)
            output.append(f"<li>{render_inline(item)}</li>")
        else:
            close_list()
            flush_quote()
            paragraph.append(line)

    flush_paragraph()
    close_list()
    flush_quote()
    return "\n".join(output)


def render_page(slug: str, title: str, source_file: str, index: int) -> str:
    page_source = (SOURCE / source_file).read_text(encoding="utf-8")
    content = render_markdown(page_source)
    nav_links = []
    for page_slug, page_title, _ in PAGES:
        current = ' aria-current="page"' if page_slug == slug else ""
        nav_links.append(f'<a href="{page_slug}.html"{current}>{html.escape(page_title)}</a>')
    nav = "\n".join(nav_links)
    previous = (
        f'<a class="page-step" rel="prev" href="{PAGES[index - 1][0]}.html">← {html.escape(PAGES[index - 1][1])}</a>'
        if index > 0
        else ""
    )
    following = (
        f'<a class="page-step next" rel="next" href="{PAGES[index + 1][0]}.html">{html.escape(PAGES[index + 1][1])} →</a>'
        if index + 1 < len(PAGES)
        else ""
    )
    canonical = f"https://kkindle.stacker.beauty/manual/{'' if slug == 'index' else slug + '.html'}"
    description = f"Kkindle 软件使用手册：{title}。"
    page_title = f"{title} · Kkindle 使用手册"
    return f'''<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="description" content="{html.escape(description, quote=True)}" />
    <meta name="color-scheme" content="light" />
    <meta name="theme-color" content="#ffffff" />
    <link rel="canonical" href="{canonical}" />
    <meta property="og:type" content="article" />
    <meta property="og:site_name" content="Kkindle" />
    <meta property="og:title" content="{html.escape(page_title, quote=True)}" />
    <meta property="og:description" content="{html.escape(description, quote=True)}" />
    <meta property="og:url" content="{canonical}" />
    <meta property="og:image" content="https://kkindle.stacker.beauty/assets/social-card.jpg" />
    <meta property="og:image:width" content="1200" />
    <meta property="og:image:height" content="630" />
    <meta name="twitter:card" content="summary_large_image" />
    <meta name="twitter:title" content="{html.escape(page_title, quote=True)}" />
    <meta name="twitter:description" content="{html.escape(description, quote=True)}" />
    <meta name="twitter:image" content="https://kkindle.stacker.beauty/assets/social-card.jpg" />
    <link rel="stylesheet" href="manual.css?v=20261105a" />
    <title>{html.escape(page_title)}</title>
  </head>
  <body>
    <a class="skip-link" href="#content">跳到正文</a>
    <header class="manual-header">
      <a class="brand" href="index.html"><span class="brand-mark">K</span><span>Kkindle 使用手册</span></a>
      <a class="home-link" href="../index.html">返回官网 ↗</a>
    </header>
    <div class="manual-layout">
      <aside class="manual-sidebar" aria-label="手册目录">
        <p class="sidebar-kicker">USER GUIDE · 简体中文</p>
        <h2>按任务查阅</h2>
        <nav>{nav}</nav>
      </aside>
      <main class="manual-content" id="content">
        <p class="eyebrow">KKINDLE · 使用手册</p>
        <article>{content}</article>
        <nav class="page-steps" aria-label="手册分页">{previous}{following}</nav>
        <footer class="manual-footer">
          <span>手册内容随软件功能更新。</span>
          <a href="/">返回 Kkindle 官网首页</a>
          <a href="https://github.com/kingstacker/Kkindle/tree/master/docs/manual/zh-CN" target="_blank" rel="noreferrer">查看 Markdown 源文件 ↗</a>
        </footer>
      </main>
    </div>
  </body>
</html>
'''


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for index, (slug, title, source_file) in enumerate(PAGES):
        destination = OUTPUT / f"{slug}.html"
        with destination.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(render_page(slug, title, source_file, index))
    shutil.copytree(SOURCE / "images", OUTPUT / "images", dirs_exist_ok=True)
    print(f"Generated {len(PAGES)} manual pages in {OUTPUT}")


if __name__ == "__main__":
    main()
