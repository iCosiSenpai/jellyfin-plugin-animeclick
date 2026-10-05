import pathlib
import re
import sys
import unittest

TOOLS = pathlib.Path(__file__).resolve().parents[1]
ROOT = TOOLS.parent
sys.path.insert(0, str(TOOLS))

import release_meta  # noqa: E402


class ReleaseMeta(unittest.TestCase):
    def test_the_shipped_name_is_the_one_the_plugin_gives_itself(self):
        # Jellyfin groups versions by this name: it must match Plugin.Name, not the catalog's package name.
        plugin = (ROOT / "Plugin.cs").read_text(encoding="utf-8")
        meta = release_meta.manifest("note")
        self.assertEqual(re.search(r'public override string Name => "([^"]+)"', plugin).group(1), meta["name"])
        self.assertEqual("1bd83d2a-f1a1-4ee5-a09b-22f4ed1f0a11", meta["guid"])
        self.assertEqual("12.0.0.0", meta["targetAbi"])
        self.assertRegex(meta["version"], r"^\d+\.\d+\.\d+\.\d+$")
        self.assertEqual(("Active", True, [], "note"), (meta["status"], meta["autoUpdate"], meta["assemblies"], meta["changelog"]))


if __name__ == "__main__":
    unittest.main()
