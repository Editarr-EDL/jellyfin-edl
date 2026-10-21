"""Tests for the verification helper's pure parts. Run: python3 -m unittest discover -s <scripts dir>."""

from __future__ import annotations

import json
import stat
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import evidence
import jellyfin_api
import library
import verify
from verify import FIXTURE


class LibraryTests(unittest.TestCase):
    def setUp(self):
        self.manifest = library.load_manifest(FIXTURE)

    def test_refuses_a_manifest_that_is_not_the_pinned_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "manifest.json").write_bytes((FIXTURE / "manifest.json").read_bytes() + b" ")
            with self.assertRaises(ValueError):
                library.load_manifest(Path(directory))

    def test_legacy_rule_reads_only_the_adjacent_sidecar(self):
        expected = {case["name"]: library.legacy_expectation(case, self.manifest)["status"]
                    for case in library.edl_cases(self.manifest)}
        self.assertEqual(len(expected), 14)
        self.assertEqual(expected["edl-personal-before-default"], 200)
        self.assertEqual(expected["edl-legacy-is-never-fallback"], 200)
        self.assertEqual(expected["edl-malformed-personal-does-not-fall-back"], 404)
        self.assertEqual(expected["version-2160p-uses-its-own-default"], 404)

    def test_build_writes_exact_payload_bytes_and_modes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            video = root / "video.mkv"
            video.write_bytes(b"synthetic")
            written = library.build(self.manifest, FIXTURE, root / "media", video)
            case = root / "media" / library.LIBRARY_DIR / "edl-unreadable-personal-does-not-fall-back"
            personal = next(case.glob("edl/*/Movie.user-*.edl"))
            self.assertEqual(stat.S_IMODE(personal.stat().st_mode), 0)
            self.assertEqual((case / "edl" / "Movie.edl").read_bytes(), (FIXTURE / "payloads" / "default.edl").read_bytes())
            self.assertTrue((root / "media" / library.LIBRARY_DIR / "edl-directory-at-personal-path-is-invalid" / "edl"
                             / "5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d" / "Movie.user-5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d.edl").is_dir())
            self.assertEqual(len(written), 14 + sum(len(c["entries"]) for c in library.edl_cases(self.manifest)))
            personal.chmod(0o600)


class ReadinessTests(unittest.TestCase):
    def test_public_info_alone_is_not_ready(self):
        health = [b"Jellyfin Server is loading.", b"Healthy"]
        requests = []

        class Server(jellyfin_api.Jellyfin):
            def request(self, method, path, body=None, authenticated=True):
                requests.append(path)
                if path == "/health":
                    text = health.pop(0)
                    return jellyfin_api.Response(200 if text == b"Healthy" else 503, {}, text)
                return jellyfin_api.Response(200, {}, b'{"Version": "12.0.0"}')

        with unittest.mock.patch.object(jellyfin_api.time, "sleep"):
            info = jellyfin_api.wait_ready(Server("http://127.0.0.1:1", "test"), 60)
        self.assertEqual(info, {"Version": "12.0.0"})
        self.assertEqual(requests, ["/health", "/health", "/System/Info/Public"])

    def test_an_idle_scan_with_missing_movies_requests_another_scan(self):
        scans = ["Idle"]
        requests = []

        class Server(jellyfin_api.Jellyfin):
            def request(self, method, path, body=None, authenticated=True):
                requests.append(f"{method} {path.split('?')[0]}")
                if path == "/ScheduledTasks":
                    return jellyfin_api.Response(200, {}, json.dumps([{"Key": "RefreshLibrary", "State": scans[-1]}]).encode())
                if path == "/Library/Refresh":
                    scans.append("Completed")
                    return jellyfin_api.Response(204, {}, b"")
                items = [{"Path": "/media/a.mkv", "Id": "1"}] if scans[-1] == "Completed" else []
                return jellyfin_api.Response(200, {}, json.dumps({"Items": items}).encode())

        with unittest.mock.patch.object(jellyfin_api.time, "sleep"):
            found = jellyfin_api.wait_for_movies(Server("http://127.0.0.1:1", "test"), {"/media/a.mkv"}, 60)
        self.assertEqual(found, {"/media/a.mkv": "1"})
        self.assertEqual(requests, ["GET /Items", "GET /ScheduledTasks", "POST /Library/Refresh", "GET /Items"])


class RedactionTests(unittest.TestCase):
    def test_repeated_cleanup_keeps_the_first_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            run = {"runId": "jfedl-test", "scratch": str(root / "gone"), "stopped": True}
            evidence.write_json(root / "run.json", run)
            evidence.write_json(root / "cleanup.json", {"clean": True, "steps": ["first"]})
            evidence.write_json(root / "redaction.json", {"files": 10, "replacements": 0, "secrets": 2})
            before = {path.name: path.read_bytes() for path in root.iterdir()}
            self.assertTrue(verify.finish(root, run))
            self.assertEqual(before, {path.name: path.read_bytes() for path in root.iterdir()})
    def test_replaces_every_secret_and_reports_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "nested").mkdir()
            (root / "a.log").write_text("token=s3cret-token s3cret-token")
            (root / "nested" / "b.json").write_text(json.dumps({"pw": "hunter-two"}))
            report = evidence.redact(root, ["s3cret-token", "hunter-two", ""])
            self.assertEqual(report, {"files": 2, "replacements": 3, "secrets": 2})
            self.assertEqual((root / "a.log").read_text(), "token=[REDACTED] [REDACTED]")
            self.assertNotIn("hunter-two", (root / "nested" / "b.json").read_text())


if __name__ == "__main__":
    unittest.main()
