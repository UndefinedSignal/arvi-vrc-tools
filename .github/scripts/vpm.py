#!/usr/bin/env python3
"""Утилиты VPM-пака ArVi VRC Tools.

  python .github/scripts/vpm.py meta              — создать недостающие .meta (стабильные GUID)
  python .github/scripts/vpm.py pack [--out dist] — собрать zip всех пакетов + тестовый index.json
  python .github/scripts/vpm.py release           — (CI) выпустить релизы для новых версий
  python .github/scripts/vpm.py listing --out _site — (CI) собрать VPM listing из всех релизов

Каждый пакет лежит в Packages/<name>/ и выпускается отдельным релизом с тегом
<name>-<version>. Чтобы выпустить новую версию — подними "version" в package.json
и запушь в main.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKAGES = ROOT / "Packages"
SOURCE = ROOT / "source.json"
WEBSITE = ROOT / "website"


# ---------------------------------------------------------------- helpers

def packages():
    for d in sorted(PACKAGES.iterdir()):
        if (d / "package.json").is_file():
            yield d, json.loads((d / "package.json").read_text(encoding="utf-8"))


def tag_of(m):
    return f"{m['name']}-{m['version']}"


def zip_name(m):
    return f"{m['name']}-{m['version']}.zip"


def build_zip(pkg_dir: Path, dest: Path):
    """Содержимое пакета кладётся в корень zip (так требует VPM)."""
    dest.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        for p in sorted(pkg_dir.rglob("*")):
            rel = p.relative_to(pkg_dir)
            if any(part.startswith(".") for part in rel.parts):
                continue
            # фиксированная дата — одинаковый вход даёт одинаковый zip
            info = zipfile.ZipInfo(rel.as_posix() + ("/" if p.is_dir() else ""), (2020, 1, 1, 0, 0, 0))
            if p.is_dir():
                info.external_attr = 0o40755 << 16
                z.writestr(info, b"")
            else:
                info.external_attr = 0o644 << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(info, p.read_bytes())
    return hashlib.sha256(dest.read_bytes()).hexdigest()


def gh(*args, capture=True, check=True):
    r = subprocess.run(["gh", *args], capture_output=capture, text=True)
    if check and r.returncode != 0:
        sys.exit(f"gh {' '.join(args)} failed:\n{r.stderr}")
    return r


def repo_slug():
    slug = os.environ.get("GITHUB_REPOSITORY")
    if not slug:
        sys.exit("GITHUB_REPOSITORY не задан (запускается в GitHub Actions)")
    return slug


def write_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def listing_base():
    src = json.loads(SOURCE.read_text(encoding="utf-8"))
    return {k: src[k] for k in ("name", "id", "url", "author") if k in src} | {"packages": {}}


def add_to_listing(listing, manifest):
    listing["packages"].setdefault(manifest["name"], {"versions": {}})["versions"][manifest["version"]] = manifest


def copy_website(out: Path):
    out.mkdir(parents=True, exist_ok=True)
    if WEBSITE.is_dir():
        for p in WEBSITE.iterdir():
            if p.is_file():
                shutil.copy2(p, out / p.name)
    src = json.loads(SOURCE.read_text(encoding="utf-8"))
    html = out / "index.html"
    if html.exists():
        s = html.read_text(encoding="utf-8")
        for k, v in {"{{LISTING_URL}}": src["url"], "{{LISTING_NAME}}": src["name"],
                     "{{REPO_URL}}": src.get("githubUrl", "")}.items():
            s = s.replace(k, v)
        html.write_text(s, encoding="utf-8")


# ---------------------------------------------------------------- meta

META_HEAD = "fileFormatVersion: 2\nguid: {guid}\n"
IMPORTER_TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
           "  executionOrder: 0\n  icon: {instanceID: 0}\n",
    ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n",
    "package.json": "PackageManifestImporter:\n  externalObjects: {}\n",
    ".json": "TextScriptImporter:\n  externalObjects: {}\n",
    ".md": "TextScriptImporter:\n  externalObjects: {}\n",
    ".txt": "TextScriptImporter:\n  externalObjects: {}\n",
}
FOLDER = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
DEFAULT = "DefaultImporter:\n  externalObjects: {}\n"


def stable_guid(pkg_name, rel):
    return hashlib.md5(f"{pkg_name}/{rel}".encode()).hexdigest()


def cmd_meta(_):
    created = 0
    for pkg_dir, m in packages():
        for p in sorted(pkg_dir.rglob("*")):
            rel = p.relative_to(pkg_dir)
            if p.suffix == ".meta" or any(x.startswith(".") for x in rel.parts):
                continue
            meta = p.with_name(p.name + ".meta")
            if meta.exists():
                continue
            if p.is_dir():
                body = FOLDER
            else:
                body = IMPORTERS.get(p.name) or IMPORTERS.get(p.suffix.lower()) or DEFAULT
            meta.write_text(META_HEAD.format(guid=stable_guid(m["name"], rel.as_posix())) + body + IMPORTER_TAIL,
                            encoding="utf-8", newline="\n")
            created += 1
            print("created", meta.relative_to(ROOT))
    # предупреждаем о .meta без файла
    for pkg_dir, _ in packages():
        for meta in pkg_dir.rglob("*.meta"):
            if not meta.with_name(meta.name[:-5]).exists():
                print("orphan meta:", meta.relative_to(ROOT))
    print(f"{created} .meta создано")


# ---------------------------------------------------------------- pack (локальная проверка)

def cmd_pack(a):
    out = Path(a.out).resolve()
    listing = listing_base()
    for pkg_dir, m in packages():
        z = out / zip_name(m)
        sha = build_zip(pkg_dir, z)
        add_to_listing(listing, m | {"url": z.as_uri(), "zipSHA256": sha})
        print(f"{z.name}  sha256={sha}")
    write_json(out / "index.json", listing)
    print("listing:", out / "index.json")


# ---------------------------------------------------------------- release (CI)

def cmd_release(_):
    slug = repo_slug()
    sha_commit = os.environ.get("GITHUB_SHA", "")
    work = ROOT / "_release"
    for pkg_dir, m in packages():
        tag = tag_of(m)
        if gh("release", "view", tag, "--repo", slug, check=False).returncode == 0:
            print(f"= {tag} уже выпущен")
            continue
        z = work / tag / zip_name(m)
        sha = build_zip(pkg_dir, z)
        manifest = m | {
            "url": f"https://github.com/{slug}/releases/download/{tag}/{zip_name(m)}",
            "zipSHA256": sha,
        }
        mf = work / tag / "package.json"
        write_json(mf, manifest)
        notes = f"{m.get('displayName', m['name'])} {m['version']}\n\n{m.get('description', '')}"
        args = ["release", "create", tag, str(z), str(mf), "--repo", slug,
                "--title", f"{m.get('displayName', m['name'])} {m['version']}", "--notes", notes]
        if sha_commit:
            args += ["--target", sha_commit]
        gh(*args, capture=False)
        print(f"+ {tag} выпущен")


# ---------------------------------------------------------------- listing (CI)

def cmd_listing(a):
    slug = repo_slug()
    out = Path(a.out).resolve()
    listing = listing_base()
    names = {m["name"] for _, m in packages()}
    # по одному компактному JSON-объекту на строку, со всех страниц
    raw = gh("api", f"repos/{slug}/releases", "--paginate", "--jq",
             ".[] | select(.draft | not) | {tag_name, assets: [.assets[].name]}").stdout
    releases = [json.loads(line) for line in raw.splitlines() if line.strip()]
    for rel in releases:
        if "package.json" not in rel["assets"]:
            continue
        r = gh("release", "download", rel["tag_name"], "--repo", slug, "--pattern", "package.json", "--output", "-")
        try:
            m = json.loads(r.stdout)
        except json.JSONDecodeError:
            print(f"! {rel['tag_name']}: битый package.json, пропускаю")
            continue
        if m.get("name") not in names:
            print(f"! {rel['tag_name']}: пакета {m.get('name')} больше нет в репо, всё равно оставляю в листинге")
        add_to_listing(listing, m)
        print(f"  {m['name']} {m['version']}")
    write_json(out / "index.json", listing)
    copy_website(out)
    print("listing:", out / "index.json")


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("meta").set_defaults(fn=cmd_meta)
    s = sub.add_parser("pack"); s.add_argument("--out", default="dist"); s.set_defaults(fn=cmd_pack)
    sub.add_parser("release").set_defaults(fn=cmd_release)
    s = sub.add_parser("listing"); s.add_argument("--out", default="_site"); s.set_defaults(fn=cmd_listing)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
