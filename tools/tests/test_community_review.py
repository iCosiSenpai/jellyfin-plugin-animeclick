import json
import pathlib
import sys
import tempfile
import unittest

TOOLS = pathlib.Path(__file__).resolve().parents[1]
ROOT = TOOLS.parent
sys.path.insert(0, str(TOOLS))

import community_mappings as cm  # noqa: E402
import community_review as cr  # noqa: E402

FIXTURES = json.loads((ROOT / "community" / "fixtures" / "proposals.json").read_text(encoding="utf-8"))
SAIKI = next(case for case in FIXTURES["valid"] if case["name"] == "season-saiki-final")
MOVIE_CARD = (ROOT / "AnimeClick.Plugin.Tests" / "Fixtures" / "anime-25493.html").read_text(encoding="utf-8")
SEASON_CARD = """<h1 itemprop="name">The Disastrous Life of Saiki K. 3</h1>
<dl><dt>Categoria</dt><dd><a href="#">Serie TV</a></dd><dt>Anno</dt><dd>2018</dd><dt>Episodi</dt><dd>2</dd></dl>"""


def issue(mapping, fingerprint=None):
    fingerprint = fingerprint or cr.fingerprint(mapping)
    title = f"[Proposta] {mapping['kind']} · AnimeClick {mapping['animeClickId']} · mapping-{fingerprint}"
    return title, "Proposta.\n\n```json\n" + cm.canonical(mapping) + "\n```\n\nConferme: 3 installazioni\n"


def web(routes):
    calls = []

    def get(url, headers=None):
        calls.append(url)
        for prefix, answer in routes.items():
            if url.startswith(prefix):
                return answer
        return 0, ""
    get.calls = calls
    return get


EMPTY = {"schemaVersion": 2, "mappings": []}


class Reading(unittest.TestCase):
    def test_reads_the_proposal_the_plugin_and_the_relay_write(self):
        title, body = issue(SAIKI["mapping"])
        proposal = cr.read_proposal(title, body)
        self.assertEqual(SAIKI["fingerprint"], proposal.fingerprint)
        self.assertEqual(SAIKI["mapping"], proposal.mapping)

    def test_an_edited_body_no_longer_matches_the_title(self):
        title, _ = issue(SAIKI["mapping"])
        _, body = issue(dict(SAIKI["mapping"], animeClickId="99999"))
        with self.assertRaisesRegex(ValueError, "impronta"):
            cr.read_proposal(title, body)

    def test_extra_fields_in_the_block_are_rejected_even_with_a_matching_marker(self):
        mapping = dict(SAIKI["mapping"], note="ciao")
        title = f"[Proposta] Season · AnimeClick 26035 · mapping-{SAIKI['fingerprint']}"
        with self.assertRaises(ValueError):
            cr.read_proposal(title, "```json\n" + json.dumps(mapping) + "\n```")

    def test_a_real_card_is_parsed(self):
        card = cr.parse_card(MOVIE_CARD)
        self.assertEqual({"title": "Rascal Does Not Dream of a Dreaming Girl", "year": 2019, "category": "Film", "episodes": 1}, card)


