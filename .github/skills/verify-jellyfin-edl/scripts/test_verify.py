"""Tests for the verification helper's pure parts. Run: python3 -m unittest discover -s <scripts dir>."""

from __future__ import annotations

import json
import os
import shutil
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

    def test_contract_reference_agrees_with_every_case_of_both_formats(self):
        for case in library.live_cases(self.manifest):
            export_id = self.manifest["principals"][case["principal"]]["exportId"]
            expected = library.contract_expectation(case, export_id, self.manifest)
            self.assertEqual(expected["outcome"], case["expect"]["outcome"], case["name"])
            self.assertEqual(expected["payload"], case["expect"].get("payload"), case["name"])
            self.assertEqual(expected["format"], case["format"], case["name"])
        self.assertEqual(len(self.manifest["selection"]), 19)
        self.assertEqual([c["format"] for c in library.LOCAL_CASES], ["mcf"])

    def test_local_case_uses_the_manifest_user_a_export_id(self):
        user_a = self.manifest["principals"]["userA"]["exportId"]
        self.assertIn(user_a, library.LOCAL_CASES[0]["entries"][0]["path"])

    def test_the_other_format_ignores_a_cases_files(self):
        for case in library.live_cases(self.manifest):
            other = "mcf" if case["format"] == "edl" else "edl"
            export_id = self.manifest["principals"][case["principal"]]["exportId"]
            result = library.contract_expectation(case, export_id, self.manifest, fmt=other)
            if result["payload"] is not None:
                self.assertEqual(self.manifest["payloads"][result["payload"]]["format"], other, case["name"])
            elif not any(entry["path"].startswith(other + "/") for entry in case["entries"]):
                self.assertEqual(result["status"], 404, case["name"])
        independent = next(c for c in self.manifest["selection"] if c["name"] == "mcf-selected-independently-of-edl")
        user_a = self.manifest["principals"]["userA"]["exportId"]
        self.assertEqual(library.contract_expectation(independent, user_a, self.manifest, fmt="edl")["payload"], "edl-personal-a")
        self.assertEqual(library.contract_expectation(independent, user_a, self.manifest)["payload"], "mcf-default")

    def test_a_server_that_reads_mode_000_changes_only_the_unreadable_cases(self):
        changed = [case["name"] for case in library.live_cases(self.manifest)
                   if library.contract_expectation(case, self.manifest["principals"][case["principal"]]["exportId"],
                                                   self.manifest, server_reads_mode_000=True)["outcome"] != case["expect"]["outcome"]]
        self.assertEqual(changed, ["edl-unreadable-personal-does-not-fall-back", "local-mcf-unreadable-personal-does-not-fall-back"])

    def test_a_server_key_expects_only_the_default_whatever_personal_files_exist(self):
        by_name = {case["name"]: case for case in library.live_cases(self.manifest)}
        expected = {
            "edl-personal-before-default": ("default", "edl-default"),
            "edl-each-user-gets-own-file": ("default", "edl-default"),
            "edl-malformed-personal-does-not-fall-back": ("default", "edl-default"),
            "edl-unreadable-personal-does-not-fall-back": ("default", "edl-default"),
            "edl-directory-at-personal-path-is-invalid": ("default", "edl-default"),
            "version-1080p-has-personal": ("none", None),
            "edl-malformed-default": ("default_invalid", None),
            "edl-legacy-is-never-fallback": ("none", None),
            "mcf-personal-before-default": ("default", "mcf-default"),
            "mcf-each-user-gets-own-file": ("none", None),
            "mcf-malformed-personal-does-not-fall-back": ("default", "mcf-default"),
            "mcf-legacy-is-never-fallback": ("none", None),
            "local-mcf-unreadable-personal-does-not-fall-back": ("default", "mcf-default"),
        }
        for name, (outcome, payload) in expected.items():
            result = library.contract_expectation(by_name[name], None, self.manifest)
            self.assertEqual((result["outcome"], result["payload"]), (outcome, payload), name)

    def test_build_writes_exact_payload_bytes_and_modes_under_live_user_ids(self):
        live_a = "a" * 32
        export_ids = {self.manifest["principals"]["userA"]["exportId"]: live_a}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            video = root / "video.mkv"
            video.write_bytes(b"synthetic")
            written = library.build(self.manifest, FIXTURE, root / "media", video, export_ids)
            case = root / "media" / library.LIBRARY_DIR / "edl-unreadable-personal-does-not-fall-back"
            personal = case / "edl" / live_a / f"Movie.user-{live_a}.edl"
            self.assertEqual(stat.S_IMODE(personal.stat().st_mode), 0)
            self.assertEqual((case / "edl" / "Movie.edl").read_bytes(), (FIXTURE / "payloads" / "default.edl").read_bytes())
            local = root / "media" / library.LIBRARY_DIR / "local-mcf-unreadable-personal-does-not-fall-back"
            personal_mcf = local / "mcf" / live_a / f"Movie.user-{live_a}.mcf"
            self.assertEqual(stat.S_IMODE(personal_mcf.stat().st_mode), 0)
            self.assertEqual((local / "mcf" / "Movie.mcf").read_bytes(), (FIXTURE / "payloads" / "default.mcf").read_bytes())
            self.assertTrue((root / "media" / library.LIBRARY_DIR / "edl-directory-at-personal-path-is-invalid" / "edl"
                             / live_a / f"Movie.user-{live_a}.edl").is_dir())
            self.assertFalse(any("5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d" in path for path in written))
            versions = root / "media" / library.LIBRARY_DIR / "Movie"
            self.assertEqual(sorted(p.name for p in versions.glob("*.mkv")), sorted(library.VERSION_MEDIA))
            self.assertTrue((versions / "edl" / live_a / f"Movie - 1080p.user-{live_a}.edl").is_file())
            restricted = root / "media" / library.RESTRICTED_DIR / library.RESTRICTED_CASES[0]
            self.assertTrue((restricted / "Movie.mkv").is_file())
            self.assertTrue((restricted / "edl" / live_a / f"Movie.user-{live_a}.edl").is_file())
            self.assertTrue((restricted / "mcf" / live_a / f"Movie.user-{live_a}.mcf").is_file())
            cases = library.live_cases(self.manifest)
            by_name = {c["name"]: c for c in cases}
            self.assertEqual(len(written), 20 + sum(len(c["entries"]) for c in cases)
                             + 2 + len(by_name[library.VERSIONS_CASE]["entries"])
                             + 1 + sum(len(by_name[name]["entries"]) for name in library.RESTRICTED_CASES))
            personal.chmod(0o600)
            personal_mcf.chmod(0o600)


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
                items = [{"Path": "/media/a.mkv", "Id": "1", "MediaSources": [{"Path": "/media/a.mkv", "Id": "s1"}]}] \
                    if scans[-1] == "Completed" else []
                return jellyfin_api.Response(200, {}, json.dumps({"Items": items}).encode())

        with unittest.mock.patch.object(jellyfin_api.time, "sleep"):
            found = jellyfin_api.wait_for_movies(Server("http://127.0.0.1:1", "test"), {"/media/a.mkv"}, 60)
        self.assertEqual(found, {"/media/a.mkv": {"item": "1", "source": "s1"}})
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
PASSWORD = "synthetic-password"


