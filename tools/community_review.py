#!/usr/bin/env python3
"""Automatic checks for a community proposal issue, and its approval.

  check   reads the issue, verifies the proposal against AnimeClick, TMDB and TheTVDB, and prints the
          comment for the issue; the verdict (ok, doubt, impossible or duplicate) goes to GITHUB_OUTPUT.
  approve adds the proposal of an issue to the dataset and regenerates both files.

Everything the issue says is data: only the JSON block is read, and only after it matches the
fingerprint in the title and the shared validation rules.
"""
import argparse
import hashlib
import html
import json
import os
import pathlib
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import community_mappings as cm  # noqa: E402

ANIMECLICK = "https://www.animeclick.it/anime/"
USER_AGENT = "AnimeClick-Jellyfin-Plugin-Review (+https://github.com/iCosiSenpai/jellyfin-plugin-animeclick)"
MARKER = re.compile(r"mapping-([0-9a-f]{20})\b")
JSON_BLOCK = re.compile(r"```json\s*\n(.*?)\n```", re.S)


class Proposal:
    def __init__(self, mapping, fingerprint):
        self.mapping = mapping
        self.fingerprint = fingerprint


def fingerprint(mapping):
    return hashlib.sha256(cm.canonical(mapping).encode("utf-8")).hexdigest()[:20]


def read_proposal(title, body):
    """The proposal of an issue, or a ValueError that explains why there is none."""
    marker = MARKER.search(title or "")
    if not marker:
        raise ValueError("Il titolo non contiene il marcatore mapping-…: non è una proposta del plugin.")
    block = JSON_BLOCK.search(body or "")
    if not block:
        raise ValueError("La segnalazione non contiene il blocco JSON della proposta.")
    mapping = cm.validate_mapping(cm.parse(block.group(1).encode("utf-8")))
    if fingerprint(mapping) != marker.group(1):
        raise ValueError("Il JSON non corrisponde all’impronta del titolo: la proposta è stata modificata.")
    return Proposal(mapping, marker.group(1))


def fetch(url, headers=None):
    """GET returning (status, text); network failures are status 0 so a check is skipped, not failed."""
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, response.read(2 * 1024 * 1024).decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, ""
    except (urllib.error.URLError, TimeoutError, OSError):
        return 0, ""


def text_of(fragment):
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", fragment or ""))).strip()


def parse_card(page):
    """Title, year, category and declared episodes of an AnimeClick card."""
    fields = {text_of(label).rstrip(":").lower(): text_of(value)
              for label, value in re.findall(r"<dt[^>]*>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>", page, re.S)}
    title = re.search(r'<h1[^>]*itemprop="name"[^>]*>(.*?)</h1>', page, re.S) \
        or re.search(r'<meta property="og:title" content="([^"]*)"', page)
    year = re.search(r'itemprop="datePublished" content="(\d{4})', page)
    year = int(year.group(1)) if year else next((int(match) for match in re.findall(r"\b(19\d\d|20\d\d)\b", fields.get("anno", ""))), None)
    episodes = re.search(r"\d+", fields.get("episodi", ""))
    return {
        "title": text_of(title.group(1)) if title else None,
        "year": year,
        "category": fields.get("categoria"),
        "episodes": int(episodes.group(0)) if episodes else None,
    }


class Review:
    def __init__(self):
        self.rows = []
        self.verdict = "ok"

    def add(self, state, check, detail):
        self.rows.append((state, check, detail))
        if state == "bad":
            self.verdict = "impossible"
        elif state == "warn" and self.verdict == "ok":
            self.verdict = "doubt"


def card_url(animeclick_id):
    # A bare number answers 404; any slug redirects to the canonical card, as the plugin relies on.
    return ANIMECLICK + animeclick_id + "/x"


def check_animeclick(review, mapping, get):
    status, page = get(card_url(mapping["animeClickId"]))
    if status == 404:
        review.add("bad", "Scheda AnimeClick", f"La scheda {mapping['animeClickId']} non esiste.")
        return None
    if status != 200 or not page:
        review.add("warn", "Scheda AnimeClick", f"Non verificabile adesso (risposta {status or 'assente'}).")
        return None
    card = parse_card(page)
    detail = f"«{card['title'] or '?'}» · {card['category'] or 'tipo ?'} · {card['year'] or 'anno ?'}"
    if card["episodes"]:
        detail += f" · {card['episodes']} episodi"
    is_film = (card["category"] or "").lower().startswith("film")
    if mapping["kind"] == "Movie" and card["category"] and not is_film:
        review.add("warn", "Scheda AnimeClick", detail + ": un film collegato a una scheda che non è un film.")
    elif mapping["kind"] != "Movie" and is_film:
        review.add("warn", "Scheda AnimeClick", detail + ": una serie collegata a un film.")
    elif mapping["kind"] == "Season" and card["episodes"] and card["episodes"] != mapping["episodeCount"]:
        review.add("warn", "Scheda AnimeClick", detail + f": la stagione della libreria ne ha {mapping['episodeCount']}.")
    else:
        review.add("ok", "Scheda AnimeClick", detail)
    return card


