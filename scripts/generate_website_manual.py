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
            output.append(
                f'<img src="{html.escape(src, quote=True)}" alt="{html.escape(image.group(1), quote=True)}" loading="lazy" />'
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
    return f'''<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="description" content="Kkindle 软件使用手册：{html.escape(title, quote=True)}。" />
    <meta name="theme-color" content="#fbfbfa" />
    <link rel="canonical" href="https://kkindle.stacker.beauty/manual/{('/' if slug == 'index' else slug + '.html')}" />
    <link rel="stylesheet" href="manual.css" />
    <title>{html.escape(title)} · Kkindle 使用手册</title>
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
        <p class="sidebar-note">按钮名称以你安装的 Kkindle 版本为准。</p>
      </aside>
      <main class="manual-content" id="content">
        <p class="eyebrow">KKINDLE · 使用手册</p>
        <article>{content}</article>
        <nav class="page-steps" aria-label="手册分页">{previous}{following}</nav>
        <footer class="manual-footer">
          <span>手册内容随软件功能更新。</span>
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
        destination.write_text(render_page(slug, title, source_file, index), encoding="utf-8", newline="\n")
    shutil.copytree(SOURCE / "images", OUTPUT / "images", dirs_exist_ok=True)
    print(f"Generated {len(PAGES)} manual pages in {OUTPUT}")


if __name__ == "__main__":
    main()
