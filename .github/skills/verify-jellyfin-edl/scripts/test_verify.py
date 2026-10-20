"""Tests for the verification helper's pure parts. Run: python3 -m unittest discover -s <scripts dir>."""

from __future__ import annotations

import json
import stat
import tempfile
import unittest
from pathlib import Path

import evidence
import library
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


class RedactionTests(unittest.TestCase):
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
