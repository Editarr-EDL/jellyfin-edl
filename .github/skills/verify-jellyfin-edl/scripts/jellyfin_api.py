"""Minimal Jellyfin HTTP client for a disposable verification server.

Secrets travel only in request bodies and headers. Nothing here logs them.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass

CLIENT = 'MediaBrowser Client="jfedl-verify", Device="jfedl-verify", DeviceId="{device}", Version="1.0"'


@dataclass(frozen=True)
class Response:
    status: int
    headers: dict
    body: bytes

    def json(self):
        return json.loads(self.body)


class Jellyfin:
    def __init__(self, base_url: str, device_id: str, token: str | None = None):
        self.base_url = base_url
        self.device_id = device_id
        self.token = token

    def request(self, method: str, path: str, body: object = None, authenticated: bool = True) -> Response:
        header = CLIENT.format(device=self.device_id)
        if authenticated and self.token:
            header += f', Token="{self.token}"'
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base_url + path, data=data, method=method)
        request.add_header("Authorization", header)
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return Response(response.status, dict(response.headers), response.read())
        except urllib.error.HTTPError as error:
            return Response(error.code, dict(error.headers), error.read())

    def expect(self, method: str, path: str, statuses: tuple, body: object = None) -> Response:
        response = self.request(method, path, body)
        if response.status not in statuses:
            raise RuntimeError(f"{method} {path.split('?')[0]} returned HTTP {response.status}")
        return response


def wait_ready(client: Jellyfin, timeout: float) -> dict:
    """Poll the public info route until the server answers with JSON."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            response = client.request("GET", "/System/Info/Public", authenticated=False)
            if response.status == 200:
                return response.json()
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(2)
    raise TimeoutError(f"Jellyfin did not answer /System/Info/Public within {timeout:.0f}s")


def complete_wizard(client: Jellyfin, username: str, password: str) -> None:
    """Run the first-start wizard with a synthetic administrator."""
    client.expect("POST", "/Startup/Configuration", (200, 204), {
        "UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    client.expect("GET", "/Startup/User", (200,))
    client.expect("POST", "/Startup/User", (200, 204), {"Name": username, "Password": password})
    client.expect("POST", "/Startup/RemoteAccess", (200, 204), {
        "EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
    client.expect("POST", "/Startup/Complete", (200, 204))


def authenticate(client: Jellyfin, username: str, password: str) -> tuple[str, str]:
    """Sign in by name and return (access token, user ID)."""
    response = client.expect("POST", "/Users/AuthenticateByName", (200,), {"Username": username, "Pw": password})
    payload = response.json()
    return payload["AccessToken"], payload["User"]["Id"]


def add_movie_library(client: Jellyfin, name: str, path: str) -> None:
    """Create a movie library with every internet metadata and image fetcher disabled."""
    no_fetchers = {"Type": "Movie", "MetadataFetchers": [], "MetadataFetcherOrder": [],
                   "ImageFetchers": [], "ImageFetcherOrder": []}
    client.expect("POST", f"/Library/VirtualFolders?name={name}&collectionType=movies&refreshLibrary=true", (200, 204), {
        "LibraryOptions": {"PathInfos": [{"Path": path}], "EnableRealtimeMonitor": False,
                           "SaveLocalMetadata": False, "TypeOptions": [no_fetchers]}})


def wait_for_movies(client: Jellyfin, paths: set, timeout: float) -> dict:
    """Poll until every expected media path is a Movie item; return path -> item ID."""
    deadline = time.monotonic() + timeout
    found = {}
    while time.monotonic() < deadline:
        items = client.expect("GET", "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path", (200,)).json()["Items"]
        found = {item["Path"]: item["Id"] for item in items if item.get("Path") in paths}
        if len(found) == len(paths):
            return found
        time.sleep(3)
    missing = sorted(paths - set(found))
    raise TimeoutError(f"{len(missing)} synthetic movies were not indexed within {timeout:.0f}s: {missing}")
