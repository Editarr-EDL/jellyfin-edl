"""Two-user EDL and MCF journey over the plugin's two routes against a live Jellyfin server.

Every expectation comes from library.contract_expectation for the requesting
user and route format, so the journey checks the editarr-export-layout-1 rules
rather than recorded responses. Each case is requested on both routes: its own
format's route selects its files, and the other route must ignore them.
"""

from __future__ import annotations

import hashlib
import uuid

import library

ROUTES = {"edl": "/Plugins/Edl/ByItem/{}", "mcf": "/Plugins/Edl/Mcf/ByItem/{}"}
CONTENT_TYPES = {"edl": "text/plain", "mcf": "text/vtt"}
USERS = ("userA", "userB")
KEY = "apiKey"
REQUESTERS = USERS + (KEY,)
PRIVATE = "private, no-cache"


def summarize(name: str, response, expected: dict, manifest: dict, **extra) -> dict:
    """Record one request and whether it matched its expected status, cache header, content type, and exact body."""
    headers = {key.lower(): value for key, value in response.headers.items()}
    result = {"check": name, "expected": expected, "status": response.status,
              "contentType": headers.get("content-type", ""), "cacheControl": headers.get("cache-control"),
              "etag": headers.get("etag"), "bodyBytes": len(response.body),
              "bodySha256": hashlib.sha256(response.body).hexdigest(), **extra}
    ok = response.status == expected["status"]
    if expected["status"] != 401:
        ok = ok and result["cacheControl"] == PRIVATE
    if expected.get("payload"):
        ok = ok and result["contentType"].startswith(CONTENT_TYPES[expected["format"]]) and \
            result["bodySha256"] == manifest["payloads"][expected["payload"]]["sha256"]
    result["ok"] = ok
    return result


def expect(status: int, payload: str | None = None, fmt: str = "edl") -> dict:
    return {"status": status, "payload": payload, "format": fmt}


