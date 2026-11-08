#!/usr/bin/env python3
"""Refresh the offline license bundle from restored public NuGet packages.

Normal builds do not run this script or access the network. Run it after
restoring dependency upgrades; review the resulting original documents and
manifest before committing. Only package metadata and public license files
are read. Machine-specific package-cache paths are never written to the bundle.
"""

import argparse
import concurrent.futures
import hashlib
import io
import json
import pathlib
import re
import struct
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "src/Kkindle.Infrastructure/ThirdParty/Notices"
MANIFEST = BUNDLE / "manifest.json"
SOURCE_CACHE = ROOT / "artifacts/license-materials-cache"

# NuGet sometimes omits a repository URL or uses a redirect rather than the
# real source repository. These overrides identify sources, not new licenses.
REPOSITORY_OVERRIDES = {
    "avalonia.controls.webview": "AvaloniaUI/Avalonia.Controls.WebView",
}
LICENSE_NAMES = ("LICENSE", "LICENSE.txt", "LICENSE.TXT", "LICENSE.md", "licence.md", "LICENCE.md", "License", "License.txt", "License.md", "license.txt", "COPYING")
EXTRA_NAMES = {
    "aws/aws-sdk-net": ("Notice.txt", "NOTICE.txt", "NOTICE"),
    "UglyToad/PdfPig": ("NOTICE", "NOTICE.txt"),
    "modelcontextprotocol/csharp-sdk": ("NOTICE", "NOTICE.txt"),
    "ericsink/SQLitePCL.raw": ("NOTICE",),
}

# Reviewed font identities, read from the actual bundled font name tables.
# A future font replacement must be reviewed, rather than inheriting a license
# merely because the library still embeds nine font resources.
REVIEWED_FONTS = {
    **{name: ("Version 3.019;git-0a5106e0b", "OFL-1.1") for name in
       ("Inter-Bold", "Inter-Light", "Inter-Medium", "Inter-Regular", "Inter-SemiBold", "Inter-Thin")},
    "AMS-Capital-Blackboard-Bold": ("001.000", "OFL-1.1"),
    "NMR10": ("Version 4.002", "OFL-1.1"),
    "LatinModernMath-Regular": ("Version 1.959", "GUST Font License"),
}
TYPOGRAPHY_REFS = {
    # git ls-tree of the actual CSharpMath package repository commit.
    "ba73eb9b89662e2f9c039f8371f29bec05d13c48": "d331968f3ee714332a8ea4fa95a21c15812a8d93",
}


class UncachedDocument(RuntimeError):
    pass


def digest(data):
    return hashlib.sha256(data).hexdigest()


def public_package_url(package, path=""):
    base = "https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg".format(
        package["id"].lower(), package["version"].lower())
    return base + ("#" + path if path else "")


class Documents:
    def __init__(self, offline):
        self.offline = offline
        self.by_hash = {}
        self.old_urls = {}
        self.old_extracted = []
        self.resolved = {}
        if MANIFEST.is_file():
            for doc in json.loads(MANIFEST.read_text())["documents"]:
                path = BUNDLE / doc["path"]
                if path.is_file() and digest(path.read_bytes()) == doc["sha256"]:
                    if doc.get("kind") == "extracted-source-notices":
                        self.old_extracted.append((doc, path))
                        # The source URLs point at source code, not at this
                        # extracted notice's bytes. Never reuse it as a source
                        # file when regenerating notices.
                        continue
                    for source in doc["sources"]:
                        self.old_urls[source] = path

    def fetch(self, url, limit=2 * 1024 * 1024):
        if url in self.old_urls:
            return self.old_urls[url].read_bytes()
        cache = SOURCE_CACHE / digest(url.encode())
        if cache.is_file():
            return cache.read_bytes()
        if self.offline:
            raise UncachedDocument("Public document is not cached: " + url)
        request = urllib.request.Request(url, headers={"User-Agent": "Kkindle-license-materials/1.0"})
        with urllib.request.urlopen(request, timeout=20) as response:
            data = response.read(limit + 1)
        if len(data) > limit:
            raise RuntimeError("Public license document exceeds size limit: " + url)
        SOURCE_CACHE.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=SOURCE_CACHE, delete=False) as temporary:
            temporary.write(data)
            temporary_path = pathlib.Path(temporary.name)
        temporary_path.replace(cache)
        return data

    def add(self, data, desired_path, source):
        sha = digest(data)
        if sha in self.by_hash:
            doc = self.by_hash[sha]
            if source not in doc["sources"]:
                doc["sources"].append(source)
            return doc["path"]
        path = pathlib.PurePosixPath(desired_path)
        if path.is_absolute() or ".." in path.parts:
            raise RuntimeError("Invalid license destination")
        target = BUNDLE / path
        if target.is_file() and digest(target.read_bytes()) != sha:
            # Preserve differently versioned third-party documents rather than
            # overwriting one package's notice with another package's text.
            path = path.with_name(path.stem + "-" + sha[:12] + path.suffix)
            target = BUNDLE / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        self.by_hash[sha] = {"path": str(path), "sha256": sha, "sources": [source]}
        return str(path)


