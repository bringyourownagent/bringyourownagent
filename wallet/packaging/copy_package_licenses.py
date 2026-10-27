#!/usr/bin/env python3
"""Copies the license and notice files of the NuGet packages a published wallet carries into one folder (one subfolder per package and version).

The same job make-package.ps1 does for the Windows package (its Copy-PackageLicenses), for make-macos-package.sh.
The packages are the ones in the .deps.json files of the three programs (the runtime pack included). Some packages add their files through
build targets, so they are in no .deps.json file: those are found in project.assets.json by their content folder. A package with no license
file fails the run, unless NO_LICENSE_FILE lists it as "id/version" (the comment says where its license text comes from; the third-party
notice of the package names its license and copyright line as well).

    copy_package_licenses.py --app <published folder> --artifacts <dotnet artifacts folder> --out <new licenses folder> [--fonts-license OFL.txt]

Nothing is overwritten or deleted: --out must not exist yet.
"""
import argparse
import json
import os
import re
import shutil
import sys

DEPS = ("Byoa.Wallet.deps.json", "byoa-host.deps.json", "byoa-mcp.deps.json")

# the packages that have no license file of their own
NO_LICENSE_FILE = {
    "Avalonia/11.3.22",                      # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Desktop/11.3.22",              # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.FreeDesktop/11.3.22",          # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Native/11.3.22",               # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Remote.Protocol/11.3.22",      # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Skia/11.3.22",                 # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Themes.Fluent/11.3.22",        # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.Win32/11.3.22",                # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "Avalonia.X11/11.3.22",                  # MIT: AvaloniaUI/Avalonia licence.md at commit 627ae9e
    "MicroCom.Runtime/0.11.0",               # MIT: kekekeks/MicroCom LICENSE on its master branch
    "Tmds.DBus.Protocol/0.21.3",             # MIT: tmds/Tmds.DBus COPYING at commit 8cb04f6
}

WANTED = re.compile(r"^(LICEN[CS]E|NOTICE|THIRD-PARTY-NOTICES)", re.IGNORECASE)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", help="the published folder, when it holds the .deps.json files (a single-file publish does not: they are then taken from --artifacts)")
    parser.add_argument("--artifacts", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--packages", default=os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages"))
    parser.add_argument("--fonts-license")
    args = parser.parse_args()

    if os.path.exists(args.out):
        print(f"copy_package_licenses: {args.out} exists; choose a folder that does not", file=sys.stderr)
        return 1
    if not os.path.isdir(args.packages):
        print(f"copy_package_licenses: the NuGet packages folder was not found: {args.packages} (set NUGET_PACKAGES)", file=sys.stderr)
        return 1

    def find_deps(name):
        if args.app and os.path.isfile(os.path.join(args.app, name)):
            return os.path.join(args.app, name)
        # the build's own output under --artifacts (bin/<project>/release_<rid>/), the self-contained one: the file that names the runtime pack
        found = []
        for root, _dirs, files in os.walk(args.artifacts):
            if name in files and os.sep + "bin" + os.sep in root + os.sep:
                found.append(os.path.join(root, name))
        for candidate in sorted(found):
            with open(candidate, encoding="utf-8") as f:
                if "runtimepack" in f.read():
                    return candidate
        raise SystemExit(f"copy_package_licenses: {name} was not found in {args.app or '-'} or under {args.artifacts}")

    folders = {}
    for deps in DEPS:
        with open(find_deps(deps), encoding="utf-8") as f:
            libraries = json.load(f).get("libraries", {})
        for name, value in libraries.items():
            if value.get("type") not in ("package", "runtimepack"):
                continue
            package_id, version = name.split("/")
            if value["type"] == "runtimepack":
                package_id = re.sub(r"^runtimepack\.", "", package_id)
            folders[os.path.join(args.packages, package_id.lower(), version.lower())] = f"{package_id}/{version}"

    for root, _dirs, files in os.walk(args.artifacts):
        if "project.assets.json" not in files:
            continue
        with open(os.path.join(root, "project.assets.json"), encoding="utf-8") as f:
            libraries = json.load(f).get("libraries", {})
        for name, value in libraries.items():
            if value.get("type") != "package":
                continue
            folder = os.path.join(args.packages, *value["path"].split("/"))
            if os.path.isdir(os.path.join(folder, "content")):
                folders[folder] = name

    copied = 0
    problems = []
    for folder, name in sorted(folders.items(), key=lambda kv: kv[1]):
        if not os.path.isdir(folder):
            problems.append(f"Package folder not found: {folder}")
            continue
        files = [f for f in sorted(os.listdir(folder)) if os.path.isfile(os.path.join(folder, f)) and WANTED.match(f)]
        if not files and name not in NO_LICENSE_FILE:
            problems.append(f"{name} has no license file in {folder}. Use a version that ships one, or list it in NO_LICENSE_FILE with where its license text comes from.")
            continue
        for f in files:
            target = os.path.join(args.out, name.replace("/", "-"))
            os.makedirs(target, exist_ok=True)
            shutil.copyfile(os.path.join(folder, f), os.path.join(target, f))
            copied += 1

    if args.fonts_license:
        target = os.path.join(args.out, "Barlow-fonts")
        os.makedirs(target, exist_ok=True)
        shutil.copyfile(args.fonts_license, os.path.join(target, "OFL.txt"))
        copied += 1

    if problems:
        for p in problems:
            print("copy_package_licenses: " + p, file=sys.stderr)
        return 1
    print(f"copy_package_licenses: {copied} license files for {len(folders)} packages into {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
