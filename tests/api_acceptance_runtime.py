#!/usr/bin/env python3
"""Exercise API acceptance against an isolated, disposable Azure Naming Tool instance.

Keys are read from a private JSON file with full/read_only/generation fields.
Only case names, HTTP statuses, and PASS/FAIL are printed. Response bodies and
credentials are never printed. This script writes the same resource type list
back to the disposable instance; it must never target production data.
"""

import argparse
import json
import secrets
import sys
import urllib.error
import urllib.request
from pathlib import Path


def request(base, path, key=None, method="GET", payload=None):
    headers = {}
    if key is not None:
        headers["APIKey"] = key
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.headers.get("Content-Type", ""), response.read()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--keys", type=Path, required=True)
    parser.add_argument("--log-file", type=Path)
    parser.add_argument("--confirm-disposable", action="store_true", required=True)
    args = parser.parse_args()
    if not args.confirm_disposable:
        parser.error("--confirm-disposable is required")
    if args.url.startswith("http://127.0.0.1:") is False:
        parser.error("only a loopback HTTP disposable instance is accepted")
    if args.keys.stat().st_mode & 0o077:
        parser.error("key file must be private (mode 0600)")
    keys = json.loads(args.keys.read_text())
    if set(keys) != {"full", "read_only", "generation"} or len(set(keys.values())) != 3:
        parser.error("expected three distinct disposable keys")

    base = args.url.rstrip("/")
    failures = []
    def check(label, status, expected, predicate=True):
        passed = status == expected and predicate
        print(f"{label}: HTTP {status} {'PASS' if passed else 'FAIL'}")
        if not passed:
            failures.append(label)

    for path in ("/", "/health/live", "/health/ready"):
        status, _, _ = request(base, path)
        check(path, status, 200)
    for version in ("v1", "v2"):
        path = f"/swagger/{version}/swagger.json"
        status, content_type, body = request(base, path)
        try:
            document = json.loads(body)
            valid = bool(document.get("openapi") and document.get("paths")) and "json" in content_type.lower()
        except (ValueError, TypeError):
            valid = False
        check(path, status, 200, valid)
    status, _, _ = request(base, "/swagger/index.html")
    check("/swagger/index.html", status, 200)

    for prefix in ("/api", "/api/v2"):
        for include_admin in ("true", "false"):
            path = f"{prefix}/ImportExport/ExportConfiguration?includeAdmin={include_admin}"
            for role, expected in (("missing", 401), ("invalid", 401),
                                   ("full", 200), ("read_only", 403), ("generation", 403)):
                key = None if role == "missing" else "invalid-disposable-key" if role == "invalid" else keys[role]
                status, _, _ = request(base, path, key)
                check(f"{prefix} export {include_admin} {role}", status, expected)

    for prefix in ("/api", "/api/v2"):
        status, _, _ = request(base, f"{prefix}/ResourceTypes", keys["read_only"])
        check(f"{prefix} read_only config GET", status, 200)
        status, _, _ = request(base, f"{prefix}/ResourceTypes", keys["generation"])
        check(f"{prefix} generation config GET", status, 403)

    mutation_path = "/api/ResourceTypes/PostConfig"
    for role in ("read_only", "generation"):
        status, _, _ = request(base, mutation_path, keys[role], "POST", [])
        check(f"mutation {role}", status, 403)

    # Send back the existing list, so the positive control does not add a new type.
    status, _, body = request(base, "/api/ResourceTypes", keys["full"])
    check("full mutation input GET", status, 200)
    if status == 200:
        try:
            existing_types = json.loads(body)
            if not isinstance(existing_types, list) or not existing_types:
                raise ValueError("unexpected type list")
            status, _, _ = request(base, mutation_path, keys["full"], "POST", existing_types)
            check("full permitted mutation", status, 204)
        except (ValueError, TypeError):
            failures.append("full mutation input structure")
            print("full mutation input structure: FAIL")

    name_request = {
        "resourceType": "st", "resourceEnvironment": "dev", "resourceLocation": "auc",
        "resourceOrg": "so", "resourceProjAppSvc": "spa", "resourceFunction": "func",
        "resourceInstance": "01", "resourceUnitDept": "sud"
    }
    instances = list(range(10, 100))
    secrets.SystemRandom().shuffle(instances)
    for role in ("generation", "full"):
        status, success = 0, False
        for _ in range(10):
            name_request["resourceInstance"] = f"{instances.pop():02d}"
            status, _, body = request(base, "/api/ResourceNamingRequests/RequestName",
                                      keys[role], "POST", name_request)
            try:
                result = json.loads(body)
                success = result.get("success") is True
                duplicate = status == 400 and "already exists" in str(result.get("message", ""))
            except (ValueError, TypeError):
                duplicate = False
            if not duplicate:
                break
        check(f"name generation {role}", status, 200, success)

    if args.log_file:
        logged = args.log_file.read_text(errors="replace")
        safe = "APIKey: present" in logged and "  Body:" not in logged
        for key in keys.values():
            safe = safe and key not in logged and key[:8] not in logged and key[-8:] not in logged
        check("credential and body log hygiene", 200 if safe else 0, 200)

    print(f"API acceptance: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
