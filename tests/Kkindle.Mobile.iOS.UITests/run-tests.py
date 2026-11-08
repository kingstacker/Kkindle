#!/usr/bin/env python3
"""Run native UI tests and capture visual checkpoints through CoreSimulator."""

import argparse
import json
import re
import struct
import subprocess
import tempfile
from pathlib import Path


def margin_colours(png: Path) -> list[str]:
    """Distinct colours, top to bottom, in the right page margin of a capture."""
    with tempfile.TemporaryDirectory() as directory:
        bitmap = Path(directory) / "capture.bmp"
        subprocess.run(["sips", "-s", "format", "bmp", str(png), "--out", str(bitmap)],
                       check=True, capture_output=True)
        data = bitmap.read_bytes()
    offset = struct.unpack_from("<I", data, 10)[0]
    width, height = struct.unpack_from("<ii", data, 18)
    channels = struct.unpack_from("<H", data, 28)[0] // 8
    stride = (width * channels + 3) & ~3
    x = int(width * 0.97)
    colours: list[str] = []
    for y in range(abs(height)):
        row = y if height < 0 else abs(height) - 1 - y
        index = offset + row * stride + x * channels
        colour = f"#{data[index + 2]:02X}{data[index + 1]:02X}{data[index]:02X}"
        if not colours or colours[-1] != colour:
            colours.append(colour)
    return colours


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--simulator", required=True)
    parser.add_argument("--results", type=Path, required=True, help="New output directory")
    parser.add_argument("--only", action="append", help="Optional XCTest method name; repeat to select several methods")
    args = parser.parse_args()
    output = args.results.resolve()
    if output.exists():
        parser.error("The results directory must not already exist.")
    output.mkdir(parents=True)
    screenshots = output / "screenshots"
    screenshots.mkdir()
    project = Path(__file__).resolve().parent / "NativeFrontendUITests.xcodeproj"
    command = [
        "xcodebuild", "test", "-project", str(project), "-scheme", "NativeFrontendUITests",
        "-destination", f"id={args.simulator}",
        "-derivedDataPath", str(output / "DerivedData"),
        "-resultBundlePath", str(output / "NativeFrontend.xcresult"),
        "-parallel-testing-enabled", "NO", "CODE_SIGNING_ALLOWED=YES", "CODE_SIGN_IDENTITY=-",
    ]
    if args.only:
        for method in args.only:
            command.append(f"-only-testing:NativeFrontendUITests/NativeFrontendTests/{method}")
    else:
        # Upgrade setup deliberately uses another preview installation and is
        # run separately after the host has installed the legacy then native UI.
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testLegacyUpgradePreservesLibraryAndPreferences")
        # The account scenario is selected explicitly with --only after both
        # regional localhost servers have been started with disposable data.
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testRegionalAccountRegistrationAndCloudSync")
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testAccountMembershipDevicesAndOrders")
    captures = []
    with (output / "xcodebuild.log").open("w") as log:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace", bufsize=1)
        assert process.stdout is not None
        for line in process.stdout:
            log.write(line)
            if "KKINDLE_VISUAL_CHECKPOINT:" in line:
                name = line.split("KKINDLE_VISUAL_CHECKPOINT:", 1)[1].strip()
                safe_name = re.sub(r"[^\w.-]", "_", name)
                filename = f"{len(captures) + 1:02d}-{safe_name}.png"
                result = subprocess.run(["xcrun", "simctl", "io", args.simulator, "screenshot", str(screenshots / filename)],
                                        capture_output=True, text=True)
                captures.append({"name": name, "file": filename, "captured": result.returncode == 0})
                print(f"Visual checkpoint: {name}; captured={result.returncode == 0}", flush=True)
            elif "Test Case '" in line or "error:" in line or "** TEST" in line or "KKINDLE_NATIVE_UI_REVIEW_" in line:
                print(line.strip(), flush=True)
        status = process.wait()
    # The SwiftUI paper above and below the page must match the page drawn by
    # C#. A different colour shows up as extra bands in the page margin.
    for capture in captures:
        if capture["name"].startswith("纸张-") and capture["captured"]:
            colours = margin_colours(screenshots / capture["file"])
            capture["marginColours"] = colours
            print(f"Paper margin {capture['name']}: {' '.join(colours)}", flush=True)
            if len(colours) != 1:
                print(f"error: {capture['file']} shows paper bands around the page.", flush=True)
                status = status or 1
    (screenshots / "manifest.json").write_text(json.dumps(captures, ensure_ascii=False, indent=2))
    print(f"UI results: {output}; exit={status}")
    return status


if __name__ == "__main__":
    raise SystemExit(main())
