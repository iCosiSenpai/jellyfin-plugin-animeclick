#!/usr/bin/env python3
"""Publish the community relay on Cloudflare Workers through the Cloudflare API (no wrangler needed).

    CLOUDFLARE_API_TOKEN=… python3 community/relay/deploy.py [--github-secret]

Idempotent: it reuses the KV namespace and the workers.dev subdomain when they exist, uploads the
worker with its bindings and enables its workers.dev address. Secrets already on the worker are kept;
on the first publication it generates INSTALL_SALT and ADMIN_SECRET, and with --github-secret it also
stores ADMIN_SECRET in the repository as RELAY_ADMIN_SECRET for the intake workflow (needs gh).
Prints the address to publish with tools/community_mappings.py --relay. Never prints a secret.
"""
import argparse
import json
import os
import pathlib
import secrets
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

API = "https://api.cloudflare.com/client/v4"
HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = "animeclick-community"
KV_TITLE = "animeclick-community"
REPOSITORY = "iCosiSenpai/jellyfin-plugin-animeclick"
COMPATIBILITY_DATE = "2026-09-01"


class Cloudflare:
    def __init__(self, token):
        self.token = token

    def call(self, method, path, payload=None, raw=None, content_type=None):
        headers = {"Authorization": "Bearer " + self.token}
        data = raw
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        elif content_type:
            headers["Content-Type"] = content_type
        request = urllib.request.Request(API + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            try:
                return error.code, json.load(error)
            except json.JSONDecodeError:
                return error.code, {}

    def require(self, method, path, what, **kwargs):
        status, body = self.call(method, path, **kwargs)
        if not body.get("success"):
            messages = "; ".join(e.get("message", "") for e in body.get("errors", [])) or f"HTTP {status}"
            sys.exit(f"{what} non riuscito: {messages}")
        return body.get("result")


def multipart(parts):
    """multipart/form-data with explicit content types, as the Workers upload API expects."""
    boundary = "----animeclick" + uuid.uuid4().hex
    chunks = []
    for name, filename, content_type, data in parts:
        disposition = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        chunks.append(f"--{boundary}\r\nContent-Disposition: {disposition}\r\nContent-Type: {content_type}\r\n\r\n".encode())
        chunks.append(data)
        chunks.append(b"\r\n")
    chunks.append(f"--{boundary}--\r\n".encode())
    return b"".join(chunks), "multipart/form-data; boundary=" + boundary


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--github-secret", action="store_true", help="store a newly generated ADMIN_SECRET in the repository")
    parser.add_argument("--subdomain", help="workers.dev subdomain of the account; renamed when it differs "
                                            "(it ends up in a public address, so it should not be personal)")
    args = parser.parse_args()
    token = os.environ.get("CLOUDFLARE_API_TOKEN", "").strip()
    if not token:
        sys.exit("CLOUDFLARE_API_TOKEN is required")
    cf = Cloudflare(token)

    accounts = cf.require("GET", "/accounts", "Lettura dell'account")
    account = os.environ.get("CLOUDFLARE_ACCOUNT_ID") or (accounts[0]["id"] if len(accounts) == 1 else None)
    if not account:
        sys.exit("Più account disponibili: indica CLOUDFLARE_ACCOUNT_ID")
    base = f"/accounts/{account}"

    # KV namespace: reuse it by title, or create it.
    namespaces = cf.require("GET", f"{base}/storage/kv/namespaces?per_page=100", "Lettura dei namespace KV")
    namespace = next((n["id"] for n in namespaces if n.get("title") == KV_TITLE), None)
    if namespace is None:
        namespace = cf.require("POST", f"{base}/storage/kv/namespaces", "Creazione del namespace KV", payload={"title": KV_TITLE})["id"]
        print("KV creato:", KV_TITLE)

    # workers.dev subdomain of the account: reuse it, or create one from the account name.
    status, body = cf.call("GET", f"{base}/workers/subdomain")
    subdomain = (body.get("result") or {}).get("subdomain") if body.get("success") else None
    wanted = args.subdomain or subdomain or "animeclick-" + secrets.token_hex(3)
    if subdomain != wanted:
        subdomain = cf.require("PUT", f"{base}/workers/subdomain", "Impostazione del sottodominio workers.dev",
                               payload={"subdomain": wanted})["subdomain"]
        print("Sottodominio workers.dev:", subdomain)

    # First publication or an existing worker: existing secrets are kept, missing ones generated.
    status, body = cf.call("GET", f"{base}/workers/scripts/{SCRIPT}/secrets")
    existing = {s["name"] for s in (body.get("result") or [])} if body.get("success") else set()
    bindings = [
        {"type": "kv_namespace", "name": "PROPOSALS", "namespace_id": namespace},
        {"type": "plain_text", "name": "REPOSITORY", "text": REPOSITORY},
        {"type": "ratelimit", "name": "IP_LIMITER", "namespace_id": "1001", "simple": {"limit": 10, "period": 60}},
    ]
    generated_admin = None
    if "INSTALL_SALT" not in existing:
        bindings.append({"type": "secret_text", "name": "INSTALL_SALT", "text": secrets.token_hex(32)})
    if "ADMIN_SECRET" not in existing:
        # A secret nobody stored would leave the intake workflow locked out: generate it only when it
        # can be saved in the repository at once.
        if not args.github_secret:
            sys.exit("Prima pubblicazione: rilancia con --github-secret per generare ADMIN_SECRET e salvarlo nel repository.")
        generated_admin = secrets.token_urlsafe(48)
        bindings.append({"type": "secret_text", "name": "ADMIN_SECRET", "text": generated_admin})
    metadata = {
        "main_module": "worker.js",
        "compatibility_date": COMPATIBILITY_DATE,
        "bindings": bindings,
        "keep_bindings": ["secret_text"],
        "observability": {"enabled": False},
    }
    body, content_type = multipart([
        ("metadata", None, "application/json", json.dumps(metadata).encode()),
        ("worker.js", "worker.js", "application/javascript+module", (HERE / "src" / "worker.js").read_bytes()),
        ("proposal.js", "proposal.js", "application/javascript+module", (HERE / "src" / "proposal.js").read_bytes()),
    ])
    cf.require("PUT", f"{base}/workers/scripts/{SCRIPT}", "Pubblicazione del worker", raw=body, content_type=content_type)
    cf.require("POST", f"{base}/workers/scripts/{SCRIPT}/subdomain", "Attivazione dell'indirizzo workers.dev",
               payload={"enabled": True, "previews_enabled": False})
    print("Worker pubblicato:", SCRIPT)

    if generated_admin:
        subprocess.run(["gh", "secret", "set", "RELAY_ADMIN_SECRET", "-R", REPOSITORY],
                       input=generated_admin.encode(), check=True)
        print("Segreto RELAY_ADMIN_SECRET salvato nel repository.")

    print(f"Indirizzo: https://{SCRIPT}.{subdomain}.workers.dev/v1/proposals")


if __name__ == "__main__":
    main()
