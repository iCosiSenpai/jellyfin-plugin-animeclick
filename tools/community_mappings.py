#!/usr/bin/env python3
"""Validate the public mappings, or add a reviewed proposal to them.

`community/mappings-v2.json` is the source: schema 2, with seasons and the relay address.
`community/mappings.json` is generated from it at schema 1 (series and films only), because the
plugin up to 1.3 rejects the whole file when it meets a kind it does not know.
"""
import argparse
import json
import pathlib
from urllib.parse import urlsplit

ROOT = pathlib.Path(__file__).resolve().parents[1]
DATASET = ROOT / "community" / "mappings-v2.json"
LEGACY = ROOT / "community" / "mappings.json"
PROVIDERS = {"Tmdb", "Tvdb", "AniList"}
# AniList identifies a single cour, never the whole series a season belongs to.
SERIES_PROVIDERS = {"Tmdb", "Tvdb"}
MAX_MAPPINGS = 5000
MAX_BYTES = 1024 * 1024


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate field: {key}")
        result[key] = value
    return result


def parse(data):
    if len(data) > MAX_BYTES:
        raise ValueError("Dataset exceeds 1 MiB")
    return json.loads(data, object_pairs_hook=unique_object)


def load(path):
    return parse(pathlib.Path(path).read_bytes())


def public_id(value):
    return (isinstance(value, str) and 0 < len(value) <= 10
            and value.isascii() and value.isdecimal() and not value.startswith("0"))


def whole_number(value, low, high):
    # bool is an int in Python; JSON true must not pass as season 1.
    return isinstance(value, int) and not isinstance(value, bool) and low <= value <= high


def validate_ids(ids, allowed, label):
    if not isinstance(ids, dict) or not ids or not set(ids) <= allowed:
        raise ValueError(f"{label} accepts only {', '.join(sorted(allowed))}")
    if not all(public_id(value) for value in ids.values()):
        raise ValueError("External IDs must be positive numeric strings")


def validate_mapping(mapping):
    if not isinstance(mapping, dict) or "kind" not in mapping:
        raise ValueError("A proposal must be an object with a kind")
    kind = mapping["kind"]
    if kind in ("Series", "Movie"):
        if set(mapping) != {"kind", "animeClickId", "providerIds"}:
            raise ValueError("A series or film may contain only kind, animeClickId and providerIds")
        validate_ids(mapping["providerIds"], PROVIDERS, "providerIds")
    elif kind == "Season":
        if set(mapping) != {"kind", "animeClickId", "series", "seasonNumber", "episodeCount"}:
            raise ValueError("A season may contain only kind, animeClickId, series, seasonNumber and episodeCount")
        validate_ids(mapping["series"], SERIES_PROVIDERS, "series")
        if not whole_number(mapping["seasonNumber"], 0, 100):
            raise ValueError("seasonNumber must be a whole number from 0 to 100")
        if not whole_number(mapping["episodeCount"], 1, 2000):
            raise ValueError("episodeCount must be a whole number from 1 to 2000")
    else:
        raise ValueError("kind must be Series, Movie or Season")
    if not public_id(mapping["animeClickId"]):
        raise ValueError("Invalid AnimeClick numeric ID")
    return mapping


def validate_relay(value):
    parts = urlsplit(value) if isinstance(value, str) and len(value) <= 200 else None
    if (parts is None or parts.scheme != "https" or not parts.hostname or parts.username or parts.password
            or parts.query or parts.fragment or parts.port not in (None, 443)):
        raise ValueError("relay must be a plain https URL without credentials, query or fragment")


def identity_keys(mapping):
    """Every public coordinate a mapping claims, so two entries cannot send it to different cards."""
    if mapping["kind"] == "Season":
        return [("Season", provider, external_id, mapping["seasonNumber"], mapping["episodeCount"])
                for provider, external_id in mapping["series"].items()]
    return [(mapping["kind"], provider, external_id) for provider, external_id in mapping["providerIds"].items()]


