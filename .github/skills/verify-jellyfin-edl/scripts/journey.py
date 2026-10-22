"""Two-user EDL journey over GET /Plugins/Edl/ByItem/{itemId} against a live Jellyfin server.

Every expectation comes from library.contract_expectation for the requesting
user, so the journey checks the editarr-export-layout-1 rules rather than
recorded responses.
"""

from __future__ import annotations

import hashlib
import uuid

import library

ROUTE = "/Plugins/Edl/ByItem/{}"
USERS = ("userA", "userB")
KEY = "apiKey"
REQUESTERS = USERS + (KEY,)
PRIVATE = "private, no-cache"


def summarize(name: str, response, expected: dict, manifest: dict, **extra) -> dict:
    """Record one request and whether it matched its expected status, cache header, and exact body."""
    headers = {key.lower(): value for key, value in response.headers.items()}
    result = {"check": name, "expected": expected, "status": response.status,
              "contentType": headers.get("content-type", ""), "cacheControl": headers.get("cache-control"),
              "etag": headers.get("etag"), "bodyBytes": len(response.body),
              "bodySha256": hashlib.sha256(response.body).hexdigest(), **extra}
    ok = response.status == expected["status"]
    if expected["status"] != 401:
        ok = ok and result["cacheControl"] == PRIVATE
    if expected.get("payload"):
        ok = ok and result["contentType"].startswith("text/plain") and \
            result["bodySha256"] == manifest["payloads"][expected["payload"]]["sha256"]
    result["ok"] = ok
    return result


def expect(status: int, payload: str | None = None) -> dict:
    return {"status": status, "payload": payload}


def run(clients: dict, manifest: dict, items: dict, users: dict, server_reads_mode_000: bool) -> list[dict]:
    """Drive authorization, per-requester selection, versions, library access, and validators; return every check."""
    export_ids = {manifest["principals"][name]["exportId"]: users[name] for name in USERS}
    cases = {case["name"]: library.localize(case, export_ids) for case in library.edl_cases(manifest)}
    item = {name: items[library.media_path(case)]["item"] for name, case in cases.items()}
    results = []

    def export_id(requester: str) -> str | None:
        return None if requester == KEY else users[requester]

    def get(user: str, item_id: str, query: str = "", **headers):
        authenticated = user != "anonymous"
        return clients[user].request("GET", ROUTE.format(item_id) + query, authenticated=authenticated, headers=headers)

    def record(name: str, response, expected: dict, **extra) -> dict:
        results.append(summarize(name, response, expected, manifest, **extra))
        return results[-1]

    record("no token", get("anonymous", item["edl-personal-before-default"]), expect(401))

    for name, case in cases.items():
        for requester in REQUESTERS:
            expected = library.contract_expectation(case, export_id(requester), manifest, server_reads_mode_000)
            planned = case["expect"]["outcome"] if requester == case["principal"] else None
            record(f"{name} as {requester}", get(requester, item[name]), expected, plannedContract=planned)
        principal = users[case["principal"]]
        record(f"{name} as {KEY} naming {case['principal']}", get(KEY, item[name], f"?userId={principal}"),
               library.contract_expectation(case, None, manifest, server_reads_mode_000))

    sources = {path.rsplit("/", 1)[1]: items[path] for path in library.version_paths()}
    versions_item = {source["item"] for source in sources.values()}
    results.append({"check": "two versions are one item", "sources": sources, "ok": len(versions_item) == 1})
    versions_case = cases[library.VERSIONS_CASE]
    for media, source in sources.items():
        for requester in REQUESTERS:
            expected = library.contract_expectation(dict(versions_case, media=media), export_id(requester), manifest)
            record(f"version {media} as {requester}", get(requester, source["item"], f"?mediaSourceId={source['source']}"), expected)
    record("version not of this item", get("userA", next(iter(versions_item)), f"?mediaSourceId={uuid.uuid4().hex}"), expect(404))
    record(f"version not of this item as {KEY}", get(KEY, next(iter(versions_item)), f"?mediaSourceId={uuid.uuid4().hex}"), expect(404))

    restricted = items[library.media_path(cases[library.RESTRICTED_CASE], library.RESTRICTED_DIR)]["item"]
    record("restricted library as userB, who has a personal file there", get("userB", restricted), expect(404))
    record("restricted library as userA", get("userA", restricted),
           library.contract_expectation(cases[library.RESTRICTED_CASE], users["userA"], manifest))
    record(f"restricted library as {KEY}, which sees every library", get(KEY, restricted),
           library.contract_expectation(cases[library.RESTRICTED_CASE], None, manifest))

    own = record("validator: userA first request", get("userA", item[library.RESTRICTED_CASE]), expect(200, "edl-personal-a"))
    record("validator: userA repeats with its own ETag", get("userA", item[library.RESTRICTED_CASE], **{"If-None-Match": own["etag"]}), expect(304))
    record("validator: userB sends userA's ETag", get("userB", item[library.RESTRICTED_CASE], **{"If-None-Match": own["etag"]}),
           expect(200, "edl-personal-b"))
    shared = "edl-local-files-are-never-selected-for-a-jellyfin-user"
    same_bytes = record("validator: userA default", get("userA", item[shared]), expect(200, "edl-default"))
    other = record("validator: userB sends userA's ETag for identical default bytes",
                   get("userB", item[shared], **{"If-None-Match": same_bytes["etag"]}), expect(200, "edl-default"))
    other["ok"] = other["ok"] and other["etag"] != same_bytes["etag"]
    key_tag = record(f"validator: {KEY} sends userA's ETag for identical default bytes",
                     get(KEY, item[shared], **{"If-None-Match": same_bytes["etag"]}), expect(200, "edl-default"))
    key_tag["ok"] = key_tag["ok"] and key_tag["etag"] not in (same_bytes["etag"], other["etag"])
    record(f"validator: userA sends the {KEY} ETag", get("userA", item[shared], **{"If-None-Match": key_tag["etag"]}),
           expect(200, "edl-default"))
    record(f"validator: {KEY} repeats with its own ETag", get(KEY, item[shared], **{"If-None-Match": key_tag["etag"]}), expect(304))
    record(f"validator: {KEY} naming userA repeats with its own ETag",
           get(KEY, item[shared], f"?userId={users['userA']}", **{"If-None-Match": key_tag["etag"]}), expect(304))
    hd, uhd = (sources[name]["source"] for name in library.VERSION_MEDIA)
    version_tag = record("validator: userA 2160p", get("userA", sources[library.VERSION_MEDIA[1]]["item"], f"?mediaSourceId={uhd}"),
                         expect(200, "edl-default"))
    record("validator: userA sends the 2160p ETag for 1080p",
           get("userA", sources[library.VERSION_MEDIA[0]]["item"], f"?mediaSourceId={hd}", **{"If-None-Match": version_tag["etag"]}),
           expect(200, "edl-personal-a"))
    return results