def identity_of(path):
    status = os.lstat(path)
    return {"dev": status.st_dev, "inode": status.st_ino}


class FakeDocker:
    """Docker CLI stand-in that tracks one run's containers and volumes. Docker is the mocked boundary."""

    def __init__(self, run_id, fail_once=()):
        self.run_id = run_id
        self.containers = {"c1"}
        self.volumes = {f"{run_id}-config"}
        self.fail_once = set(fail_once)

    def __call__(self, *args, check=True, log=None):
        out, code = "", 0
        command = " ".join(args[:2]) if args[0] == "volume" else args[0]
        if command in self.fail_once:
            self.fail_once.discard(command)
            code = 1
        elif args[0] == "logs":
            out, code = (f"server log {SECRET}\n", 0) if self.containers else ("Error: No such container", 1)
        elif args[0] == "ps":
            out = "\n".join(sorted(self.containers))
        elif args[0] == "rm":
            self.containers.discard(args[-1])
        elif command == "volume inspect":
            out, code = (self.run_id, 0) if args[-1] in self.volumes else ("", 1)
        elif command == "volume rm":
            self.volumes.discard(args[-1])
        elif command == "volume ls":
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
        self.run["scratchIdentity"] = identity_of(self.scratch)
        evidence.private_file(self.scratch / "secrets.json", {"password": PASSWORD, "token": SECRET})
        evidence.write_json(self.evidence / "drive.json", {"echo": SECRET})
        verify.save(self.evidence, self.run)

    def leaked(self):
        return [p.name for p in self.evidence.rglob("*")
                if p.is_file() and any(s.encode() in p.read_bytes() for s in (SECRET, PASSWORD))]

    def finish_with(self, docker):
        with unittest.mock.patch.object(instance, "docker", docker):
            return verify.finish(self.evidence, evidence.read_json(self.evidence / "run.json"))

    def snapshot(self):
        return {path.name: path.read_bytes() for path in self.evidence.iterdir()}

    def error_run(self):
        """The in-memory run as command_run holds it after an error that echoed both secrets."""
        run = evidence.read_json(self.evidence / "run.json")
        run["error"] = f"RuntimeError: sign-in with {PASSWORD} returned token {SECRET}"
        run["detail"] = {"attempts": [{"response": f"AccessToken={SECRET}"}]}
        return run

    def assert_final_metadata_clean(self):
        self.assertEqual(self.leaked(), [])
        persisted = evidence.read_json(self.evidence / "run.json")
        self.assertEqual(persisted["error"], "RuntimeError: sign-in with [REDACTED] returned token [REDACTED]")
        self.assertEqual(persisted["detail"], {"attempts": [{"response": "AccessToken=[REDACTED]"}]})
        self.assertTrue(persisted["redacted"] and persisted["stopped"])
        self.assertTrue(evidence.read_json(self.evidence / "cleanup.json")["clean"])
        self.assertFalse(self.scratch.exists())

    def assert_secret_free(self, run):
        self.assertNotIn(SECRET, json.dumps(run))
        self.assertNotIn(PASSWORD, json.dumps(run))

    def test_an_error_naming_both_secrets_is_redacted_in_every_final_file(self):
        run = self.error_run()
        docker = FakeDocker(self.run["runId"])
        with unittest.mock.patch.object(instance, "docker", docker):
            self.assertTrue(verify.finish(self.evidence, run))
        self.assert_final_metadata_clean()
        self.assert_secret_free(run)
        before = self.snapshot()
        self.assertTrue(self.finish_with(docker))
        self.assertEqual(before, self.snapshot())

    def test_an_error_saved_before_cleanup_is_redacted_in_every_final_file(self):
        verify.save(self.evidence, self.error_run())
        self.assertTrue(self.finish_with(FakeDocker(self.run["runId"])))
        self.assert_final_metadata_clean()

    def test_an_interrupted_redaction_of_a_secret_error_retries_to_clean_metadata(self):
        run = self.error_run()
        docker = FakeDocker(self.run["runId"])
        with unittest.mock.patch.object(instance, "docker", docker):
            with unittest.mock.patch.object(evidence, "redact", side_effect=OSError("interrupted")):
                with self.assertRaises(OSError):
                    verify.finish(self.evidence, run)
        self.assert_secret_free(run)
        self.assertFalse(evidence.read_json(self.evidence / "run.json")["redacted"])
        self.assert_retry_completes(docker)
        self.assert_final_metadata_clean()
        before = self.snapshot()
        self.assertTrue(self.finish_with(docker))
        self.assertEqual(before, self.snapshot())

    def test_a_failed_final_rmdir_is_retried_to_a_clean_finish(self):
        docker = FakeDocker(self.run["runId"])
        real_rmdir = Path.rmdir

        def interrupted_rmdir(path):
            if path == self.scratch:
                raise OSError("interrupted before the final rmdir")
            return real_rmdir(path)

        with unittest.mock.patch.object(Path, "rmdir", interrupted_rmdir):
            self.assertFalse(self.finish_with(docker))
        self.assertEqual(list(self.scratch.iterdir()), [])
        self.assertTrue(self.finish_with(docker))
        self.assertFalse(self.scratch.exists())
        self.assertEqual(self.leaked(), [])
        cleanup = evidence.read_json(self.evidence / "cleanup.json")
        self.assertTrue(cleanup["clean"])
        self.assertEqual([attempt["clean"] for attempt in cleanup["earlierAttempts"]], [False])
        before = self.snapshot()
        self.assertTrue(self.finish_with(docker))
        self.assertEqual(before, self.snapshot())

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
        docker = FakeDocker(self.run["runId"], fail_once={"rm"})
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

    def test_a_retry_after_the_container_is_gone_keeps_the_captured_server_log(self):
        docker = FakeDocker(self.run["runId"], fail_once={"volume rm"})
        self.assertFalse(self.finish_with(docker))
        captured = (self.evidence / "jellyfin.log").read_bytes()
        self.assertIn(b"server log [REDACTED]", captured)
        self.assert_retry_completes(docker)
        self.assertEqual((self.evidence / "jellyfin.log").read_bytes(), captured)

    def test_a_stopped_run_whose_secrets_vanished_before_redaction_is_not_a_success(self):
        evidence.write_json(self.evidence / "cleanup.json", {"clean": True, "steps": [], "errors": []})
        verify.save(self.evidence, dict(self.run, stopped=True))
        shutil.rmtree(self.scratch)
        with self.assertRaises(RuntimeError):
            self.finish_with(FakeDocker(self.run["runId"]))
        self.assertEqual(self.leaked(), ["drive.json"])
        self.assertFalse((self.evidence / "jellyfin.log").exists(), "no log captured that could not be redacted")

    def test_a_retry_without_secrets_does_not_capture_an_unredactable_log(self):
        evidence.write_json(self.evidence / "drive.json", {"echo": "redacted earlier"})
        verify.save(self.evidence, dict(self.run, redacted=True))
        shutil.rmtree(self.scratch)
        self.assertTrue(self.finish_with(FakeDocker(self.run["runId"])))
        self.assertEqual(self.leaked(), [])
        self.assertFalse((self.evidence / "jellyfin.log").exists())


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

    def cleanup(self, run_id, scratch, identity=None):
        if identity is None and os.path.lexists(scratch):
            identity = identity_of(scratch)
        run = {"runId": run_id, "volumes": [f"{run_id}-config"], "scratch": str(scratch), "scratchIdentity": identity}
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

    def test_a_partial_scratch_removal_is_reported_and_a_retry_finishes_it(self):
        own = instance.new_scratch("jfedl-run-a")
        (own / "media").mkdir()
        (own / "media" / "file").write_text("x")
        (own / "media").chmod(0o500)
        first = self.cleanup("jfedl-run-a", own)
        self.assertFalse(first["clean"])
        self.assertTrue(first["errors"])
        self.assertTrue(own.exists())
        (own / "media").chmod(0o700)
        self.assertTrue(self.cleanup("jfedl-run-a", own)["clean"])
        self.assertFalse(own.exists())

    def test_refuses_a_markerless_scratch_that_is_not_empty(self):
        own = instance.new_scratch("jfedl-run-a")
        (own / instance.SCRATCH_MARKER).unlink()
        (own / "sentinel").write_text("kept")
        self.assertFalse(self.cleanup("jfedl-run-a", own)["clean"])
        self.assertEqual((own / "sentinel").read_text(), "kept")

    def test_refuses_a_directory_that_replaced_the_recorded_scratch(self):
        own = instance.new_scratch("jfedl-run-a")
        recorded = identity_of(own)
        shutil.rmtree(own)
        own.mkdir()
        self.assertNotEqual(identity_of(own), recorded)
        self.assertFalse(self.cleanup("jfedl-run-a", own, recorded)["clean"], "empty and markerless")
        self.assertTrue(own.is_dir())
        (own / instance.SCRATCH_MARKER).write_text("jfedl-run-a")
        (own / "sentinel").write_text("someone else's")
        self.assertFalse(self.cleanup("jfedl-run-a", own, recorded)["clean"], "carrying a copied marker")
        self.assertEqual((own / "sentinel").read_text(), "someone else's")


if __name__ == "__main__":
    unittest.main()
