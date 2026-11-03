"""The MCP registry ownership proofs must name the server.json listing (offline, no network)."""
import json
from pathlib import Path
import re
import unittest

_ROOT = Path(__file__).resolve().parents[2]


def _listing_name():
    return json.loads((_ROOT / "server.json").read_text(encoding="utf-8"))["name"]


class RegistryOwnershipTests(unittest.TestCase):
    def test_nuget_readme_carries_mcp_name(self):
        # The registry reads the packed README (docs/NUGET.md, see the csproj) for `mcp-name: <name>`
        # followed by a boundary (whitespace, a tag, or `-->`), so a longer name must not pass.
        readme = (_ROOT / "docs" / "NUGET.md").read_text(encoding="utf-8")
        token = r"mcp-name: " + re.escape(_listing_name()) + r"(?=\s|<|-->|\Z)"
        self.assertRegex(readme, token)

    def test_docker_label_names_the_listing(self):
        dockerfile = (_ROOT / "Dockerfile").read_text(encoding="utf-8")
        match = re.search(r'io\.modelcontextprotocol\.server\.name="([^"]+)"', dockerfile)
        self.assertIsNotNone(match, "Dockerfile lost its io.modelcontextprotocol.server.name label")
        self.assertEqual(match.group(1), _listing_name())


if __name__ == "__main__":
    unittest.main()
