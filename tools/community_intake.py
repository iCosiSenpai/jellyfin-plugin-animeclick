#!/usr/bin/env python3
"""Collect the proposals queued by the community relay and turn each into a checked issue.

Runs in the repository's intake workflow with the workflow's own token, so the relay holds no GitHub
credential. For every queued proposal: validate it again, reuse an issue that already carries its
fingerprint or open one, run the automatic checks, comment, label or close, and report the issue
back to the relay so the plugins that sent it can link it.

Issues opened with the workflow token do not start other workflows, which is why the checks run
here and not in the review workflow.
"""
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import community_mappings as cm  # noqa: E402
import community_review as cr  # noqa: E402

LABELS = {
    "proposta": ("1d76db", "Proposta di abbinamento dal plugin"),
    "controlli-ok": ("0e8a16", "Controlli automatici superati"),
    "controlli-dubbi": ("fbca04", "Controlli da guardare"),
    "approvato": ("5319e7", "Approvala: entra nell'elenco della comunità"),
    "ricontrolla": ("c5def5", "Ripeti i controlli automatici"),
}


def issue_title(mapping, fingerprint):
    """Same title the plugin writes when it sends with its own token."""
    return f"[Proposta] {mapping['kind']} · AnimeClick {mapping['animeClickId']} · mapping-{fingerprint}"


def issue_body(mapping, confirmations):
    plural = "installazione" if confirmations == 1 else "installazioni"
    return ("Proposta di abbinamento condivisa dal plugin con il consenso dell’amministratore. "
            "Contiene solo identificativi pubblici; i controlli automatici la verificano prima della revisione.\n\n"
            "```json\n" + cm.canonical(mapping) + "\n```\n\n"
            f"Conferme: {confirmations} {plural} (arrivata dal servizio della comunità).\n")


class Http:
    """JSON over HTTPS with a bearer token; returns (status, parsed body or None)."""

    def __init__(self, token, user_agent="AnimeClick-Community-Intake"):
        self.token = token
        self.user_agent = user_agent

    def __call__(self, method, url, payload=None, extra=None):
        headers = {"User-Agent": self.user_agent, "Accept": "application/json", **(extra or {})}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        data = None
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                text = response.read().decode("utf-8")
                return response.status, json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            text = error.read().decode("utf-8", "replace")
            try:
                return error.code, json.loads(text) if text else None
            except json.JSONDecodeError:
                return error.code, None


class GitHub:
    def __init__(self, http, repository):
        self.http = http
        self.base = "https://api.github.com/repos/" + repository
        self.repository = repository

    def ensure_labels(self):
        for name, (color, description) in LABELS.items():
            self.http("POST", self.base + "/labels", {"name": name, "color": color, "description": description})

    def find(self, fingerprint):
        query = urllib.parse.quote(f"repo:{self.repository} in:title mapping-{fingerprint}")
        status, body = self.http("GET", "https://api.github.com/search/issues?q=" + query)
        items = ((body or {}).get("items") or []) if status == 200 else []
        return items[0] if items else None

    def create(self, title, body):
        status, issue = self.http("POST", self.base + "/issues", {"title": title, "body": body, "labels": ["proposta"]})
        if status != 201:
            raise RuntimeError(f"GitHub did not create the issue ({status})")
        return issue

    def comment(self, number, text):
        self.http("POST", f"{self.base}/issues/{number}/comments", {"body": text})

    def label(self, number, name):
        self.http("POST", f"{self.base}/issues/{number}/labels", {"labels": [name]})

    def close(self, number, reason):
        self.http("PATCH", f"{self.base}/issues/{number}", {"state": "closed", "state_reason": reason})


def relay_base(dataset):
    relay = dataset.get("relay")
    if not relay or not relay.endswith("/proposals"):
        return None
    return relay[: -len("/proposals")]


def intake(dataset, relay_http, github, check):
    """Process every queued proposal; returns a list of (fingerprint, outcome)."""
    base = relay_base(dataset)
    if base is None:
        return [("-", "no relay published in the dataset")]
    status, body = relay_http("GET", base + "/pending")
    if status != 200:
        raise RuntimeError(f"The relay did not list the queue ({status})")
    outcomes = []
    for proposal in (body or {}).get("proposals", []):
        fingerprint = proposal.get("fingerprint", "")
        try:
            mapping = cm.validate_mapping(proposal.get("mapping"))
            if cr.fingerprint(mapping) != fingerprint:
                raise ValueError("fingerprint does not match the mapping")
            title = issue_title(mapping, fingerprint)
            text = issue_body(mapping, int(proposal.get("confirmations") or 1))
            issue = github.find(fingerprint)
            if issue is None:
                issue = github.create(title, text)
                review, report = check(title, text, dataset)
                github.comment(issue["number"], report)
                if review.verdict == "ok":
                    github.label(issue["number"], "controlli-ok")
                elif review.verdict == "doubt":
                    github.label(issue["number"], "controlli-dubbi")
                elif review.verdict == "impossible":
                    github.close(issue["number"], "not_planned")
                elif review.verdict == "duplicate":
                    github.close(issue["number"], "completed")
                outcome = "opened " + review.verdict
            else:
                outcome = "already open"
            status, _ = relay_http("POST", base + "/published",
                                   {"fingerprint": fingerprint, "issue": issue["number"], "url": issue["html_url"]})
            if status != 200:
                raise RuntimeError(f"The relay did not record the issue ({status})")
            outcomes.append((fingerprint, f"{outcome} #{issue['number']}"))
        except Exception as error:  # one bad proposal must not block the queue
            outcomes.append((fingerprint, f"skipped: {error}"))
    return outcomes


def main():
    secret = os.environ.get("RELAY_ADMIN_SECRET", "")
    token = os.environ.get("GITHUB_TOKEN", "")
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not secret or not token or not repository:
        sys.exit("RELAY_ADMIN_SECRET, GITHUB_TOKEN and GITHUB_REPOSITORY are required")
    dataset = cm.validate(cm.load(cm.DATASET))
    github = GitHub(Http(token), repository)
    github.ensure_labels()
    tmdb_key = os.environ.get("TMDB_API_KEY", "")
    outcomes = intake(dataset, Http(secret), github,
                      lambda title, body, data: cr.run_check(title, body, data, tmdb_key=tmdb_key))
    for fingerprint, outcome in outcomes:
        # A skipped proposal stays queued and is retried on the next run; flag it without failing the run.
        print(("::warning::" if outcome.startswith("skipped") else "") + f"{fingerprint}: {outcome}")


if __name__ == "__main__":
    main()
