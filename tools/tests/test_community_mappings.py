import hashlib
import json
import pathlib
import sys
import tempfile
import unittest

TOOLS = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))

import community_mappings as cm  # noqa: E402

FIXTURES = json.loads((TOOLS.parent / "community" / "fixtures" / "proposals.json").read_text(encoding="utf-8"))
SAIKI = next(case["mapping"] for case in FIXTURES["valid"] if case["name"] == "season-saiki-final")
SERIES = next(case["mapping"] for case in FIXTURES["valid"] if case["name"] == "series-all-providers")


def dataset(*mappings, **extra):
    return {"schemaVersion": 2, "mappings": [dict(mapping) for mapping in mappings], **extra}


class SharedFixtures(unittest.TestCase):
    def test_accepts_every_valid_example(self):
        for case in FIXTURES["valid"]:
            with self.subTest(case["name"]):
                cm.validate_mapping(case["mapping"])

    def test_rejects_every_invalid_example(self):
        for case in FIXTURES["invalid"]:
            with self.subTest(case["name"]), self.assertRaises(ValueError):
                cm.validate_mapping(case["mapping"])

    def test_canonical_text_and_fingerprint_match_the_fixtures(self):
        for case in FIXTURES["valid"]:
            with self.subTest(case["name"]):
                text = cm.canonical(case["mapping"])
                self.assertEqual(case["canonical"], text)
                self.assertEqual(case["fingerprint"], hashlib.sha256(text.encode()).hexdigest()[:20])


class Dataset(unittest.TestCase):
    def test_a_season_and_its_series_can_point_to_different_cards(self):
        cm.validate(dataset(SERIES, SAIKI))

    def test_two_cards_for_the_same_season_layout_conflict(self):
        other = dict(SAIKI, animeClickId="99999")
        with self.assertRaises(ValueError):
            cm.validate(dataset(SAIKI, other))

    def test_a_different_episode_count_is_a_different_layout(self):
        # Another library may cut the same season differently; both layouts can coexist.
        cm.validate(dataset(SAIKI, dict(SAIKI, animeClickId="99999", episodeCount=1)))

    def test_duplicate_fields_are_rejected(self):
        with self.assertRaises(ValueError):
            cm.parse(b'{"schemaVersion": 2, "mappings": [], "mappings": []}')

    def test_only_a_plain_https_relay_is_accepted(self):
        cm.validate(dataset(relay="https://animeclick-community.example.workers.dev/v1/proposals"))
        for relay in ("http://example.org/v1", "https://user:pw@example.org/v1", "https://example.org/v1?x=1",
                      "https://example.org/v1#x", "https://example.org:8443/v1", "ftp://example.org", 42):
            with self.subTest(relay), self.assertRaises(ValueError):
                cm.validate(dataset(relay=relay))

    def test_unknown_top_level_fields_are_rejected(self):
        with self.assertRaises(ValueError):
            cm.validate(dataset(comment="x"))

    def test_legacy_file_carries_only_series_and_films(self):
        legacy = cm.legacy(dataset(SERIES, SAIKI, relay="https://example.org/v1"))
        self.assertEqual({"schemaVersion": 1, "mappings": [SERIES]}, legacy)
        cm.validate_legacy(legacy)

    def test_add_sorts_deduplicates_and_writes_both_files(self):
        with tempfile.TemporaryDirectory() as folder:
            source = pathlib.Path(folder) / "mappings-v2.json"
            target = pathlib.Path(folder) / "mappings.json"
            data = dataset()
            cm.add(data, SAIKI)
            cm.add(data, SERIES)
            cm.add(data, SERIES)
            cm.write(data, source, target)
            written = json.loads(source.read_text(encoding="utf-8"))
            self.assertEqual(["Season", "Series"], [entry["kind"] for entry in written["mappings"]])
            self.assertEqual([SERIES], json.loads(target.read_text(encoding="utf-8"))["mappings"])

    def test_repository_files_are_valid_and_in_sync(self):
        data = cm.validate(cm.load(cm.DATASET))
        self.assertEqual(cm.render(cm.legacy(data)), cm.LEGACY.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
