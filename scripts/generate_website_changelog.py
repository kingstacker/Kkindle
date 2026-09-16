#!/usr/bin/env python3
"""Build the website's recent changelog entries from CHANGELOG.md."""

from __future__ import annotations

import argparse
import html
import re
import shutil
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WEBSITE_SOURCE = ROOT / "website"
CHANGELOG_SOURCE = ROOT / "CHANGELOG.md"
DEFAULT_OUTPUT = ROOT / "artifacts" / "website"
START_MARKER = "<!-- changelog-generated:start -->"
END_MARKER = "<!-- changelog-generated:end -->"
RELEASE_COUNT = 3

RELEASE_HEADING = re.compile(
    r"^##\s+\[?(?P<version>v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\]?"
    r"(?:\s+\(\s*(?P<paren_date>\d{4}-\d{2}-\d{2})\s*\)"
    r"|\s+-\s+(?P<dash_date>\d{4}-\d{2}-\d{2}))?\s*$"
)
BULLET = re.compile(r"^\s*-\s+(.*)$")
INLINE_CODE = re.compile(r"`([^`]+)`")
BOLD = re.compile(r"\*\*(.+?)\*\*")
ITALIC = re.compile(r"(?<!\*)\*([^*]+)\*(?!\*)")

CATEGORY_TRANSLATIONS = {
    "新增": "changelogAdded",
    "Added": "changelogAdded",
    "修复": "changelogFixed",
    "Fixed": "changelogFixed",
    "优化": "changelogImproved",
    "改进": "changelogImproved",
    "Improved": "changelogImproved",
    "变更": "changelogChanged",
    "Changed": "changelogChanged",
    "安全": "changelogSecurity",
    "Security": "changelogSecurity",
    "弃用": "changelogDeprecated",
    "已弃用": "changelogDeprecated",
    "Deprecated": "changelogDeprecated",
    "移除": "changelogRemoved",
    "Removed": "changelogRemoved",
    "文档": "changelogDocumentation",
    "Documentation": "changelogDocumentation",
    "维护": "changelogMaintenance",
    "Maintenance": "changelogMaintenance",
    "性能": "changelogPerformance",
    "Performance": "changelogPerformance",
}


@dataclass
class ChangelogCategory:
    name: str
    points: list[str] = field(default_factory=list)


@dataclass
class Release:
    version: str
    date: str | None
    categories: list[ChangelogCategory] = field(default_factory=list)


def parse_changelog(markdown: str) -> list[Release]:
    releases: list[Release] = []
    current_release: Release | None = None
    current_category: ChangelogCategory | None = None
    current_point: int | None = None

    for line in markdown.splitlines():
        if line.startswith("## "):
            match = RELEASE_HEADING.match(line)
            current_category = None
            current_point = None
            if not match:
                current_release = None
                continue

            release_date = match.group("paren_date") or match.group("dash_date")
            if release_date:
                date.fromisoformat(release_date)
            current_release = Release(match.group("version").removeprefix("v"), release_date)
            releases.append(current_release)
            continue

        if current_release is None:
            continue

        if line.startswith("### "):
            current_category = ChangelogCategory(line[4:].strip())
            current_release.categories.append(current_category)
            current_point = None
            continue

        if current_category is None:
            continue

        bullet_match = BULLET.match(line)
        if bullet_match:
            current_category.points.append(bullet_match.group(1).strip())
            current_point = len(current_category.points) - 1
        elif not line.strip():
            current_point = None
        elif current_point is not None and line[:1].isspace() and not line.lstrip().startswith(("-", "#")):
            current_category.points[current_point] += " " + line.strip()

    if not releases:
        raise ValueError(f"No versioned release headings found in {CHANGELOG_SOURCE}")
    return releases[:RELEASE_COUNT]


def render_inline_markdown(value: str) -> str:
    escaped = html.escape(value, quote=False)
    escaped = INLINE_CODE.sub(r"<code>\1</code>", escaped)
    escaped = BOLD.sub(r"<strong>\1</strong>", escaped)
    return ITALIC.sub(r"<em>\1</em>", escaped)


def render_release(release: Release, is_latest: bool) -> str:
    version = html.escape(release.version)
    release_date = release.date
    date_markup = ""
    if release_date:
        parsed_date = date.fromisoformat(release_date)
        date_label = html.escape(
            f"{parsed_date.year} 年 {parsed_date.month} 月 {parsed_date.day} 日"
        )
        date_value = html.escape(release_date, quote=True)
        date_markup = (
            f'<time datetime="{date_value}" data-changelog-date="{date_value}">'
            f"{date_label}</time>"
        )

    classes = "changelog-entry is-latest" if is_latest else "changelog-entry"
    badge = (
        '<span class="changelog-badge" data-i18n="changelogLatest">最新</span>'
        if is_latest
        else ""
    )
    lines = [
        f'<article class="{classes}">',
        '  <div class="changelog-entry-head">',
        '    <p class="label">',
        f'      <span class="label-num">v{version}</span>{date_markup}',
        "    </p>",
        f"    {badge}" if badge else "",
        "  </div>",
        '  <ul class="changelog-points">',
    ]

    for category in release.categories:
        category_key = CATEGORY_TRANSLATIONS.get(category.name)
        if category_key:
            category_markup = (
                f'<span class="changelog-type" data-i18n="{category_key}">'
                f"{html.escape(category.name)}</span>"
            )
        else:
            category_markup = (
                f'<span class="changelog-type" lang="zh-CN">'
                f"{html.escape(category.name)}</span>"
            )

        for point in category.points:
            lines.extend(
                [
                    "    <li>",
                    f"      {category_markup}",
                    f'      <span lang="zh-CN">{render_inline_markdown(str(point))}</span>',
                    "    </li>",
                ]
            )

    lines.extend(["  </ul>", "</article>"])
    return "\n".join(line for line in lines if line)


def render_changelog(releases: list[Release]) -> str:
    return "\n\n".join(
        render_release(release, is_latest=(index == 0))
        for index, release in enumerate(releases)
    )


def generate_site(output_dir: Path) -> None:
    source_index_path = WEBSITE_SOURCE / "index.html"
    source_index = source_index_path.read_text(encoding="utf-8")
    if source_index.count(START_MARKER) != 1 or source_index.count(END_MARKER) != 1:
        raise ValueError(f"Expected one pair of generated changelog markers in {source_index_path}")

    start, remainder = source_index.split(START_MARKER, maxsplit=1)
    _, end = remainder.split(END_MARKER, maxsplit=1)
    releases = parse_changelog(CHANGELOG_SOURCE.read_text(encoding="utf-8"))
    generated = render_changelog(releases)
    output_index = "\n".join(
        [start.rstrip(), START_MARKER, generated, END_MARKER, end.lstrip()]
    )

    output_dir = output_dir.expanduser().resolve()
    source_dir = WEBSITE_SOURCE.resolve()
    if output_dir == source_dir or source_dir in output_dir.parents:
        raise ValueError("Output directory must not be inside the source website directory")

    shutil.copytree(source_dir, output_dir, dirs_exist_ok=True)
    (output_dir / "index.html").write_text(output_index, encoding="utf-8", newline="\n")
    print(f"Generated the latest {len(releases)} changelog entries in {output_dir}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Website artifact directory (default: {DEFAULT_OUTPUT})",
    )
    args = parser.parse_args()
    generate_site(args.output_dir)


if __name__ == "__main__":
    main()