def read_package(directory):
    nuspec = next(directory.glob("*.nuspec"), None)
    if nuspec is None:
        raise RuntimeError("Restored package is missing its nuspec: " + directory.name)
    values = {}
    repository = {}
    license_type = ""
    for element in ET.parse(nuspec).iter():
        tag = element.tag.split("}")[-1]
        if tag in ("id", "version", "authors", "copyright", "projectUrl", "license"):
            values[tag] = (element.text or "").strip()
        if tag == "license":
            license_type = element.attrib.get("type", "")
        if tag == "repository":
            repository = element.attrib
    return {"id": values["id"], "version": values["version"],
            "licenseType": license_type, "license": values.get("license", ""),
            "copyright": values.get("copyright", ""), "authors": values.get("authors", ""),
            "repository": repository, "projectUrl": values.get("projectUrl", ""),
            "developmentOnly": values["id"].lower() == "avaloniaui.diagnosticssupport",
            "directory": directory}


def restored_packages(extra_caches):
    found = {}
    caches = set(pathlib.Path(x).resolve() for x in extra_caches)
    for assets in sorted((ROOT / "src").rglob("project.assets.json")):
        data = json.loads(assets.read_text())
        folders = [pathlib.Path(x) for x in data.get("packageFolders", {})]
        caches.update(folders)
        for key, value in data.get("libraries", {}).items():
            if value.get("type") != "package":
                continue
            package_id, version = key.rsplit("/", 1)
            package_key = (package_id.lower(), version.lower())
            if package_key in found:
                continue
            relative = value.get("path", package_id.lower() + "/" + version.lower())
            directory = next((folder / relative for folder in folders if (folder / relative).is_dir()), None)
            if directory is None:
                raise RuntimeError("Restore the package before refreshing licenses: " + key)
            found[package_key] = read_package(directory)
    # Runtime packs are resolved by the SDK and do not necessarily appear in
    # project.assets.json. Preserve their own unmodified license/notices too.
    for cache in sorted(caches):
        for pattern in ("microsoft.netcore.app.runtime.*", "microsoft.aspnetcore.app.runtime.*"):
            for parent in sorted(cache.glob(pattern)):
                for directory in sorted(parent.iterdir()):
                    if directory.is_dir() and next(directory.glob("*.nuspec"), None):
                        package = read_package(directory)
                        found[(package["id"].lower(), package["version"].lower())] = package
    if not found:
        raise RuntimeError("No restored source projects were found; restore the application first")
    return [found[key] for key in sorted(found)]


def github_source(package):
    repository = package["repository"]
    repo = REPOSITORY_OVERRIDES.get(package["id"].lower())
    if not repo:
        url = repository.get("url") or package["projectUrl"]
        match = re.match(r"https?://github\.com/([^/]+/[^/?#]+)", url)
        if not match:
            raise RuntimeError("Record the original license source for " + package["id"])
        repo = match[1].removesuffix(".git").rstrip("/")
    ref = repository.get("commit")
    if not ref:
        # Packages without source commits are recorded honestly: first try the
        # package version tag, then the public repository's branch snapshot.
        refs = (package["version"], "v" + package["version"], "master", "main")
    else:
        refs = (ref,)
    return repo, refs


def resolve_upstream_document(package, documents, extra=False):
    repo, refs = github_source(package)
    key = (repo, refs, extra)
    if key in documents.resolved:
        return documents.resolved[key]
    names = EXTRA_NAMES.get(repo, ()) if extra else LICENSE_NAMES
    for ref in refs:
        for name in names:
            url = "https://raw.githubusercontent.com/{}/{}/{}".format(repo, ref, name)
            try:
                data = documents.fetch(url)
            except UncachedDocument:
                continue
            except urllib.error.HTTPError as error:
                if error.code == 404:
                    continue
                raise
            text = data.decode("utf-8", errors="replace")
            if not extra and (len(data) < 200 or not re.search(
                    r"Permission is hereby granted|Apache License|Redistribution and use|GENERAL PUBLIC LICENSE", text, re.I)):
                continue
            documents.resolved[key] = (data, name, url)
            return data, name, url
    if not extra:
        raise RuntimeError("Could not obtain the original license for " + package["id"])
    return None


