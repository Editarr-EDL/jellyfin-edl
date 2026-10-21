"""Tests for the verification helper's pure parts. Run: python3 -m unittest discover -s <scripts dir>."""

from __future__ import annotations

import json
import stat
import subprocess
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import evidence
import instance
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
            run = {"runId": "jfedl-test", "scratch": str(root / "gone"), "stopped": True, "redacted": True}
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


SECRET = "synthetic-token-value"


class FakeDocker:
    """Docker CLI stand-in that tracks one run's containers and volumes. Docker is the mocked boundary."""

    def __init__(self, run_id, failing_removals=0):
        self.label = f"label={instance.LABEL}={run_id}"
        self.run_id = run_id
        self.containers = {"c1"}
        self.volumes = {f"{run_id}-config"}
        self.failing_removals = failing_removals

    def __call__(self, *args, check=True, log=None):
        out, code = "", 0
        if args[0] == "logs":
            out, code = (f"server log {SECRET}\n", 0) if self.containers else ("", 1)
        elif args[0] == "ps":
            out = "\n".join(sorted(self.containers))
        elif args[0] == "rm":
            if self.failing_removals:
                self.failing_removals -= 1
                code = 1
            else:
                self.containers.discard(args[-1])
        elif args[:2] == ("volume", "inspect"):
            out, code = (self.run_id, 0) if args[-1] in self.volumes else ("", 1)
        elif args[:2] == ("volume", "rm"):
            self.volumes.discard(args[-1])
        elif args[:2] == ("volume", "ls"):
            out = "\n".join(sorted(self.volumes))
        if check and code:
            raise RuntimeError(f"docker {args[0]} failed")
        return subprocess.CompletedProcess(args, code, out, "")


