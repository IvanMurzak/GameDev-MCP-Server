#!/usr/bin/env python3
"""
Release dependency guard: refuse a release that would ship a dependency users cannot restore.

Between test waves GameDev-MCP-Server's `main` may build against UNRELEASED McpPlugin packages
(e.g. `com.IvanMurzak.McpPlugin.Server 8.14.0-dev.<sha>`) restored from the committed
`prerelease-packages/` feed that `Directory.Build.props` adds to every restore (and that the
Dockerfile copies into the image build). That is fine for CI, but a release cut in that state ships
executables, a NuGet global tool and a Docker image built from unreleased library code, and a NuGet
tool whose dependencies nuget.org cannot satisfy. This guard runs as the FIRST step of
`release.yml` and fails the run before anything is built or published while any such pin is left.

What it checks (everything the release builds or ships: the 7 executables, the NuGet tool and the
Docker image all come from `com.IvanMurzak.GameDev.MCP.Server.csproj`):
  * every NuGet pin of that project and the `Directory.Build.props` / `Directory.Packages.props` /
    `.props` / `.targets` / `.nuspec` files that feed it, MSBuild `$(Property)` versions resolved:
    a pre-release version (any `-` suffix) or a version not listed on nuget.org fails;
  * a local (non-URL) NuGet restore source: `RestoreSources` / `RestoreAdditionalProjectSources` /
    `RestoreFallbackFolders` in those files, or a `nuget.config` package source above the project;
  * the `Dockerfile`: a `COPY`/`ADD` of a local feed (a `.nupkg`, a folder holding one, or
    `prerelease-packages`) and a `dotnet ... --source` / `dotnet nuget add source` with a local path.
nuget.org being unreachable fails the run too (fail closed). The test project ships nothing and is
not scanned.

Usage:
    python scripts/release_dependency_guard.py [--root <repo>]
Exit 0 = every pin is a published release; exit 1 = problems listed (file:line: message).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Optional

ROOT = Path(__file__).resolve().parents[1]
NUGET_INDEX_URL = "https://api.nuget.org/v3-flatcontainer/{id}/index.json"

# MSBuild item types that pin a NuGet package version.
PACKAGE_ITEMS = {"PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload"}
# MSBuild properties that add NuGet restore sources.
RESTORE_SOURCE_PROPERTIES = {"RestoreSources", "RestoreAdditionalProjectSources", "RestoreFallbackFolders"}
SKIP_DIRS = {"bin", "obj", "node_modules", "prerelease-packages", "publish", "packages"}
PROPERTY_RE = re.compile(r"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")


@dataclass(frozen=True)
class Pin:
    """One dependency version as written in a shipped file."""

    package: str
    version: str
    file: Path
    line: int


@dataclass(frozen=True)
class Problem:
    file: Path
    line: int
    message: str


class RegistryError(Exception):
    """nuget.org could not be queried (network / HTTP error other than 404)."""


class NuGetRegistry:
    """Listed versions per package id from the nuget.org flat container (cached; 404 = unknown package)."""

    def __init__(self, fetch: Optional[Callable[[str], Optional[bytes]]] = None) -> None:
        self._fetch = fetch or _http_get
        self._cache: dict[str, Optional[set[str]] | RegistryError] = {}

    def versions(self, package: str) -> Optional[set[str]]:
        key = package.lower()
        if key not in self._cache:
            try:
                body = self._fetch(NUGET_INDEX_URL.format(id=key))
                self._cache[key] = None if body is None else {
                    normalize_nuget_version(v) for v in json.loads(body.decode("utf-8")).get("versions", [])
                }
            except RegistryError as error:  # remembered: one retry cycle per package, not per pin
                self._cache[key] = error
        cached = self._cache[key]
        if isinstance(cached, RegistryError):
            raise cached
        return cached


def _http_get(url: str) -> Optional[bytes]:
    """GET with retries; None on 404; RegistryError when nuget.org stays unreachable."""
    last: Exception | None = None
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            last = error
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            last = error
        if attempt < 2:
            time.sleep(2 ** attempt)
    raise RegistryError(f"{url}: {last}")


# --- versions ---------------------------------------------------------------------------------


def is_prerelease(version: str) -> bool:
    """SemVer/NuGet pre-release: any `-` suffix on the version (build metadata `+x` alone is not)."""
    return "-" in version.split("+", 1)[0]


def normalize_nuget_version(version: str) -> str:
    """NuGet's normalized form: `1.0` -> `1.0.0`, `1.0.0.0` -> `1.0.0`, leading zeros dropped, metadata dropped."""
    core, _, prerelease = version.split("+", 1)[0].strip().partition("-")
    parts = [str(int(p)) if p.isdigit() else p for p in core.split(".")]
    while len(parts) < 3:
        parts.append("0")
    if len(parts) == 4 and parts[3] == "0":
        parts.pop()
    normalized = ".".join(parts)
    return (normalized + "-" + prerelease if prerelease else normalized).lower()