def upstream_document(package, documents, extra=False):
    result = resolve_upstream_document(package, documents, extra)
    if result is None:
        return None
    data, name, url = result
    path = "packages/{}/{}/{}".format(package["id"], package["version"], name)
    return documents.add(data, path, url)


def package_documents(package, documents):
    paths = []
    primary = False
    for file in sorted(package["directory"].rglob("*")):
        if not file.is_file() or not re.search(r"licen[cs]e|notice|copying", file.name, re.I):
            continue
        relative = file.relative_to(package["directory"]).as_posix()
        path = "packages/{}/{}/{}".format(package["id"], package["version"], relative)
        paths.append(documents.add(file.read_bytes(), path, public_package_url(package, relative)))
        primary |= bool(re.match(r"(?:license|copying)(?:\.|$)", file.name, re.I))
    if package["developmentOnly"]:
        url = "https://raw.githubusercontent.com/AvaloniaUI/avalonia-docs/main/tools/faq.md"
        paths.append(documents.add(documents.fetch(url), "development/Avalonia-tools-faq.md", url))
    elif not primary:
        paths.append(upstream_document(package, documents))
    if package["licenseType"] == "expression" and "Apache-2.0" in package["license"]:
        # Include the standard Apache text as well as any original NOTICE and
        # differently licensed packaging/native material. No copyright notice
        # is invented or substituted for an upstream document.
        path = documents.add((ROOT / "src/Kkindle.Infrastructure/ThirdParty/MdxParser/LICENSE.txt").read_bytes(),
                             "licenses/Apache-2.0.txt", "https://www.apache.org/licenses/LICENSE-2.0.txt")
        paths.append(path)
        if not any("notice" in p.lower() for p in paths):
            extra = upstream_document(package, documents, extra=True)
            if extra:
                paths.append(extra)
    return sorted(set(paths))


def embedded_font_names(assembly):
    data = assembly.read_bytes()
    result = []
    for match in re.finditer(rb"OTTO|\x00\x01\x00\x00", data):
        start = match.start()
        try:
            count = struct.unpack_from(">H", data, start + 4)[0]
            if not 5 <= count <= 100:
                continue
            tables = {}
            for index in range(count):
                tag, _, offset, length = struct.unpack_from(">4sIII", data, start + 12 + index * 16)
                if any(c < 32 or c > 126 for c in tag) or start + offset + length > len(data):
                    raise ValueError("Not a font")
                tables[tag] = (start + offset, length)
            if b"name" not in tables:
                continue
            position, total = tables[b"name"]
            fmt, records, string_start = struct.unpack_from(">HHH", data, position)
            if fmt not in (0, 1) or records > 1000:
                continue
            names = {}
            for index in range(records):
                platform, _, _, name_id, length, offset = struct.unpack_from(">HHHHHH", data, position + 6 + index * 12)
                if name_id not in (0, 1, 5, 6, 13, 14):
                    continue
                p = position + string_start + offset
                if p + length > position + total:
                    raise ValueError("Not a valid font name table")
                text = data[p:p + length].decode("utf-16-be" if platform in (0, 3) else "mac_roman", errors="replace")
                if text not in names.setdefault(str(name_id), []):
                    names[str(name_id)].append(text)
            if names:
                result.append(names)
        except (ValueError, struct.error):
            continue
    return result


