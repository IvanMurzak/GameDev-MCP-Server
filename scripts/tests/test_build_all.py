"""Cases for build/build_all.py: arguments, the dotnet publish line, exit codes and the zip layout (hermetic: a fake dotnet)."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
import zipfile
from unittest import mock

_SCRIPT = Path(__file__).resolve().parents[2] / "build" / "build_all.py"
_spec = importlib.util.spec_from_file_location("build_all", _SCRIPT)
build_all = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(build_all)


def _fake_publish(fail_rids=()):
    """A subprocess.run stand-in that 'publishes' an exe + a config file into the -o folder."""
    calls = []

    def run(cmd):
        calls.append(cmd)
        rid, out = cmd[cmd.index("-r") + 1], Path(cmd[cmd.index("-o") + 1])
        exe = out / ("gamedev-mcp-server.exe" if rid.startswith("win-") else "gamedev-mcp-server")
        exe.write_bytes(b"binary")
        exe.chmod(0o755)
        (out / "appsettings.json").write_text("{}")
        return mock.Mock(returncode=1 if rid in fail_rids else 0)

    return run, calls


class BuildAllTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        patcher = mock.patch.object(build_all, "REPO_ROOT", self.root)
        patcher.start()
        self.addCleanup(patcher.stop)

    def main(self, *argv, fail_rids=()):
        run, calls = _fake_publish(fail_rids)
        out = io.StringIO()
        with mock.patch.object(build_all.subprocess, "run", run), contextlib.redirect_stdout(out), \
                contextlib.redirect_stderr(out):
            try:
                code = build_all.main(list(argv))
            except SystemExit as exc:
                code = exc.code
        return code, calls, out.getvalue()

    def entries(self, rid):
        with zipfile.ZipFile(self.root / "publish" / ("gamedev-mcp-server-%s.zip" % rid)) as zf:
            return {i.filename: i.external_attr >> 16 for i in zf.infolist()}

    def test_defaults_build_all_seven_runtimes_in_release(self):
        code, calls, _ = self.main("--no-zip")
        self.assertEqual(code, 0)
        self.assertEqual([c[c.index("-r") + 1] for c in calls], list(build_all.RUNTIMES))
        self.assertEqual(calls[0], [
            "dotnet", "publish", str(self.root / build_all.DEFAULT_PROJECT), "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-p:PublishSingleFile=true", "-o", str(self.root / "publish" / "win-x64")])
        self.assertEqual(list((self.root / "publish").glob("*.zip")), [])

    def test_positional_and_comma_separated_arguments(self):
        args = build_all.parse_args(["Debug", "other.csproj", "osx-arm64,linux-x64", "osx-arm64", "win-x86"])
        self.assertEqual(args.configuration, "Debug")
        self.assertEqual(args.project, self.root / "other.csproj")
        self.assertEqual(args.runtimes, ["osx-arm64", "linux-x64", "win-x86"])
        self.assertEqual(build_all.parse_args(["-c", "Debug"]).configuration, "Debug")
        self.assertEqual(build_all.parse_args(["/abs/x.csproj"]).project, Path("/abs/x.csproj"))

    def test_bad_arguments_exit_1(self):
        for argv in (["win-x65"], ["--no-zip", "--zip-only"], ["--bogus"]):
            with self.subTest(argv=argv):
                code, calls, out = self.main(*argv)
                self.assertEqual(code, 1)
                self.assertEqual(calls, [])

    def test_zip_layouts_flat_for_windows_rid_folder_with_exec_bit_otherwise(self):
        code, _, _ = self.main("win-x64", "linux-x64")
        self.assertEqual(code, 0)
        self.assertEqual(set(self.entries("win-x64")), {"gamedev-mcp-server.exe", "appsettings.json"})
        linux = self.entries("linux-x64")
        self.assertEqual(set(linux), {"linux-x64/", "linux-x64/gamedev-mcp-server", "linux-x64/appsettings.json"})
        self.assertTrue(stat.S_ISDIR(linux["linux-x64/"]))
        self.assertTrue(linux["linux-x64/gamedev-mcp-server"] & stat.S_IXUSR)

    def test_build_failure_exits_1_and_skips_zips(self):
        code, calls, out = self.main("linux-x64", "osx-x64", fail_rids=("linux-x64",))
        self.assertEqual(code, 1)
        self.assertEqual(len(calls), 2)  # one failure does not stop the remaining runtimes
        self.assertIn("Failed: 1", out)
        self.assertEqual(list((self.root / "publish").glob("*.zip")), [])

    def test_build_cleans_a_previous_publish_folder(self):
        stale = self.root / "publish" / "stale.txt"
        stale.parent.mkdir()
        stale.write_text("old")
        self.assertEqual(self.main("--no-zip", "osx-arm64")[0], 0)
        self.assertFalse(stale.exists())

    def test_zip_only_zips_existing_outputs_without_building(self):
        rid_dir = self.root / "publish" / "osx-arm64"
        rid_dir.mkdir(parents=True)
        (rid_dir / "gamedev-mcp-server").write_bytes(b"signed")
        (self.root / "publish" / "gamedev-mcp-server-osx-arm64.zip").write_bytes(b"stale")
        code, calls, _ = self.main("--zip-only", "osx-arm64")
        self.assertEqual(code, 0)
        self.assertEqual(calls, [])
        self.assertEqual(set(self.entries("osx-arm64")), {"osx-arm64/", "osx-arm64/gamedev-mcp-server"})

    def test_zip_only_missing_folder_exits_1(self):
        (self.root / "publish").mkdir()
        code, calls, out = self.main("--zip-only", "win-x64")
        self.assertEqual(code, 1)
        self.assertIn("directory not found", out)


if __name__ == "__main__":
    unittest.main()
