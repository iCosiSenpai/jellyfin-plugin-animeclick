import json
import pathlib
import sys
import unittest

TOOLS = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))

import community_intake as ci  # noqa: E402
import community_review as cr  # noqa: E402

FIXTURES = json.loads((TOOLS.parent / "community" / "fixtures" / "proposals.json").read_text(encoding="utf-8"))
SAIKI = next(case for case in FIXTURES["valid"] if case["name"] == "season-saiki-final")
RELAY = "https://relay.example/v1"
DATASET = {"schemaVersion": 2, "relay": RELAY + "/proposals", "mappings": []}


class FakeRelay:
    def __init__(self, proposals):
        self.proposals = proposals
        self.published = []

    def __call__(self, method, url, payload=None, extra=None):
        if method == "GET" and url == RELAY + "/pending":
            return 200, {"proposals": self.proposals}
        if method == "POST" and url == RELAY + "/published":
            self.published.append(payload)
            return 200, {"ok": True}
        return 404, None


class FakeGitHub:
    def __init__(self, existing=None):
        self.existing = existing or {}
        self.created, self.comments, self.labels, self.closed = [], [], [], []

    def find(self, fingerprint):
        return self.existing.get(fingerprint)

    def create(self, title, body):
        number = 100 + len(self.created)
        self.created.append((title, body))
        return {"number": number, "html_url": f"https://github.com/o/r/issues/{number}"}

    def comment(self, number, text):
        self.comments.append((number, text))

    def label(self, number, name):
        self.labels.append((number, name))

    def close(self, number, reason):
        self.closed.append((number, reason))


def verdict(name):
    def check(title, body, dataset):
        review = cr.Review()
        review.verdict = name
        return review, f"report {name}"
    return check


class Intake(unittest.TestCase):
    def queued(self, **extra):
        return [dict({"fingerprint": SAIKI["fingerprint"], "mapping": SAIKI["mapping"], "confirmations": 3}, **extra)]

    def test_a_queued_proposal_becomes_a_checked_issue_reported_to_the_relay(self):
        relay, github = FakeRelay(self.queued()), FakeGitHub()
        outcomes = ci.intake(DATASET, relay, github, verdict("ok"))
        title, body = github.created[0]
        self.assertEqual(f"[Proposta] Season · AnimeClick 26035 · mapping-{SAIKI['fingerprint']}", title)
        self.assertIn(SAIKI["canonical"], body)
        self.assertIn("Conferme: 3 installazioni", body)
        self.assertEqual(SAIKI["fingerprint"], cr.read_proposal(title, body).fingerprint, "the review reads what the intake writes")
        self.assertEqual([(100, "report ok")], github.comments)
        self.assertEqual([(100, "controlli-ok")], github.labels)
        self.assertEqual([{"fingerprint": SAIKI["fingerprint"], "issue": 100, "url": "https://github.com/o/r/issues/100"}], relay.published)
        self.assertEqual([(SAIKI["fingerprint"], "opened ok #100")], outcomes)

    def test_verdicts_label_or_close(self):
        for name, label, closed in (("doubt", [(100, "controlli-dubbi")], []), ("impossible", [], [(100, "not_planned")]),
                                    ("duplicate", [], [(100, "completed")])):
            with self.subTest(name):
                github = FakeGitHub()
                ci.intake(DATASET, FakeRelay(self.queued()), github, verdict(name))
                self.assertEqual(label, github.labels)
                self.assertEqual(closed, github.closed)

    def test_an_existing_issue_is_reused_without_a_second_check(self):
        relay = FakeRelay(self.queued())
        github = FakeGitHub({SAIKI["fingerprint"]: {"number": 7, "html_url": "https://github.com/o/r/issues/7"}})
        self.assertEqual([(SAIKI["fingerprint"], "already open #7")], ci.intake(DATASET, relay, github, verdict("ok")))
        self.assertEqual([], github.created)
        self.assertEqual(7, relay.published[0]["issue"])

    def test_a_tampered_or_invalid_proposal_is_skipped_and_stays_queued(self):
        invalid = next(case for case in FIXTURES["invalid"] if case["name"] == "extra-field")
        relay = FakeRelay(self.queued(fingerprint="0" * 20) + [{"fingerprint": "1" * 20, "mapping": invalid["mapping"]}])
        github = FakeGitHub()
        outcomes = ci.intake(DATASET, relay, github, verdict("ok"))
        self.assertTrue(all(outcome.startswith("skipped") for _, outcome in outcomes))
        self.assertEqual([], github.created)
        self.assertEqual([], relay.published)

    def test_nothing_happens_without_a_published_relay(self):
        self.assertEqual([("-", "no relay published in the dataset")],
                         ci.intake({"schemaVersion": 2, "mappings": []}, FakeRelay([]), FakeGitHub(), verdict("ok")))


if __name__ == "__main__":
    unittest.main()