def typography_documents(package, documents):
    """Preserve notices of the font/text code compiled into CSharpMath itself."""
    ref = TYPOGRAPHY_REFS.get(package["repository"].get("commit"))
    if not ref:
        raise RuntimeError("Review the Typography submodule of the changed CSharpMath package")
    base = "https://raw.githubusercontent.com/LayoutFarm/Typography/" + ref + "/"
    paths = []
    for source, destination in (
        (base + "LICENSE.md", "components/Typography/LICENSE.md"),
        (base + "Typography.TextBreak/LICENSE", "components/Typography/TextBreak-LICENSE.txt"),
        ("https://raw.githubusercontent.com/freetype/freetype/VER-2-6-5/docs/FTL.TXT", "components/Typography/FreeType-FTL.txt"),
        ("https://raw.githubusercontent.com/unicode-org/icu/release-58-2/icu4c/LICENSE", "components/Typography/ICU-LICENSE.txt"),
    ):
        paths.append(documents.add(documents.fetch(source), destination, source))

    # A reviewed notice for the same immutable submodule can be reused in a
    # fresh checkout without downloading its source files again. Verify its
    # existing hash above; a new submodule revision requires a fresh extraction.
    for previous, file in documents.old_extracted:
        if (previous.get("component") == "Typography" and previous.get("revision") == ref
                and all(source.startswith(base) for source in previous["sources"])):
            data = file.read_bytes()
            for source in previous["sources"]:
                notice_path = documents.add(data, "components/Typography/NOTICE.md", source)
            documents.by_hash[digest(data)].update(kind="extracted-source-notices", component="Typography", revision=ref)
            return paths + [notice_path]

    sources = set()
    for project in ("Typography.OpenFont/Typography.OpenFont.projitems",
                    "Typography.GlyphLayout/Typography.GlyphLayout.projitems",
                    "Typography.TextBreak/Typography.TextBreak/Typography.TextBreak.projitems"):
        root = ET.fromstring(documents.fetch(base + project))
        for element in root.iter():
            if element.tag.split("}")[-1] != "Compile":
                continue
            include = element.attrib["Include"].replace("$(MSBuildThisFileDirectory)", "").replace("\\", "/")
            sources.add((pathlib.PurePosixPath(project).parent / include).as_posix())

    def read_notices(source):
        text = documents.fetch(base + source).decode("utf-8-sig")
        comments = re.findall(r"/\*.*?\*/|(?m:^[ \t]*//[^\n]*(?:\n[ \t]*//[^\n]*)*)", text, re.S)
        return source, [comment for comment in comments if re.search(
            r"copyright|\bMIT\b|Apache|FreeType|licen[cs]e|all rights reserved", comment, re.I)]

    notices = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        for source, comments in pool.map(read_notices, sorted(sources)):
            for comment in comments:
                notices.setdefault(comment, []).append(source)
    lines = ["# Typography incorporated-source notices", "",
             "CSharpMath.Rendering compiles font and text code from the Typography submodule",
             "at commit `" + ref + "`. The complete original project license/attribution",
             "file and TextBreak MIT license accompany this notice, along with the",
             "FreeType FTL and historical ICU license texts referenced by this code.",
             "Apache-2.0 text is included in ../../licenses/Apache-2.0.txt.", "",
             "The following comment excerpts are copied unchanged from the source files",
             "included by the OpenFont, GlyphLayout and TextBreak shared project imports.",
             "These excerpts preserve individual copyright and licensing notices; they",
             "are an extracted notice document, rather than an upstream LICENSE file.", ""]
    for comment, source_paths in notices.items():
        lines.extend(["Sources: " + ", ".join("[" + source + "](" + base + source + ")" for source in source_paths),
                      "", "```text", comment, "```", ""])
    data = "\n".join(lines).encode("utf-8")
    desired_path = "components/Typography/NOTICE.md"
    for source in sorted(sources):
        notice_path = documents.add(data, desired_path, base + source)
    documents.by_hash[digest(data)].update(kind="extracted-source-notices", component="Typography", revision=ref)
    paths.append(notice_path)
    return paths


