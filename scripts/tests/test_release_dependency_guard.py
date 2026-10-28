"""Cases for scripts/release_dependency_guard.py: which pins a server release may ship (hermetic: a fake nuget.org)."""
import importlib.util
import json
import sys
from pathlib import Path
import tempfile
import textwrap
import unittest

_SCRIPT = Path(__file__).resolve().parents[1] / "release_dependency_guard.py"
_spec = importlib.util.spec_from_file_location("release_dependency_guard", _SCRIPT)
guard = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = guard  # dataclasses resolve annotations through sys.modules
_spec.loader.exec_module(guard)

HOST = "com.IvanMurzak.GameDev.MCP.Server.csproj"
PUBLISHED = {
    "com.ivanmurzak.reflectornet": ["5.4.0", "5.5.0"],
    "com.ivanmurzak.mcpplugin": ["8.13.0", "8.13.1"],
    "com.ivanmurzak.mcpplugin.server": ["8.13.0", "8.13.1"],
    "r3": ["1.3.0"],
}

CLEAN_DOCKERFILE = """\
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["com.IvanMurzak.GameDev.MCP.Server.csproj", "."]
RUN dotnet restore "com.IvanMurzak.GameDev.MCP.Server.csproj"
COPY . .
RUN dotnet publish "com.IvanMurzak.GameDev.MCP.Server.csproj" -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
COPY --from=build --chown=1654:1654 /app/publish .
"""

# The shape of main while McpPlugin is integrated unreleased (8.14.0-dev.<sha> from prerelease-packages/).
PRERELEASE_DOCKERFILE = CLEAN_DOCKERFILE.replace(
    'COPY ["com.IvanMurzak.GameDev.MCP.Server.csproj", "."]',
    'COPY ["com.IvanMurzak.GameDev.MCP.Server.csproj", "Directory.Build.props", "."]\n'
    'COPY ["prerelease-packages/", "prerelease-packages/"]')

FEED_PROPS = """\
<Project>
  <PropertyGroup>
    <RestoreAdditionalProjectSources>$(RestoreAdditionalProjectSources);$(MSBuildThisFileDirectory)prerelease-packages</RestoreAdditionalProjectSources>
  </PropertyGroup>
</Project>
"""


def fake_registry(published=PUBLISHED, fail=False):
    def fetch(url):
        if fail:
            raise guard.RegistryError("offline")
        package = url.split("/")[-2]
        versions = published.get(package)
        return None if versions is None else json.dumps({"versions": versions}).encode()
    return guard.NuGetRegistry(fetch)


def csproj(mcp_plugin="8.13.1"):
    return textwrap.dedent(f"""\
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <Version>9.2.17</Version>
            <UseLocalMcpPlugin Condition="'$(UseLocalMcpPlugin)' == ''">false</UseLocalMcpPlugin>
          </PropertyGroup>
          <ItemGroup Condition="'$(UseLocalMcpPlugin)' != 'true'">
            <PackageReference Include="com.IvanMurzak.ReflectorNet" Version="5.5.0" />
            <PackageReference Include="com.IvanMurzak.McpPlugin.Server" Version="{mcp_plugin}" />
            <PackageReference Include="com.IvanMurzak.McpPlugin" Version="{mcp_plugin}" />
          </ItemGroup>
          <ItemGroup Condition="'$(UseLocalMcpPlugin)' == 'true'">
            <ProjectReference Include="./../MCP-Plugin-dotnet/McpPlugin.Server/McpPlugin.Server.csproj" />
          </ItemGroup>
          <ItemGroup>
            <PackageReference Include="R3" Version="1.3.0" />
          </ItemGroup>
        </Project>
        """)


