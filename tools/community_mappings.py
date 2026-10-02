#!/usr/bin/env python3
"""Validate public mappings, or add a manually reviewed proposal to the dataset."""
import argparse
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
DATASET = ROOT / "community" / "mappings.json"
PROVIDERS = {"Tmdb", "Tvdb", "AniList"}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate field: {key}")
        result[key] = value
    return result


def load(path):
    data = pathlib.Path(path).read_bytes()
    if len(data) > 1024 * 1024:
        raise ValueError("Dataset exceeds 1 MiB")
    return json.loads(data, object_pairs_hook=unique_object)


def public_id(value):
    return (isinstance(value, str) and 0 < len(value) <= 10
            and value.isascii() and value.isdecimal() and not value.startswith("0"))


def validate_mapping(mapping):
    if not isinstance(mapping, dict) or set(mapping) != {"kind", "animeClickId", "providerIds"}:
        raise ValueError("A proposal may contain only kind, animeClickId and providerIds")
    if mapping["kind"] not in {"Series", "Movie"} or not public_id(mapping["animeClickId"]):
        raise ValueError("Invalid type or AnimeClick numeric ID")
    providers = mapping["providerIds"]
    if not isinstance(providers, dict) or not providers or not set(providers) <= PROVIDERS:
        raise ValueError("Only Tmdb, Tvdb and AniList are allowed")
    if not all(public_id(value) for value in providers.values()):
        raise ValueError("External IDs must be positive numeric strings")


def validate(dataset):
    if not isinstance(dataset, dict) or set(dataset) != {"schemaVersion", "mappings"} or dataset["schemaVersion"] != 1:
        raise ValueError("Invalid schema")
    mappings = dataset["mappings"]
    if not isinstance(mappings, list) or len(mappings) > 5000:
        raise ValueError("At most 5000 public mappings are allowed")
    assigned = {}
    for mapping in mappings:
        validate_mapping(mapping)
        for provider, external_id in mapping["providerIds"].items():
            key = (mapping["kind"], provider, external_id)
            previous = assigned.setdefault(key, mapping["animeClickId"])
            if previous != mapping["animeClickId"]:
                raise ValueError(f"Conflicting public identity: {key}")
    return dataset


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=pathlib.Path, default=DATASET)
    parser.add_argument("--add", type=pathlib.Path, help="JSON proposal already verified by a maintainer")
    args = parser.parse_args()
    dataset = validate(load(args.dataset))
    if args.add:
        proposal = load(args.add)
        validate_mapping(proposal)
        if proposal not in dataset["mappings"]:
            dataset["mappings"].append(proposal)
        validate(dataset)
        dataset["mappings"].sort(key=lambda entry: (entry["kind"], int(entry["animeClickId"]), json.dumps(entry["providerIds"], sort_keys=True)))
        args.dataset.write_text(json.dumps(dataset, ensure_ascii=False, indent=2, sort_keys=True) + "\n")
    print(f"Valid public mappings: {len(dataset['mappings'])}")


if __name__ == "__main__":
    main()
