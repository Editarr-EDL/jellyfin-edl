"""Caller-supplied EDL and MCF request matrix against a launched instance.

A case file is JSON `{"cases": [CASE, ...]}`. Each CASE names one request and its
expected answer:

- `name`: unique within the file.
- `principal`: `userA`, `userB`, `apiKey`, or `anonymous`.
- `format`: `edl` or `mcf`.
- `media`: a media path the instance indexed; the request uses its item ID.
- `version`: optional media path whose media source ID is sent as `mediaSourceId`.
- `status`: the expected HTTP status.
- `sha256`: for a 200, the list of acceptable body digests.
- `ifNoneMatch`: optional earlier case name whose `ETag` is sent as `If-None-Match`.
- `until`: optional digest; the request repeats until the body has it or
  `seconds` (default 60) pass, and every answer must satisfy the case.

The expectations belong to the caller, typically Editarr's interoperability
journey, which derives them from the bytes it published. This module requests
and compares; it never reads the export files itself.
"""

from __future__ import annotations

import hashlib
import time

from journey import CONTENT_TYPES, PRIVATE, ROUTES

PRINCIPALS = ("userA", "userB", "apiKey", "anonymous")
FIELDS = {"name", "principal", "format", "media", "version", "status", "sha256", "ifNoneMatch", "until", "seconds"}


def load_cases(document: dict, items: dict) -> list[dict]:
    """Validate a case document against the instance's indexed media; raise ValueError on any defect."""
    cases = document.get("cases") if isinstance(document, dict) else None
    if not isinstance(cases, list) or not cases:
        raise ValueError("cases must be a non-empty list")
    seen = set()
    for case in cases:
        unknown = set(case) - FIELDS
        if unknown:
            raise ValueError(f"{case.get('name')}: unknown fields {sorted(unknown)}")
        name = case.get("name")
        if not isinstance(name, str) or name in seen:
            raise ValueError(f"case name {name!r} is missing or repeated")
        if case.get("principal") not in PRINCIPALS or case.get("format") not in ROUTES:
            raise ValueError(f"{name}: unknown principal or format")
        for key in ("media", "version"):
            if key in case and case[key] not in items:
                raise ValueError(f"{name}: {key} {case[key]!r} is not an indexed media path")
        if "media" not in case or not isinstance(case.get("status"), int):
            raise ValueError(f"{name}: media and status are required")
        if (case["status"] == 200) != bool(case.get("sha256")):
            raise ValueError(f"{name}: sha256 is required exactly for status 200")
        if "ifNoneMatch" in case and case["ifNoneMatch"] not in seen:
            raise ValueError(f"{name}: ifNoneMatch must name an earlier case")
        if "until" in case and case["until"] not in case.get("sha256", []):
            raise ValueError(f"{name}: until must be one of its acceptable digests")
        seen.add(name)
    return cases


def judge(case: dict, response) -> dict:
    """One response's observable fields and whether they satisfy the case."""
    headers = {key.lower(): value for key, value in response.headers.items()}
    observed = {"status": response.status, "contentType": headers.get("content-type", ""),
                "cacheControl": headers.get("cache-control"), "etag": headers.get("etag"),
                "bodyBytes": len(response.body), "bodySha256": hashlib.sha256(response.body).hexdigest()}
    ok = response.status == case["status"]
    if case["status"] not in (401, 403):
        ok = ok and observed["cacheControl"] == PRIVATE
    if case["status"] == 200:
        ok = ok and observed["contentType"].startswith(CONTENT_TYPES[case["format"]]) and \
            observed["bodySha256"] in case["sha256"]
    return {**observed, "ok": ok}


def run(clients: dict, items: dict, cases: list[dict], clock=time.monotonic, pause=time.sleep) -> list[dict]:
    """Request every case in order and record each answer; a repeating case records them all."""
    results = {}
    for case in cases:
        query = f"?mediaSourceId={items[case['version']]['source']}" if "version" in case else ""
        path = ROUTES[case["format"]].format(items[case["media"]]["item"]) + query
        headers = {}
        if "ifNoneMatch" in case and results[case["ifNoneMatch"]]["etag"]:
            headers["If-None-Match"] = results[case["ifNoneMatch"]]["etag"]

        def ask():
            response = clients[case["principal"]].request(
                "GET", path, authenticated=case["principal"] != "anonymous", headers=dict(headers))
            return judge(case, response)

        answers = [ask()]
        if "until" in case:
            deadline = clock() + case.get("seconds", 60)
            while answers[-1]["bodySha256"] != case["until"] and answers[-1]["ok"] and clock() < deadline:
                pause(0.05)
                answers.append(ask())
        last = answers[-1]
        ok = all(answer["ok"] for answer in answers) and ("until" not in case or last["bodySha256"] == case["until"])
        result = {"check": case["name"], "case": case, **last, "ok": ok}
        if "until" in case:
            result["answers"] = len(answers)
            result["digestsSeen"] = sorted({answer["bodySha256"] for answer in answers})
            result["statusesSeen"] = sorted({answer["status"] for answer in answers})
        results[case["name"]] = result
    return list(results.values())