def font_documents(packages, documents):
    inter = next(p for p in packages if p["id"].lower() == "avalonia.fonts.inter")
    math = next(p for p in packages if p["id"].lower() == "sylinko.csharpmath.avalonia")
    fonts = []
    for package, assembly_name in ((inter, "Avalonia.Fonts.Inter.dll"), (math, "CSharpMath.Rendering.dll")):
        assembly = package["directory"] / "lib/net10.0" / assembly_name
        if not assembly.is_file():
            assembly = next(package["directory"].glob("lib/*/" + assembly_name))
        for names in embedded_font_names(assembly):
            postscript = names["6"][0]
            reviewed = REVIEWED_FONTS.get(postscript)
            if reviewed is None or names["5"][0] != reviewed[0] or not names.get("0"):
                raise RuntimeError("Review the changed embedded font and its original license: " + postscript)
            fonts.append({"name": names["1"][0], "postscriptName": postscript, "version": names["5"][0],
                          "copyright": names["0"], "package": package["id"], "packageVersion": package["version"],
                          "assembly": assembly.relative_to(package["directory"]).as_posix(),
                          "assemblySha256": digest(assembly.read_bytes()), "license": reviewed[1]})
    if {font["postscriptName"] for font in fonts} != set(REVIEWED_FONTS) or len(fonts) != len(REVIEWED_FONTS):
        raise RuntimeError("Embedded fonts changed; review their actual licenses before refreshing")
    inter_url = "https://raw.githubusercontent.com/rsms/inter/v3.19/LICENSE.txt"
    inter_path = documents.add(documents.fetch(inter_url), "fonts/Inter/LICENSE.txt", inter_url)
    # AMS and Cyrillic fonts retain their exact embedded copyright notices in
    # the generated font NOTICE; the OFL text is included separately in full.
    ofl_url = "https://raw.githubusercontent.com/Happypig375/AMSFonts-Ttf-Otf/master/LICENSE.md"
    ofl_path = documents.add(documents.fetch(ofl_url), "fonts/AMS-OFL-1.1.md", ofl_url)
    gust_url = "https://mirrors.ibiblio.org/CTAN/fonts/lm-math.zip"
    previous_gust = next((url for url in documents.old_urls if url.startswith(gust_url + "#")
                          and url.lower().endswith("gust-font-license.txt")), None)
    if previous_gust:
        gust_path = documents.add(documents.fetch(previous_gust), "fonts/GUST-FONT-LICENSE.txt", previous_gust)
    else:
        archive = zipfile.ZipFile(io.BytesIO(documents.fetch(gust_url, 10 * 1024 * 1024)))
        gust_entry = next(x for x in archive.namelist() if x.lower().endswith("gust-font-license.txt"))
        gust_path = documents.add(archive.read(gust_entry), "fonts/GUST-FONT-LICENSE.txt", gust_url + "#" + gust_entry)
    lppl_url = "https://www.latex-project.org/lppl/lppl-1-3c.txt"
    lppl_path = documents.add(documents.fetch(lppl_url), "fonts/LPPL-1.3c.txt", lppl_url)
    readme_url = "https://raw.githubusercontent.com/Sylinko/CSharpMath.Avalonia/{}/ReadMe.md".format(math["repository"]["commit"])
    # Preserve the complete original attribution document, including its MIT
    # Typography dependency notice, as a license source for the embedded fonts.
    readme_path = documents.add(documents.fetch(readme_url), "fonts/CSharpMath-upstream-ReadMe.md", readme_url)
    lines = ["# Embedded font copyright and license notices", "",
             "The packaging libraries' MIT licenses do not replace these font licenses.",
             "The fonts are bundled without modification. The following copyright notices",
             "are copied from the actual font name tables in the restored assemblies.", ""]
    for font in fonts:
        refs = [gust_path, lppl_path] if font["license"] == "GUST Font License" else [inter_path if font["package"] == inter["id"] else ofl_path]
        font["documents"] = refs
        lines.extend(["## " + font["postscriptName"], "", "Family: " + font["name"], "Version: " + font["version"],
                      "License: " + font["license"], "", *font["copyright"], "",
                      "License text: " + ", ".join(refs), ""])
    lines.extend(["Math font license attribution source: " + readme_path, "",
                  "Reserved Font Names in the original notices remain unchanged.", ""])
    (BUNDLE / "fonts/NOTICE.md").write_text("\n".join(lines), encoding="utf-8")
    return fonts