def run(clients: dict, manifest: dict, items: dict, users: dict, server_reads_mode_000: bool) -> list[dict]:
    """Drive authorization, per-requester selection for both formats, versions, library access, and validators."""
    export_ids = {manifest["principals"][name]["exportId"]: users[name] for name in USERS}
    cases = {case["name"]: library.localize(case, export_ids) for case in library.live_cases(manifest)}
    item = {name: items[library.media_path(case)]["item"] for name, case in cases.items()}
    results = []

    def export_id(requester: str) -> str | None:
        return None if requester == KEY else users[requester]

    def get(user: str, fmt: str, item_id: str, query: str = "", **headers):
        authenticated = user != "anonymous"
        return clients[user].request("GET", ROUTES[fmt].format(item_id) + query, authenticated=authenticated, headers=headers)

    def record(name: str, response, expected: dict, **extra) -> dict:
        results.append(summarize(name, response, expected, manifest, **extra))
        return results[-1]

    for fmt in library.FORMATS:
        record(f"{fmt}: no token", get("anonymous", fmt, item["edl-personal-before-default"]), expect(401, fmt=fmt))

    for name, case in cases.items():
        for fmt in library.FORMATS:
            for requester in REQUESTERS:
                expected = library.contract_expectation(case, export_id(requester), manifest, server_reads_mode_000, fmt)
                planned = case["expect"]["outcome"] if requester == case["principal"] and fmt == case["format"] else None
                record(f"{name} {fmt} as {requester}", get(requester, fmt, item[name]), expected, plannedContract=planned)
            principal = users[case["principal"]]
            record(f"{name} {fmt} as {KEY} naming {case['principal']}", get(KEY, fmt, item[name], f"?userId={principal}"),
                   library.contract_expectation(case, None, manifest, server_reads_mode_000, fmt))

    sources = {path.rsplit("/", 1)[1]: items[path] for path in library.version_paths()}
    versions_item = {source["item"] for source in sources.values()}
    results.append({"check": "two versions are one item", "sources": sources, "ok": len(versions_item) == 1})
    versions_case = cases[library.VERSIONS_CASE]
    for fmt in library.FORMATS:
        for media, source in sources.items():
            for requester in REQUESTERS:
                expected = library.contract_expectation(dict(versions_case, media=media), export_id(requester), manifest, fmt=fmt)
                record(f"version {media} {fmt} as {requester}", get(requester, fmt, source["item"], f"?mediaSourceId={source['source']}"), expected)
        for requester in ("userA", KEY):
            record(f"version not of this item {fmt} as {requester}",
                   get(requester, fmt, next(iter(versions_item)), f"?mediaSourceId={uuid.uuid4().hex}"), expect(404, fmt=fmt))

    restricted_case = library.localize(library.restricted_case(manifest), export_ids)
    restricted = items[library.media_path(restricted_case, library.RESTRICTED_DIR)]["item"]
    for fmt in library.FORMATS:
        record(f"restricted library {fmt} as userB, who has a personal file there", get("userB", fmt, restricted), expect(404, fmt=fmt))
        record(f"restricted library {fmt} as userA", get("userA", fmt, restricted),
               library.contract_expectation(restricted_case, users["userA"], manifest, fmt=fmt))
        record(f"restricted library {fmt} as {KEY}, which sees every library", get(KEY, fmt, restricted),
               library.contract_expectation(restricted_case, None, manifest, fmt=fmt))

    def validators(fmt: str, own_case: str, shared: str, own: str, other: str, default: str) -> None:
        mine = record(f"validator {fmt}: userA first request", get("userA", fmt, item[own_case]), expect(200, own, fmt))
        record(f"validator {fmt}: userA repeats with its own ETag", get("userA", fmt, item[own_case], **{"If-None-Match": mine["etag"]}),
               expect(304, fmt=fmt))
        record(f"validator {fmt}: userB sends userA's ETag", get("userB", fmt, item[own_case], **{"If-None-Match": mine["etag"]}),
               expect(200, other, fmt))
        same_bytes = record(f"validator {fmt}: userA default", get("userA", fmt, item[shared]), expect(200, default, fmt))
        theirs = record(f"validator {fmt}: userB sends userA's ETag for identical default bytes",
                        get("userB", fmt, item[shared], **{"If-None-Match": same_bytes["etag"]}), expect(200, default, fmt))
        theirs["ok"] = theirs["ok"] and theirs["etag"] != same_bytes["etag"]
        key_tag = record(f"validator {fmt}: {KEY} sends userA's ETag for identical default bytes",
                         get(KEY, fmt, item[shared], **{"If-None-Match": same_bytes["etag"]}), expect(200, default, fmt))
        key_tag["ok"] = key_tag["ok"] and key_tag["etag"] not in (same_bytes["etag"], theirs["etag"])
        record(f"validator {fmt}: userA sends the {KEY} ETag", get("userA", fmt, item[shared], **{"If-None-Match": key_tag["etag"]}),
               expect(200, default, fmt))
        record(f"validator {fmt}: {KEY} repeats with its own ETag", get(KEY, fmt, item[shared], **{"If-None-Match": key_tag["etag"]}),
               expect(304, fmt=fmt))
        record(f"validator {fmt}: {KEY} naming userA repeats with its own ETag",
               get(KEY, fmt, item[shared], f"?userId={users['userA']}", **{"If-None-Match": key_tag["etag"]}), expect(304, fmt=fmt))

    validators("edl", "edl-each-user-gets-own-file", "edl-local-files-are-never-selected-for-a-jellyfin-user",
               "edl-personal-a", "edl-personal-b", "edl-default")
    validators("mcf", "mcf-each-user-gets-own-file", "mcf-selected-independently-of-edl",
               "mcf-personal-a", "mcf-personal-b", "mcf-default")

    hd, uhd = (sources[name]["source"] for name in library.VERSION_MEDIA)
    version_tag = record("validator: userA 2160p", get("userA", "edl", sources[library.VERSION_MEDIA[1]]["item"], f"?mediaSourceId={uhd}"),
                         expect(200, "edl-default"))
    record("validator: userA sends the 2160p ETag for 1080p",
           get("userA", "edl", sources[library.VERSION_MEDIA[0]]["item"], f"?mediaSourceId={hd}", **{"If-None-Match": version_tag["etag"]}),
           expect(200, "edl-personal-a"))
    both = "mcf-selected-independently-of-edl"
    edl_tag = record("validator: userA EDL where userA also gets an MCF", get("userA", "edl", item[both]), expect(200, "edl-personal-a"))
    record("validator: userA sends that EDL ETag to the MCF route", get("userA", "mcf", item[both], **{"If-None-Match": edl_tag["etag"]}),
           expect(200, "mcf-default", "mcf"))
    return results