class Fixture:
    """A minimal GameDev-MCP-Server tree: the host csproj, the Dockerfile, the (unshipped) test project."""

    def __init__(self, root: Path, mcp_plugin="8.13.1", feed=False):
        self.root = root
        self.write(HOST, csproj(mcp_plugin))
        self.write("Dockerfile", PRERELEASE_DOCKERFILE if feed else CLEAN_DOCKERFILE)
        # The test project ships nothing: its pins and local feeds must not trip the guard.
        self.write("tests/GameDev.MCP.Server.Tests/GameDev.MCP.Server.Tests.csproj",
                   '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
                   '<PackageReference Include="xunit" Version="3.0.0-pre.1" /></ItemGroup></Project>')
        self.write("tests/GameDev.MCP.Server.Tests/nuget.config",
                   '<configuration><packageSources><add key="local" value="../local-nuget" /></packageSources></configuration>')
        if feed:
            self.write("Directory.Build.props", FEED_PROPS)
            self.write(f"prerelease-packages/com.IvanMurzak.McpPlugin.Server.{mcp_plugin}.nupkg", "")

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def problems(self, registry=None):
        _, problems = guard.run(self.root, registry or fake_registry())
        return [(guard.rel(p.file, self.root), p.message) for p in problems]

    def test_published_pins_pass(self):
        Fixture(self.root)
        pins, problems = guard.run(self.root, fake_registry())
        self.assertEqual([], problems)
        self.assertEqual({("com.IvanMurzak.ReflectorNet", "5.5.0"), ("com.IvanMurzak.McpPlugin.Server", "8.13.1"),
                          ("com.IvanMurzak.McpPlugin", "8.13.1"), ("R3", "1.3.0")},
                         {(p.package, p.version) for p in pins})

    def test_prerelease_pins_feed_and_docker_copy_fail(self):
        Fixture(self.root, mcp_plugin="8.14.0-dev.36320da", feed=True)
        problems = self.problems()
        self.assertIn((HOST, "com.IvanMurzak.McpPlugin.Server 8.14.0-dev.36320da is a pre-release version"), problems)
        self.assertIn((HOST, "com.IvanMurzak.McpPlugin 8.14.0-dev.36320da is a pre-release version"), problems)
        self.assertTrue(any(f == "Directory.Build.props" and "local NuGet feed" in m and "prerelease-packages" in m
                            for f, m in problems))
        self.assertTrue(any(f == "Dockerfile" and "COPY brings the local NuGet feed 'prerelease-packages/'" in m
                            for f, m in problems))
        self.assertFalse(any(f.startswith("tests/") for f, _ in problems))
        self.assertEqual(4, len(problems))

    def test_unpublished_stable_version_and_unknown_package_fail(self):
        Fixture(self.root, mcp_plugin="8.99.0")
        self.assertIn((HOST, "com.IvanMurzak.McpPlugin.Server 8.99.0 is not published on nuget.org"), self.problems())
        unknown = self.problems(fake_registry({**PUBLISHED, "r3": None}))
        self.assertIn((HOST, "R3 is not a package on nuget.org"), unknown)

    def test_registry_outage_fails_closed(self):
        Fixture(self.root)
        calls = []
        registry = fake_registry(fail=True)
        fetch = registry._fetch
        registry._fetch = lambda url: calls.append(url) or fetch(url)
        problems = self.problems(registry)
        self.assertEqual(1, len(calls))  # one retry cycle in total, not one per package
        self.assertEqual(4, len(problems))
        self.assertTrue(all("cannot verify on nuget.org" in m for _, m in problems))

    def test_msbuild_property_versions_are_resolved(self):
        fixture = Fixture(self.root)
        fixture.write(HOST, textwrap.dedent("""\
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><McpVersion>$(McpBase)-dev.1</McpVersion><McpBase>8.14.0</McpBase></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><McpVersion>8.13.1</McpVersion></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="com.IvanMurzak.McpPlugin.Server" Version="$(McpVersion)" />
                <PackageReference Include="com.IvanMurzak.ReflectorNet"><Version>$(Missing)</Version></PackageReference>
              </ItemGroup>
            </Project>
            """))
        problems = self.problems()
        # A property set under different Conditions: every value it may take is checked.
        self.assertIn((HOST, "com.IvanMurzak.McpPlugin.Server 8.14.0-dev.1 is a pre-release version"), problems)
        self.assertIn((HOST, "com.IvanMurzak.ReflectorNet: cannot resolve version '$(Missing)'"), problems)
        self.assertEqual(2, len(problems))

    def test_version_property_from_directory_build_props(self):
        fixture = Fixture(self.root)
        fixture.write("Directory.Build.props",
                      "<Project><PropertyGroup><McpVersion>8.14.0-rc.2</McpVersion></PropertyGroup></Project>")
        fixture.write(HOST, csproj("$(McpVersion)"))
        problems = self.problems()
        self.assertIn((HOST, "com.IvanMurzak.McpPlugin 8.14.0-rc.2 is a pre-release version"), problems)

    def test_central_package_versions_and_nuget_config_feed(self):
        fixture = Fixture(self.root)
        fixture.write("Directory.Packages.props",
                      '<Project><ItemGroup><PackageVersion Include="R3" Version="[1.4.0-beta]" />'
                      '</ItemGroup></Project>')
        fixture.write("nuget.config", '<configuration><packageSources><clear />'
                      '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
                      '<add key="pre" value="prerelease-packages" /></packageSources></configuration>')
        problems = self.problems()
        self.assertEqual([("Directory.Packages.props", "R3 1.4.0-beta is a pre-release version"),
                          ("nuget.config", "package source 'pre' is the local feed 'prerelease-packages'")], problems)

    def test_restore_sources_property_with_url_passes(self):
        fixture = Fixture(self.root)
        fixture.write("Directory.Build.props", "<Project><PropertyGroup>"
                      "<RestoreSources>https://api.nuget.org/v3/index.json</RestoreSources></PropertyGroup></Project>")
        self.assertEqual([], self.problems())

    def test_dockerfile_local_sources_fail(self):
        fixture = Fixture(self.root)
        fixture.write("feed/x.1.0.0.nupkg", "")
        fixture.write("Dockerfile", CLEAN_DOCKERFILE + textwrap.dedent("""\
            COPY feed /feed
            ADD local/pkg.1.0.0.nupkg /pkgs/
            RUN dotnet restore app.csproj \\
                --source /feed
            RUN dotnet restore app.csproj -s=./pkgs
            RUN dotnet nuget add source "/pkgs" --name local
            RUN dotnet restore app.csproj --source https://api.nuget.org/v3/index.json
            # COPY prerelease-packages/ prerelease-packages/
            """))
        _, found = guard.run(self.root, fake_registry())
        problems = [(guard.rel(p.file, self.root), p.message) for p in found]
        self.assertEqual([
            ("Dockerfile", "COPY brings the local NuGet feed 'feed' into the image build "
                           "(a release must restore from nuget.org only)"),
            ("Dockerfile", "ADD brings the local NuGet feed 'local/pkg.1.0.0.nupkg' into the image build "
                           "(a release must restore from nuget.org only)"),
            ("Dockerfile", "dotnet restores from the local NuGet source '/feed'"),
            ("Dockerfile", "dotnet restores from the local NuGet source './pkgs'"),
            ("Dockerfile", "dotnet restores from the local NuGet source '/pkgs'"),
        ], problems)
        self.assertEqual([9, 10, 11, 13, 14], [p.line for p in found])

    def test_missing_shipped_files_fail(self):
        Fixture(self.root)
        self.root.joinpath("Dockerfile").unlink()
        self.assertEqual([("Dockerfile", "expected file is missing (update scripts/release_dependency_guard.py)")],
                         self.problems())
        self.root.joinpath(HOST).unlink()
        self.assertIn((HOST, "expected file is missing (update scripts/release_dependency_guard.py)"), self.problems())

    def test_no_pins_found_fails(self):
        fixture = Fixture(self.root)
        fixture.write(HOST, '<Project Sdk="Microsoft.NET.Sdk.Web"></Project>')
        self.assertEqual([(HOST, "no NuGet pins found (update scripts/release_dependency_guard.py)")], self.problems())