def validate_mappings(mappings):
    if not isinstance(mappings, list) or len(mappings) > MAX_MAPPINGS:
        raise ValueError(f"At most {MAX_MAPPINGS} public mappings are allowed")
    assigned = {}
    for mapping in mappings:
        validate_mapping(mapping)
        for key in identity_keys(mapping):
            previous = assigned.setdefault(key, mapping["animeClickId"])
            if previous != mapping["animeClickId"]:
                raise ValueError(f"Conflicting public identity: {key}")


def validate(dataset):
    if not isinstance(dataset, dict) or dataset.get("schemaVersion") != 2:
        raise ValueError("Invalid schema: expected schemaVersion 2")
    if not {"schemaVersion", "mappings"} <= set(dataset) <= {"schemaVersion", "mappings", "relay"}:
        raise ValueError("The dataset may contain only schemaVersion, relay and mappings")
    if "relay" in dataset:
        validate_relay(dataset["relay"])
    validate_mappings(dataset["mappings"])
    return dataset


def validate_legacy(dataset):
    if not isinstance(dataset, dict) or set(dataset) != {"schemaVersion", "mappings"} or dataset["schemaVersion"] != 1:
        raise ValueError("Invalid legacy schema")
    if any(not isinstance(entry, dict) or entry.get("kind") not in ("Series", "Movie") for entry in dataset["mappings"]):
        raise ValueError("The legacy dataset may contain only series and films")
    validate_mappings(dataset["mappings"])
    return dataset


def sort_key(mapping):
    rest = {key: value for key, value in mapping.items() if key not in ("kind", "animeClickId")}
    return mapping["kind"], int(mapping["animeClickId"]), json.dumps(rest, sort_keys=True)


def canonical(mapping):
    """The exact text the plugin, the relay and the review hash: sorted keys, no spaces."""
    return json.dumps(mapping, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def legacy(dataset):
    return {"schemaVersion": 1,
            "mappings": [mapping for mapping in dataset["mappings"] if mapping["kind"] in ("Series", "Movie")]}


def add(dataset, proposal):
    validate_mapping(proposal)
    if proposal not in dataset["mappings"]:
        dataset["mappings"].append(proposal)
    dataset["mappings"].sort(key=sort_key)
    return validate(dataset)


def render(dataset):
    return json.dumps(dataset, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def write(dataset, path=DATASET, legacy_path=LEGACY):
    validate(dataset)
    pathlib.Path(path).write_text(render(dataset), encoding="utf-8")
    pathlib.Path(legacy_path).write_text(render(legacy(dataset)), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=pathlib.Path, default=DATASET)
    parser.add_argument("--legacy", type=pathlib.Path, default=LEGACY)
    parser.add_argument("--add", type=pathlib.Path, action="append", default=[],
                        help="JSON proposal already reviewed by a maintainer (repeatable)")
    parser.add_argument("--relay", help="publish the relay address in the dataset")
    parser.add_argument("--write", action="store_true", help="rewrite both files from the source")
    args = parser.parse_args()

    dataset = validate(load(args.dataset))
    changed = False
    for path in args.add:
        add(dataset, load(path))
        changed = True
    if args.relay is not None:
        validate_relay(args.relay)
        dataset["relay"] = args.relay
        changed = True
    if changed:
        write(dataset, args.dataset, args.legacy)
    elif not args.write:
        validate_legacy(load(args.legacy))
        if args.legacy.read_text(encoding="utf-8") != render(legacy(dataset)):
            raise SystemExit(f"{args.legacy.name} is out of date: regenerate it with --write")
    if args.write:
        write(dataset, args.dataset, args.legacy)
    seasons = sum(1 for mapping in dataset["mappings"] if mapping["kind"] == "Season")
    print(f"Valid public mappings: {len(dataset['mappings'])} ({seasons} seasons)")


if __name__ == "__main__":
    main()