def refresh(offline, caches):
    documents = Documents(offline)
    packages = restored_packages(caches)
    missing = {}
    for package in packages:
        if package["developmentOnly"]:
            continue
        if not any(file.is_file() and re.match(r"(?:license|copying)(?:\.|$)", file.name, re.I)
                   for file in package["directory"].rglob("*")):
            repo, refs = github_source(package)
            missing.setdefault((repo, refs), package)
    # Independent upstream reads happen together. Writing/deduplicating the
    # reviewed bundle stays sequential so the generated paths are deterministic.
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(lambda p: resolve_upstream_document(p, documents), missing.values()))
    for result in results:
        if result is None:
            raise RuntimeError("An original package license was not resolved")
    for package in packages:
        package["documents"] = package_documents(package, documents)
        if package["id"].lower() == "sylinko.csharpmath.avalonia":
            package["documents"].extend(typography_documents(package, documents))
        package.pop("directory")
    # Re-read directories for inspecting actual embedded font resources.
    fonts = font_documents(restored_packages(caches), documents)
    for name, source in (("LICENSE.MiniLZO.txt", ROOT / "LICENSE"),):
        (ROOT / "src/Kkindle.Infrastructure/ThirdParty/MdxParser" / name).write_bytes(source.read_bytes())
    bc_url = "https://raw.githubusercontent.com/bcgit/bc-csharp/release-1.9.0/crypto/License.html"
    bc_data = documents.fetch(bc_url)
    documents.add(bc_data, "components/MdxParser/BouncyCastle-LICENSE.html", bc_url)
    (ROOT / "src/Kkindle.Infrastructure/ThirdParty/MdxParser/LICENSE.BouncyCastle.html").write_bytes(bc_data)
    manifest = {"schemaVersion": 1, "packages": packages, "fonts": fonts,
                "documents": sorted(documents.by_hash.values(), key=lambda x: x["path"])}
    BUNDLE.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = ["# Kkindle third-party notices", "",
             "Kkindle is distributed under the GNU GPL v3; see LICENSE.",
             "Third-party packages, fonts and native components retain their own licenses.",
             "This offline bundle preserves original license and notice texts, including",
             "native notices supplied in NuGet packages. The table combines platform dependencies",
             "and cached SDK runtime packs;",
             "a particular build may not use every listed package.", "",
             "## Incorporated source and native components", "",
             "- KindleUnpack adaptations: ThirdParty/KindleUnpack/NOTICE.md and LICENSE.txt (GPL v3 or later).",
             "- MDX/MiniLZO and digest code: ThirdParty/MdxParser/NOTICE.md and accompanying licenses.",
             "- PDFium and its native subcomponents: ThirdParty/PDFium/LICENSE, NOTICE.md and licenses/.",
             "- Embedded Inter and math fonts: ThirdParty/Notices/fonts/NOTICE.md and original license texts.",
             "- Typography code compiled into CSharpMath: ThirdParty/Notices/components/Typography/.",
             "- Noto fonts and Lucide icons: original license files under Assets/Fonts and Assets/Icons.",
             "- macOS libmtp/libusb: LGPL texts and the matching source archives, checksums and",
             "  build/replacement instructions under Contents/Resources/CorrespondingSource/MTP/.", "",
             "The existing KingHwaOldSong source/permission record is separate; this bundle does not",
             "replace the font author's original custom license or certify that record as an original agreement.", "",
             "## NuGet package licenses and copyright notices", "",
             "License expressions and copyright entries below are copied from the publishers'",
             "NuGet metadata. Original documents listed for each package take precedence for",
             "their respective components. Avalonia's DiagnosticsSupport bridge is a development",
             "integration and is excluded from Release binaries; its upstream FAQ is included.", "",
             "| Package | Version | Publisher license metadata | Copyright / authors | License and notice documents |",
             "| --- | --- | --- | --- | --- |"]
    for p in packages:
        copyright_text = (p["copyright"] or p["authors"]).replace("|", "\\|").replace("\n", " ")
        license_text = p["license"] or "Development integration; see upstream FAQ"
        refs = ", ".join("[{}](ThirdParty/Notices/{})".format(pathlib.PurePosixPath(x).name, x) for x in p["documents"])
        lines.append("| {} | {} | {} | {} | {} |".format(p["id"], p["version"], license_text, copyright_text, refs))
    lines.extend(["", "The machine-readable manifest and document SHA-256 values are in",
                  "ThirdParty/Notices/manifest.json. Dependency upgrades require refreshing and",
                  "reviewing this bundle; publish validation rejects undocumented package versions.", ""])
    (ROOT / "THIRD-PARTY-NOTICES.md").write_text("\n".join(lines), encoding="utf-8")
    keep = {x["path"] for x in manifest["documents"]} | {"manifest.json", "fonts/NOTICE.md"}
    for file in BUNDLE.rglob("*"):
        if file.is_file() and file.relative_to(BUNDLE).as_posix() not in keep:
            file.unlink()
    print("Offline bundle:", len(packages), "package versions,", len(fonts), "fonts,", len(documents.by_hash), "license/notice documents")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--offline", action="store_true", help="Use restored packages and already cached public license documents only")
    parser.add_argument("--package-cache", action="append", default=[], help="Additional restored NuGet cache (also reads SDK runtime pack notices)")
    args = parser.parse_args()
    refresh(args.offline, args.package_cache)