class DockerfileParsingTests(unittest.TestCase):
    def test_copy_sources(self):
        self.assertEqual(["a", "b"], guard.copy_sources('["a", "b", "."]'))
        self.assertEqual(["a", "b"], guard.copy_sources("--chown=1:1 a b /dst/"))
        self.assertEqual(["x"], guard.copy_sources('--chmod=644 ["x", "/y"]'))
        self.assertEqual([], guard.copy_sources("--from=build /app/publish ."))

    def test_logical_lines(self):
        self.assertEqual([(1, "RUN a    b"), (3, "COPY c d")], list(guard.logical_lines("RUN a \\\n  b\nCOPY c d\n")))


class VersionTests(unittest.TestCase):
    def test_prerelease(self):
        self.assertTrue(guard.is_prerelease("8.14.0-dev.36320da"))
        self.assertTrue(guard.is_prerelease("1.0.0-rc.1+build"))
        self.assertFalse(guard.is_prerelease("1.0.0+build-7"))
        self.assertFalse(guard.is_prerelease("8.13.1"))

    def test_normalize(self):
        self.assertEqual("1.0.0", guard.normalize_nuget_version("1.0"))
        self.assertEqual("1.0.0", guard.normalize_nuget_version("1.0.0.0"))
        self.assertEqual("1.2.3.4", guard.normalize_nuget_version("01.2.3.4"))
        self.assertEqual("1.0.0-rc.1", guard.normalize_nuget_version("1.0.0-RC.1+meta"))

    def test_ranges(self):
        self.assertEqual((["1.0.0"], None), guard.nuget_versions_in("[1.0.0]"))
        self.assertEqual((["1.0"], None), guard.nuget_versions_in("[1.0,2.0)"))  # exclusive upper bound need not exist
        self.assertEqual((["1.0", "2.0"], None), guard.nuget_versions_in("[1.0,2.0]"))
        self.assertEqual(([], "floating version '1.*' is not a pin"), guard.nuget_versions_in("1.*"))


if __name__ == "__main__":
    unittest.main()
