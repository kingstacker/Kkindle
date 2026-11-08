#!/usr/bin/env python3
"""Install an isolated simulator copy, with local books and no connection settings."""

import argparse
import json
import plistlib
import shutil
import sqlite3
import subprocess
import tempfile
from pathlib import Path


PREVIEW_ID = "com.kingstacker.kkindle.nativepreview"


def run(*command: str, check: bool = True) -> str:
    result = subprocess.run(command, check=check, capture_output=True, text=True)
    return result.stdout.strip()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True, help="Built simulator .app")
    parser.add_argument("--simulator", required=True, help="Booted simulator UUID")
    parser.add_argument("--seed", type=Path, required=True, help="Local Kkindle directory with data/kkindle.db and EPUB/PDF files")
    parser.add_argument("--preserve-data", action="store_true", help="Update only the isolated preview app, keeping its data and scene sessions for upgrade verification")
    parser.add_argument("--upgrade-preview", action="store_true", help="Use a second isolated bundle for legacy-to-native upgrade tests")
    args = parser.parse_args()
    preview_id = "com.kingstacker.kkindle.nativeupgradepreview" if args.upgrade_preview else PREVIEW_ID
    source_app = args.app.resolve(strict=True)
    source_data = (args.seed / "data").resolve(strict=True)
    if not (source_data / "kkindle.db").is_file():
        parser.error("The seed must contain data/kkindle.db.")

    output = Path(tempfile.mkdtemp(prefix="kkindle-native-preview-"))
    preview_app = output / "NativePreview.app"
    shutil.copytree(source_app, preview_app)
    info_path = preview_app / "Info.plist"
    with info_path.open("rb") as stream:
        info = plistlib.load(stream)
    info["CFBundleIdentifier"] = preview_id
    info["CFBundleDisplayName"] = "Kkindle Native Preview"
    with info_path.open("wb") as stream:
        plistlib.dump(info, stream, fmt=plistlib.FMT_BINARY)

    seed_root = output / "Kkindle"
    seed_data = seed_root / "data"
    seed_data.mkdir(parents=True)
    # Whitelist only the database and the readable book assets. API keys,
    # protected passwords, connection settings and mobile preferences are excluded.
    for name in ("kkindle.db", "kkindle.db-wal", "kkindle.db-shm"):
        path = source_data / name
        if path.is_file():
            shutil.copy2(path, seed_data / name)
    for name in ("library", "covers", "fonts"):
        path = source_data / name
        if path.is_dir():
            shutil.copytree(path, seed_data / name)
    with sqlite3.connect(seed_data / "kkindle.db") as database:
        for extension, title in (("epub", "Native UI EPUB"), ("pdf", "Native UI PDF")):
            row = database.execute(
                "SELECT BookId FROM BookFiles WHERE lower(Format) = ? ORDER BY BookId LIMIT 1", (extension,)
            ).fetchone()
            if row is None:
                parser.error(f"The seed needs at least one local {extension.upper()} book.")
            database.execute("UPDATE Books SET Title = ? WHERE Id = ?", (title, row[0]))

    run("codesign", "--force", "--deep", "--sign", "-", str(preview_app))
    if args.upgrade_preview:
        # Artificial local preferences make an unintended reset during upgrade
        # observable. They contain no connection configuration or credentials.
        (seed_root / "mobile-preferences.json").write_text(json.dumps({
            "ThemeMode": 1, "ListView": False, "CollectionsView": False, "PdfZoom": 1,
            "Sort": 0, "ReaderTheme": 1, "ReaderFontScale": 1.25, "ReaderLineHeight": 1.75,
            "ReaderBodyPadding": 24, "ReaderParagraphIndent": True,
            "ReaderVerticalWriting": False, "ReaderPageAnimation": 2,
        }))
    run("xcrun", "simctl", "terminate", args.simulator, preview_id, check=False)
    run("xcrun", "simctl", "install", args.simulator, str(preview_app))
    container = Path(run("xcrun", "simctl", "get_app_container", args.simulator, preview_id, "data"))
    target = container / "Documents" / "Kkindle"
    if args.preserve_data:
        if not (target / "data" / "kkindle.db").is_file():
            parser.error("--preserve-data needs an existing isolated preview library.")
        print(f"Updated {preview_id}; preview data preserved; artifacts: {output}")
        return
    # The container comes from the dedicated preview bundle, never the user's app.
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(seed_root, target)
    print(f"Installed {preview_id}; isolated artifacts: {output}")


if __name__ == "__main__":
    main()