class Checks(unittest.TestCase):
    def test_a_coherent_season_passes_and_points_to_the_approval_label(self):
        get = web({
            cr.ANIMECLICK + "26035": (200, SEASON_CARD),
            "https://api.themoviedb.org/3/tv/67676/season/3": (200, json.dumps({"episodes": [{}, {}]})),
            "https://api.themoviedb.org/3/tv/67676": (200, json.dumps({"name": "The Disastrous Life of Saiki K.", "first_air_date": "2016-07-04"})),
        })
        review, text = cr.run_check(*issue(SAIKI["mapping"]), EMPTY, get, tmdb_key="k")
        self.assertEqual("ok", review.verdict)
        self.assertIn("`approvato`", text)
        self.assertIn("stagione 3 con 2 episodi", text)

    def test_a_season_tmdb_numbers_differently_is_a_doubt_not_a_rejection(self):
        get = web({
            cr.ANIMECLICK + "26035": (200, SEASON_CARD),
            "https://api.themoviedb.org/3/tv/67676/season/3": (404, ""),
            "https://api.themoviedb.org/3/tv/67676": (200, json.dumps({"name": "Saiki", "first_air_date": "2016-07-04"})),
        })
        review, text = cr.run_check(*issue(SAIKI["mapping"]), EMPTY, get, tmdb_key="k")
        self.assertEqual("doubt", review.verdict)
        self.assertIn("non c’è con questa numerazione", text)

    def test_a_missing_card_or_a_missing_external_id_is_impossible(self):
        movie = {"kind": "Movie", "animeClickId": "25493", "providerIds": {"Tmdb": "572154"}}
        missing_card = web({cr.ANIMECLICK: (404, "")})
        self.assertEqual("impossible", cr.run_check(*issue(movie), EMPTY, missing_card)[0].verdict)
        missing_tmdb = web({cr.ANIMECLICK: (200, MOVIE_CARD), "https://api.themoviedb.org/": (404, "")})
        self.assertEqual("impossible", cr.run_check(*issue(movie), EMPTY, missing_tmdb, tmdb_key="k")[0].verdict)

    def test_a_film_linked_to_a_series_card_is_flagged(self):
        series = {"kind": "Series", "animeClickId": "25493", "providerIds": {"AniList": "1"}}
        review, text = cr.run_check(*issue(series), EMPTY, web({cr.ANIMECLICK: (200, MOVIE_CARD)}))
        self.assertEqual("doubt", review.verdict)
        self.assertIn("collegata a un film", text)

    def test_an_unreachable_site_or_a_missing_key_never_rejects(self):
        movie = {"kind": "Movie", "animeClickId": "25493", "providerIds": {"Tmdb": "572154"}}
        review, text = cr.run_check(*issue(movie), EMPTY, web({}))
        self.assertEqual("doubt", review.verdict)
        self.assertIn("manca la chiave TMDB", text)

    def test_conflicts_and_duplicates_are_recognised(self):
        approved = {"schemaVersion": 2, "mappings": [SAIKI["mapping"]]}
        review, _ = cr.run_check(*issue(SAIKI["mapping"]), approved, web({}))
        self.assertEqual("duplicate", review.verdict)
        other = dict(SAIKI["mapping"], animeClickId="99999")
        review, text = cr.run_check(*issue(other), approved, web({cr.ANIMECLICK: (200, SEASON_CARD)}))
        self.assertEqual("doubt", review.verdict)
        self.assertIn("conflitto", text)

    def test_a_malformed_issue_is_impossible(self):
        review, text = cr.run_check("Domanda generica", "ciao", EMPTY, web({}))
        self.assertEqual("impossible", review.verdict)
        self.assertIn("marcatore", text)


class Approval(unittest.TestCase):
    def test_approving_writes_both_files(self):
        with tempfile.TemporaryDirectory() as folder:
            source = pathlib.Path(folder) / "mappings-v2.json"
            legacy = pathlib.Path(folder) / "mappings.json"
            cm.write({"schemaVersion": 2, "mappings": []}, source, legacy)
            body = pathlib.Path(folder) / "body.md"
            title, text = issue(SAIKI["mapping"])
            body.write_text(text, encoding="utf-8")
            argv = sys.argv
            sys.argv = ["community_review.py", "approve", "--title", title, "--body-file", str(body),
                        "--dataset", str(source), "--legacy", str(legacy)]
            try:
                cr.main()
            finally:
                sys.argv = argv
            self.assertEqual([SAIKI["mapping"]], json.loads(source.read_text(encoding="utf-8"))["mappings"])
            self.assertEqual([], json.loads(legacy.read_text(encoding="utf-8"))["mappings"])


if __name__ == "__main__":
    unittest.main()