def check_tmdb(review, mapping, card, get, key):
    ids = mapping.get("providerIds") or mapping.get("series") or {}
    if "Tmdb" not in ids:
        return
    if not key:
        review.add("info", "TMDB", "Non controllato: manca la chiave TMDB tra i segreti del repository.")
        return
    kind = "movie" if mapping["kind"] == "Movie" else "tv"
    auth = {"Authorization": f"Bearer {key}"} if len(key) > 40 else {}
    suffix = "" if auth else "?api_key=" + urllib.parse.quote(key)
    status, text = get(f"https://api.themoviedb.org/3/{kind}/{ids['Tmdb']}{suffix}", auth)
    if status == 404:
        review.add("bad", "TMDB", f"L’ID {ids['Tmdb']} non esiste su TMDB.")
        return
    if status != 200:
        review.add("warn", "TMDB", f"Non verificabile adesso (risposta {status or 'assente'}).")
        return
    work = json.loads(text)
    name = work.get("title") or work.get("name") or "?"
    date = work.get("release_date") or work.get("first_air_date") or ""
    year = int(date[:4]) if date[:4].isdigit() else None
    detail = f"«{name}» · {year or 'anno ?'}"
    if mapping["kind"] == "Season":
        season_status, season_text = get(f"https://api.themoviedb.org/3/tv/{ids['Tmdb']}/season/{mapping['seasonNumber']}{suffix}", auth)
        episodes = len(json.loads(season_text).get("episodes", [])) if season_status == 200 else None
        if episodes is None:
            review.add("warn", "TMDB", detail + f": la stagione {mapping['seasonNumber']} non c’è con questa numerazione.")
        elif episodes != mapping["episodeCount"]:
            review.add("warn", "TMDB", detail + f": la stagione {mapping['seasonNumber']} ha {episodes} episodi, la proposta {mapping['episodeCount']}.")
        else:
            review.add("ok", "TMDB", detail + f" · stagione {mapping['seasonNumber']} con {episodes} episodi")
        return
    if card and card.get("year") and year and abs(card["year"] - year) > 1:
        review.add("warn", "TMDB", detail + f": anno diverso dalla scheda AnimeClick ({card['year']}).")
    else:
        review.add("ok", "TMDB", detail)


def check_dataset(review, proposal, dataset):
    if proposal.mapping in dataset["mappings"]:
        review.verdict = "duplicate"
        review.add("ok", "Elenco approvato", "La proposta è già nell’elenco.")
        return
    try:
        cm.validate(dict(dataset, mappings=dataset["mappings"] + [proposal.mapping]))
        review.add("ok", "Elenco approvato", "Nessun conflitto con gli abbinamenti già approvati.")
    except ValueError as error:
        review.add("warn", "Elenco approvato", f"In conflitto con un abbinamento già approvato: {error}.")


ICONS = {"ok": "✅", "warn": "⚠️", "bad": "❌", "info": "ℹ️"}
VERDICTS = {
    "ok": "**Controlli superati.** Se ti convince, aggiungi l’etichetta `approvato`: l’abbinamento entrerà nell’elenco e arriverà a tutti.",
    "doubt": "**Da guardare con attenzione.** Qualche controllo non torna o non si è potuto fare: verifica sui siti prima di approvare.",
    "impossible": "**Proposta impossibile**: viene chiusa automaticamente.",
    "duplicate": "**Già approvata**: la segnalazione viene chiusa.",
}


def comment(review, proposal):
    lines = [f"### Controlli automatici · `{proposal.fingerprint}`", "", "| | Controllo | Esito |", "|---|---|---|"]
    lines += [f"| {ICONS[state]} | {check} | {detail.replace('|', '/')} |" for state, check, detail in review.rows]
    lines += ["", VERDICTS[review.verdict], "", "<sub>Commento generato da `tools/community_review.py`.</sub>"]
    return "\n".join(lines) + "\n"


def run_check(title, body, dataset, get=fetch, tmdb_key=""):
    try:
        proposal = read_proposal(title, body)
    except (ValueError, json.JSONDecodeError) as error:
        review = Review()
        review.add("bad", "Proposta", str(error))
        return review, "### Controlli automatici\n\n❌ " + str(error) + "\n\n" + VERDICTS["impossible"] + "\n"
    review = Review()
    review.add("ok", "Proposta", "Formato valido e impronta corretta.")
    check_dataset(review, proposal, dataset)
    if review.verdict != "duplicate":
        card = check_animeclick(review, proposal.mapping, get)
        check_tmdb(review, proposal.mapping, card, get, tmdb_key)
    return review, comment(review, proposal)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["check", "approve"])
    parser.add_argument("--title", required=True)
    parser.add_argument("--body-file", type=pathlib.Path, required=True)
    parser.add_argument("--dataset", type=pathlib.Path, default=cm.DATASET)
    parser.add_argument("--legacy", type=pathlib.Path, default=cm.LEGACY)
    args = parser.parse_args()
    body = args.body_file.read_text(encoding="utf-8")
    dataset = cm.validate(cm.load(args.dataset))

    if args.command == "check":
        review, text = run_check(args.title, body, dataset, tmdb_key=os.environ.get("TMDB_API_KEY", ""))
        sys.stdout.write(text)
        verdict = review.verdict
    else:
        proposal = read_proposal(args.title, body)
        cm.add(dataset, proposal.mapping)
        cm.write(dataset, args.dataset, args.legacy)
        sys.stdout.write(f"Approvata: {cm.canonical(proposal.mapping)}\n")
        verdict = "approved"
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"verdict={verdict}\n")


if __name__ == "__main__":
    main()
