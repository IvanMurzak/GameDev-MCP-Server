#!/usr/bin/env python3
"""
Build the self-contained single-file server executables for every RID, then zip each one.

One stdlib-only script (Python 3.9+) for every OS: it replaces `build-all.sh` and `build-all.ps1`.
The project and `publish/` are anchored to the repo root (one level up), so any cwd works.

Per RID: `dotnet publish <csproj> -c <config> -r <rid> --self-contained true
-p:PublishSingleFile=true -o publish/<rid>`, then `publish/gamedev-mcp-server-<rid>.zip`.
The zip layout is the one the GitHub Release has always shipped:
  * win-*      -> flat (the files at the zip root), as .NET `ZipFile.CreateFromDirectory` made it;
  * linux/osx  -> under a `<rid>/` folder with Unix modes kept (the exec bit), as `zip -r` made it.

Usage:
    python build/build_all.py [Debug|Release] [-c <config>] [*.csproj] [--no-zip | --zip-only] [rid ...]
  --no-zip    publish only (CI code-signs the executables before zipping);
  --zip-only  zip the existing publish/<rid> folders, no clean and no build (after signing).
Runtimes may be space- or comma-separated; none = all seven.
Exit 0 = every requested build and zip succeeded; exit 1 = bad argument or any failure.
"""
import argparse
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

RUNTIMES = ("win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
CONFIGURATIONS = ("Debug", "Release")
DEFAULT_PROJECT = "com.IvanMurzak.GameDev.MCP.Server.csproj"
REPO_ROOT = Path(__file__).resolve().parents[1]


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        print("%s: error: %s" % (self.prog, message), file=sys.stderr)
        print("Known runtimes: %s" % " ".join(RUNTIMES), file=sys.stderr)
        sys.exit(1)


def parse_args(argv):
    parser = _Parser(prog="build_all.py", description="Publish and zip the server for every RID.")
    parser.add_argument("items", nargs="*", metavar="Debug|Release|*.csproj|rid",
                        help="configuration, project file and runtimes, in any order")
    parser.add_argument("-c", "--configuration", help="build configuration (default Release)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--no-zip", action="store_true", help="publish only, skip the zip phase")
    mode.add_argument("--zip-only", action="store_true", help="zip existing publish/<rid> folders only")
    args = parser.parse_args(argv)

    configuration, project, runtimes = args.configuration, None, []
    for item in args.items:
        if item in CONFIGURATIONS:
            configuration = item
        elif item.endswith(".csproj"):
            project = item
        else:
            for rid in filter(None, item.split(",")):
                if rid not in RUNTIMES:
                    parser.error("unknown argument: %s" % rid)
                if rid not in runtimes:
                    runtimes.append(rid)
    args.configuration = configuration or "Release"
    args.project = REPO_ROOT / (project or DEFAULT_PROJECT)  # an absolute path stays as it is
    args.runtimes = runtimes or list(RUNTIMES)
    return args


def publish(project, configuration, rid, out_dir):
    cmd = ["dotnet", "publish", str(project), "-c", configuration, "-r", rid,
           "--self-contained", "true", "-p:PublishSingleFile=true", "-o", str(out_dir)]
    print("Running: %s" % " ".join(cmd), flush=True)
    try:
        return subprocess.run(cmd).returncode
    except OSError as exc:  # dotnet missing or not executable
        print("Error executing dotnet publish: %s" % exc, flush=True)
        return 1


def zip_runtime(publish_root, rid):
    """Write publish/gamedev-mcp-server-<rid>.zip from publish/<rid>/ and return its path."""
    src = publish_root / rid
    zip_path = publish_root / ("gamedev-mcp-server-%s.zip" % rid)
    prefix = "" if rid.startswith("win-") else rid + "/"
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        if prefix:
            zf.write(src, prefix)
        for root, dirs, files in os.walk(src):
            dirs.sort()
            for name in sorted(dirs) + sorted(files):
                path = Path(root) / name
                if path.is_dir() and not prefix:
                    continue  # CreateFromDirectory writes no entry for a non-empty folder
                # ZipFile.write keeps st_mode in the entry, so the Unix exec bit survives.
                zf.write(path, prefix + path.relative_to(src).as_posix())
    return zip_path


def run_zips(publish_root, runtimes):
    print("\nCreating zip archives for each runtime...")
    ok = failed = 0
    for rid in runtimes:
        if not (publish_root / rid).is_dir():
            print("Skipping %s - directory not found" % rid)
            failed += 1
            continue
        print("Creating zip for %s..." % rid)
        try:
            zip_path = zip_runtime(publish_root, rid)
        except OSError as exc:
            print("Failed to create gamedev-mcp-server-%s.zip: %s" % (rid, exc))
            failed += 1
            continue
        print("Successfully created %s" % zip_path.name)
        ok += 1

    print("\nZip Creation Summary:\nSuccess: %d\nFailed: %d" % (ok, failed))
    if failed:
        print("\n%d zip archive(s) failed to create. See errors above." % failed)
        return 1
    print("\nAll zip archives created successfully!\nZip files are located in: %s\nCreated files:" % publish_root)
    for zip_path in sorted(publish_root.glob("*.zip")):
        print("  %s (%.2f KB)" % (zip_path.name, zip_path.stat().st_size / 1024))
    return 0


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)
    publish_root = REPO_ROOT / "publish"
    if args.zip_only:
        return run_zips(publish_root, args.runtimes)

    print("Building self-contained executables...")
    if publish_root.exists():
        print("Cleaning existing publish folder...")
        try:
            shutil.rmtree(publish_root)
        except OSError as exc:
            print("Failed to clean publish folder: %s" % exc)
            return 1
    publish_root.mkdir(parents=True)

    print("Configuration: %s\nProject File: %s\nTarget runtimes: %s\n"
          % (args.configuration, args.project, " ".join(args.runtimes)))
    ok = failed = 0
    for rid in args.runtimes:
        print("Building for %s..." % rid, flush=True)
        out_dir = publish_root / rid
        out_dir.mkdir()
        code = publish(args.project, args.configuration, rid, out_dir)
        if code == 0:
            print("Successfully built %s -> %s\n" % (rid, out_dir))
            ok += 1
        else:
            print("Failed to build %s (exit code: %d)\n" % (rid, code))
            failed += 1

    print("Build Summary:\nSuccess: %d\nFailed: %d" % (ok, failed))
    if failed:
        print("\nSome builds failed. Check the output above. Partial outputs: %s" % publish_root)
        return 1
    print("\nAll builds completed successfully!\nExecutables are located in: %s/{runtime}/" % publish_root)
    if args.no_zip:
        print("\n--no-zip specified: skipping zip-archive phase (executables left unzipped).")
        return 0
    return run_zips(publish_root, args.runtimes)


if __name__ == "__main__":
    sys.exit(main())