def nuget_versions_in(spec: str) -> tuple[list[str], Optional[str]]:
    """The concrete versions named by a NuGet version spec (`1.2.3`, `[1.2.3]`, `[1.0,2.0)`), or an error."""
    spec = spec.strip()
    if not spec:
        return [], "empty version"
    if "*" in spec:
        return [], f"floating version '{spec}' is not a pin"
    if spec[0] in "[(":
        if spec[-1] not in "])":
            return [], f"malformed version range '{spec}'"
        lower, _, upper = (b.strip() for b in spec[1:-1].partition(","))
        if "," not in spec:  # `[1.2.3]`: exactly that version
            return [lower], None
        # The lower bound is what restore picks; an exclusive upper bound need not exist (yet).
        return [b for b in (lower, upper if spec[-1] == "]" else "") if b], None
    return [spec], None


# --- MSBuild ----------------------------------------------------------------------------------


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def line_of(text: str, *needles: str) -> int:
    """1-based line of the first line containing every needle (0 when none does)."""
    for number, line in enumerate(text.splitlines(), 1):
        if all(n in line for n in needles):
            return number
    return 0


def rel(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def ancestors(start: Path, root: Path) -> list[Path]:
    """`start` and its parents up to and including `root`, outermost first."""
    root = root.resolve()
    chain = []
    current = start.resolve()
    while True:
        chain.append(current)
        if current == root or current.parent == current:
            break
        current = current.parent
    return list(reversed(chain))


def msbuild_chain(project: Path, root: Path) -> list[Path]:
    """The `Directory.*.props/targets` above a project (outermost first), the project, and the
    `.props`/`.targets`/`.nuspec` files packed from its folder."""
    files: list[Path] = []
    for directory in ancestors(project.parent, root):
        for name in ("Directory.Build.props", "Directory.Packages.props", "Directory.Build.targets"):
            if (directory / name).is_file():
                files.append(directory / name)
    files.append(project)
    for path in walk(project.parent):
        if path.suffix in (".props", ".targets", ".nuspec") and path not in files:
            files.append(path)
    return files


def walk(directory: Path) -> Iterable[Path]:
    """Files of a project's folder, not descending into build output or another project's folder."""
    for entry in sorted(directory.iterdir()):
        if entry.is_dir():
            if entry.name not in SKIP_DIRS and not entry.name.startswith(".") and not any(entry.glob("*.csproj")):
                yield from walk(entry)
        else:
            yield entry


def read_properties(files: list[Path]) -> dict[str, list[str]]:
    """Every static `<PropertyGroup>` definition over the chain (Conditions are not evaluated, so a
    property set more than once keeps every value it may take)."""
    properties: dict[str, list[str]] = {}
    for path in files:
        try:
            tree = ET.parse(path)
        except ET.ParseError:
            continue
        for group in tree.getroot().iter():
            if _local(group.tag) != "PropertyGroup":
                continue
            for prop in group:
                if isinstance(prop.tag, str) and prop.text is not None:
                    values = properties.setdefault(_local(prop.tag), [])
                    if prop.text.strip() not in values:
                        values.append(prop.text.strip())
    return properties


def resolve(value: str, properties: dict[str, list[str]], file: Path) -> list[str]:
    """Every expansion of the `$(Name)`s in `value` over the static definitions; an unknown name stays
    as `$(Name)` (the caller reports it)."""
    builtins = {"MSBuildThisFileDirectory": str(file.parent) + os.sep, "MSBuildProjectDirectory": str(file.parent)}
    results: set[str] = set()
    pending = [(value, 0)]
    while pending:
        current, depth = pending.pop()
        match = PROPERTY_RE.search(current)
        name = match.group(1) if match else ""
        options = [builtins[name]] if name in builtins else properties.get(name)
        if not match or not options or depth >= 16:
            results.add(current)
            continue
        pending += [(current[:match.start()] + option + current[match.end():], depth + 1) for option in options]
    return sorted(results)


def is_url(source: str) -> bool:
    return re.match(r"^[a-z][a-z0-9+.-]*://", source, re.IGNORECASE) is not None and not source.lower().startswith("file:")


def item_version(element: ET.Element) -> Optional[str]:
    """The version an item pins; `VersionOverride` wins over `Version` (central package management)."""
    for name in ("VersionOverride", "Version", "version"):
        if element.get(name) is not None:
            return element.get(name)
    for name in ("VersionOverride", "Version"):
        child = next((c for c in element if isinstance(c.tag, str) and _local(c.tag) == name), None)
        if child is not None and child.text:
            return child.text.strip()
    return None


def scan_msbuild(projects: list[Path], root: Path) -> tuple[list[Pin], list[Problem]]:
    pins: list[Pin] = []
    problems: list[Problem] = []
    for project in (p.resolve() for p in projects):
        chain = msbuild_chain(project, root)
        properties = read_properties(chain)
        for path in chain:  # a shared props file is read per project: its `$(X)` may differ per project
            text = path.read_text(encoding="utf-8-sig")
            try:
                document = ET.fromstring(text)
            except ET.ParseError as error:
                problems.append(Problem(path, 1, f"cannot parse MSBuild/nuspec XML: {error}"))
                continue
            for element in document.iter():
                if not isinstance(element.tag, str):
                    continue
                name = _local(element.tag)
                if name in PACKAGE_ITEMS or (name == "dependency" and path.suffix == ".nuspec"):
                    package = element.get("Include") or element.get("Update") or element.get("id")
                    raw = item_version(element)
                    if not package or raw is None:
                        continue  # a version-less reference takes its pin from a PackageVersion scanned here too
                    line = line_of(text, f'"{package}"', raw) or line_of(text, package, raw)
                    for version in resolve(raw, properties, path):
                        if PROPERTY_RE.search(version):
                            problems.append(Problem(path, line, f"{package}: cannot resolve version '{raw}'"))
                            continue
                        versions, error = nuget_versions_in(version)
                        if error:
                            problems.append(Problem(path, line, f"{package}: {error}"))
                        pins.extend(Pin(package, v, path, line) for v in versions)
                elif name in RESTORE_SOURCE_PROPERTIES and element.text:
                    for entry in element.text.split(";"):
                        entry = entry.strip()
                        if not entry or entry == f"$({name})":
                            continue
                        if not all(is_url(source) for source in resolve(entry, properties, path)):
                            problems.append(Problem(path, line_of(text, f"<{name}"),
                                                    f"{name} adds the local NuGet feed '{entry}' "
                                                    f"(a release must restore from nuget.org only; delete the "
                                                    f"pre-release feed when the pins return to published versions)"))
    return pins, problems


def scan_nuget_configs(projects: list[Path], root: Path) -> list[Problem]:
    problems: list[Problem] = []
    directories = {d for project in projects for d in ancestors(project.parent, root)}
    for directory in sorted(directories):
        for entry in directory.iterdir():
            if entry.is_file() and entry.name.lower() == "nuget.config":
                text = entry.read_text(encoding="utf-8-sig")
                try:
                    tree = ET.fromstring(text)
                except ET.ParseError as error:
                    problems.append(Problem(entry, 1, f"cannot parse nuget.config: {error}"))
                    continue
                for sources in tree.iter("packageSources"):
                    for add in sources.iter("add"):
                        value = add.get("value", "")
                        if value and not is_url(value):
                            problems.append(Problem(entry, line_of(text, value),
                                                    f"package source '{add.get('key')}' is the local feed '{value}'"))
    return problems




def check_pins(pins: list[Pin], registry: NuGetRegistry) -> list[Problem]:
    problems: list[Problem] = []
    for pin in pins:
        if is_prerelease(pin.version):
            problems.append(Problem(pin.file, pin.line, f"{pin.package} {pin.version} is a pre-release version"))
            continue
        try:
            listed = registry.versions(pin.package)
        except RegistryError as error:
            problems.append(Problem(pin.file, pin.line, f"{pin.package} {pin.version}: cannot verify on nuget.org ({error})"))
            continue
        if listed is None:
            problems.append(Problem(pin.file, pin.line, f"{pin.package} is not a package on nuget.org"))
        elif normalize_nuget_version(pin.version) not in listed:
            problems.append(Problem(pin.file, pin.line, f"{pin.package} {pin.version} is not published on nuget.org"))
    return problems


def require(path: Path, problems: list[Problem]) -> bool:
    """A shipped file the guard expects; missing = the guard is out of date, which must fail too."""
    if path.is_file():
        return True
    problems.append(Problem(path, 1, "expected file is missing (update scripts/release_dependency_guard.py)"))
    return False


# --- Dockerfile -------------------------------------------------------------------------------

_ARG = r"(\"[^\"]*\"|'[^']*'|\S+)"
DOTNET_SOURCE_RE = re.compile(r"\bdotnet\b.*?\s(?:--source|-s)(?:\s+|=)" + _ARG, re.IGNORECASE)
NUGET_ADD_SOURCE_RE = re.compile(r"\bdotnet\s+nuget\s+add\s+source\s+" + _ARG, re.IGNORECASE)
COPY_RE = re.compile(r"^\s*(COPY|ADD)\s+(.*)$", re.IGNORECASE)


def logical_lines(text: str) -> Iterable[tuple[int, str]]:
    """Dockerfile instructions with backslash continuations joined, each with its first line's number."""
    start, parts = 0, []
    for number, line in enumerate(text.splitlines(), 1):
        if not parts:
            start = number
        stripped = line.rstrip()
        if stripped.endswith("\\"):
            parts.append(stripped[:-1])
            continue
        parts.append(stripped)
        yield start, " ".join(parts)
        parts = []
    if parts:
        yield start, " ".join(parts)


def copy_sources(arguments: str) -> list[str]:
    """The build-context sources of a COPY/ADD (flags dropped; `--from=` copies from a stage, not the context)."""
    tokens = arguments.split()
    flags = [t for t in tokens if t.startswith("--")]
    if any(f.startswith("--from") for f in flags):
        return []
    rest = " ".join(t for t in tokens if not t.startswith("--"))
    if rest.startswith("["):
        try:
            paths = [str(t) for t in json.loads(rest)]
        except json.JSONDecodeError:
            return []
    else:
        paths = rest.split()
    return paths[:-1]


def is_local_feed(source: str, root: Path) -> bool:
    name = source.strip().rstrip("/\\")
    if name.lower().endswith(".nupkg") or Path(name).name.lower() == "prerelease-packages":
        return True
    if name in ("", ".") or any(c in name for c in "*?["):
        return False  # the whole context: a feed folder in it only matters if a restore source names it
    path = root / name
    return path.is_dir() and any(path.glob("*.nupkg"))


def scan_dockerfile(path: Path, root: Path) -> list[Problem]:
    problems: list[Problem] = []
    for line, instruction in logical_lines(path.read_text(encoding="utf-8-sig")):
        if instruction.lstrip().startswith("#"):
            continue
        copy = COPY_RE.match(instruction)
        if copy:
            for source in copy_sources(copy.group(2)):
                if is_local_feed(source, root):
                    problems.append(Problem(path, line, f"{copy.group(1).upper()} brings the local NuGet feed "
                                                        f"'{source}' into the image build (a release must restore "
                                                        f"from nuget.org only)"))
        for pattern in (DOTNET_SOURCE_RE, NUGET_ADD_SOURCE_RE):
            for match in pattern.finditer(instruction):
                source = match.group(1).strip("\"'")
                if not is_url(source):
                    problems.append(Problem(path, line, f"dotnet restores from the local NuGet source '{source}'"))
    return problems


# --- this repository --------------------------------------------------------------------------

HOST_PROJECT = "com.IvanMurzak.GameDev.MCP.Server.csproj"


def collect(root: Path) -> tuple[list[Pin], list[Problem]]:
    """Every dependency pin and restore source the server release (executables, NuGet tool, Docker image) uses."""
    problems: list[Problem] = []
    host = root / HOST_PROJECT
    projects = [host] if require(host, problems) else []
    pins, msbuild_problems = scan_msbuild(projects, root)
    problems += msbuild_problems + scan_nuget_configs(projects, root)
    dockerfile = root / "Dockerfile"
    if require(dockerfile, problems):
        problems += scan_dockerfile(dockerfile, root)
    if projects and not pins:
        problems.append(Problem(host, 1, "no NuGet pins found (update scripts/release_dependency_guard.py)"))
    return pins, problems


def run(root: Path, registry: NuGetRegistry) -> tuple[list[Pin], list[Problem]]:
    pins, problems = collect(root)
    pins = list(dict.fromkeys(pins))
    problems += check_pins(pins, registry)
    unique = sorted(set(problems), key=lambda p: (rel(p.file, root), p.line, p.message))
    return pins, unique


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("--root", type=Path, default=ROOT, help="repository root (default: this checkout)")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    pins, problems = run(root, NuGetRegistry())
    files = sorted({rel(p.file, root) for p in pins})
    print(f"Release dependency guard: {len(pins)} pins in {len(files)} shipped files")
    for name in files:
        print(f"  {name}")
    if not problems:
        print("OK: every dependency pin is a published release.")
        return 0
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    print(f"\nFAIL: {len(problems)} problem(s); this release would ship dependencies users cannot restore:")
    for problem in problems:
        location, line = rel(problem.file, root), problem.line or 1
        print(f"  {location}:{line}: {problem.message}")
        if annotate:
            print(f"::error file={location},line={line}::{problem.message}")
    print("\nSwitch every pin back to a published release, delete prerelease-packages/ and Directory.Build.props "
          "and drop their Dockerfile COPY lines before releasing.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