class FinishTests(unittest.TestCase):
    """finish() against a real scratch directory and evidence files, with Docker mocked."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name).resolve()
        (root / "tmp").mkdir()
        patcher = unittest.mock.patch.object(tempfile, "tempdir", str(root / "tmp"))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.evidence = root / "evidence"
        self.evidence.mkdir()
        self.run = {"runId": "jfedl-run-a", "container": "jfedl-run-a-jellyfin", "volumes": ["jfedl-run-a-config"],
                    "stopped": False}
        self.scratch = instance.new_scratch(self.run["runId"])
        self.run["scratch"] = str(self.scratch)
        evidence.private_file(self.scratch / "secrets.json", {"password": "synthetic-password", "token": SECRET})
        evidence.write_json(self.evidence / "drive.json", {"echo": SECRET})
        verify.save(self.evidence, self.run)

    def leaked(self):
        return [p.name for p in self.evidence.rglob("*") if p.is_file() and SECRET.encode() in p.read_bytes()]

    def finish_with(self, docker):
        with unittest.mock.patch.object(instance, "docker", docker):
            return verify.finish(self.evidence, evidence.read_json(self.evidence / "run.json"))

    def assert_retry_completes(self, docker):
        self.assertTrue((self.scratch / "secrets.json").exists(), "secrets kept until redaction completes")
        self.assertTrue(self.finish_with(docker))
        self.assertEqual(self.leaked(), [])
        self.assertFalse(self.scratch.exists())
        self.assertEqual(evidence.read_json(self.evidence / "redaction.json")["secrets"], 2)
        self.assertTrue(evidence.read_json(self.evidence / "cleanup.json")["clean"])

    def test_interruption_before_redaction_keeps_secrets_and_the_retry_redacts(self):
        docker = FakeDocker(self.run["runId"])
        with unittest.mock.patch.object(evidence, "redact", side_effect=OSError("interrupted")):
            with self.assertRaises(OSError):
                self.finish_with(docker)
        self.assertEqual(sorted(self.leaked()), ["drive.json", "jellyfin.log"])
        self.assert_retry_completes(docker)

    def test_interruption_during_redaction_keeps_secrets_and_the_retry_redacts(self):
        docker = FakeDocker(self.run["runId"])
        real_write = Path.write_bytes
        writes = []

        def interrupted_write(path, data):
            writes.append(path.name)
            if len(writes) > 1:
                raise OSError("interrupted mid-redaction")
            return real_write(path, data)

        with unittest.mock.patch.object(Path, "write_bytes", interrupted_write):
            with self.assertRaises(OSError):
                self.finish_with(docker)
        self.assertTrue(self.leaked(), "the interrupted pass left a secret behind")
        self.assert_retry_completes(docker)

    def test_failed_container_removal_keeps_scratch_and_earlier_reports_until_a_retry_succeeds(self):
        docker = FakeDocker(self.run["runId"], failing_removals=1)
        self.assertFalse(self.finish_with(docker))
        failed = evidence.read_json(self.evidence / "cleanup.json")
        self.assertFalse(failed["clean"])
        self.assertTrue(failed["errors"])
        self.assertTrue(self.scratch.exists())
        self.assert_retry_completes(docker)
        cleanup = evidence.read_json(self.evidence / "cleanup.json")
        self.assertEqual([attempt["clean"] for attempt in cleanup["earlierAttempts"]], [False])
        before = {path.name: path.read_bytes() for path in self.evidence.iterdir()}
        self.assertTrue(self.finish_with(docker))
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.evidence.iterdir()})


class ScratchOwnershipTests(unittest.TestCase):
    """instance.cleanup() refuses scratch it cannot prove belongs to this run, with Docker mocked."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        (self.root / "tmp").mkdir()
        patcher = unittest.mock.patch.object(tempfile, "tempdir", str(self.root / "tmp"))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.outside = self.root / "outside"
        (self.outside / "locked").mkdir(parents=True)
        self.sentinel = self.outside / "sentinel"
        self.sentinel.write_text("not this run's")
        self.sentinel.chmod(0o400)
        (self.outside / "locked").chmod(0o500)
        self.addCleanup((self.outside / "locked").chmod, 0o700)

    def cleanup(self, run_id, scratch):
        run = {"runId": run_id, "volumes": [f"{run_id}-config"], "scratch": str(scratch)}
        with unittest.mock.patch.object(instance, "docker", FakeDocker(run_id)):
            return instance.cleanup(run, self.root / "docker.log")

    def assert_outside_untouched(self):
        self.assertEqual(stat.S_IMODE(self.sentinel.stat().st_mode), 0o400)
        self.assertEqual(stat.S_IMODE((self.outside / "locked").stat().st_mode), 0o500)
        self.assertEqual(self.sentinel.read_text(), "not this run's")

    def test_refuses_another_runs_scratch(self):
        other = instance.new_scratch("jfedl-run-b")
        (other / "sentinel").write_text("run b")
        report = self.cleanup("jfedl-run-a", other)
        self.assertFalse(report["clean"])
        self.assertTrue(report["errors"])
        self.assertEqual((other / "sentinel").read_text(), "run b")

    def test_refuses_a_root_symlink_before_traversal(self):
        own = instance.new_scratch("jfedl-run-a")
        (own / "sentinel").write_text("target")
        (own / "sentinel").chmod(0o400)
        link = Path(tempfile.gettempdir()) / f"{instance.SCRATCH_PREFIX}link"
        link.symlink_to(own, target_is_directory=True)
        report = self.cleanup("jfedl-run-a", link)
        self.assertFalse(report["clean"])
        self.assertTrue(report["leftovers"]["scratchExists"])
        self.assertTrue(link.is_symlink())
        self.assertEqual(stat.S_IMODE((own / "sentinel").stat().st_mode), 0o400)

    def test_removes_own_scratch_without_following_nested_symlinks(self):
        own = instance.new_scratch("jfedl-run-a")
        (own / "media").mkdir()
        (own / "media" / "unreadable.edl").write_text("0\t1\t0\n")
        (own / "media" / "unreadable.edl").chmod(0)
        (own / "media" / "dir-link").symlink_to(self.outside, target_is_directory=True)
        (own / "media" / "file-link").symlink_to(self.sentinel)
        report = self.cleanup("jfedl-run-a", own)
        self.assertTrue(report["clean"], report)
        self.assertFalse(own.exists())
        self.assert_outside_untouched()
        self.assertEqual(self.cleanup("jfedl-run-a", own)["clean"], True)


if __name__ == "__main__":
    unittest.main()
