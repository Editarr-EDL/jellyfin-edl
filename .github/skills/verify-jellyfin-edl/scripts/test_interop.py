"""Tests for the caller-supplied request matrix and the interop launch options."""

from __future__ import annotations

import contextlib
import hashlib
import io
import json
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import instance
import jellyfin_api
import matrix
import verify

ITEMS = {"/m/A/A.mkv": {"item": "i1", "source": "s1"}, "/m/B/B.mkv": {"item": "i2", "source": "s2"}}
OWN = b"1 2 3\n"
OWN_SHA = hashlib.sha256(OWN).hexdigest()
PRIVATE = {"Content-Type": "text/plain; charset=utf-8", "Cache-Control": "private, no-cache", "ETag": '"e1"'}


def case(**fields):
    return {"name": "c", "principal": "userA", "format": "edl", "media": "/m/A/A.mkv", "status": 200, "sha256": [OWN_SHA], **fields}


class FakeClient:
    def __init__(self, answers):
        self.answers = list(answers)
        self.requests = []

    def request(self, method, path, authenticated=True, headers=None):
        self.requests.append((path, headers))
        return self.answers.pop(0) if len(self.answers) > 1 else self.answers[0]


class LoadCasesTests(unittest.TestCase):
    def test_accepts_a_complete_case(self):
        self.assertEqual(matrix.load_cases({"cases": [case()]}, ITEMS)[0]["name"], "c")

    def test_refuses_defects_before_any_request(self):
        bad = [
            {"cases": []},
            {"cases": [case(), case()]},
            {"cases": [case(principal="userC")]},
            {"cases": [case(media="/elsewhere.mkv")]},
            {"cases": [case(version="/elsewhere.mkv")]},
            {"cases": [case(status=404)]},
            {"cases": [case(sha256=[])]},
            {"cases": [case(ifNoneMatch="later")]},
            {"cases": [case(until="0" * 64)]},
            {"cases": [case(extra=1)]},
            {"cases": [case(untilChanged=False)]},
            {"cases": [case(untilChanged=True, until=OWN_SHA)]},
        ]
        for document in bad:
            with self.assertRaises(ValueError, msg=document):
                matrix.load_cases(document, ITEMS)


class RunTests(unittest.TestCase):
    def test_a_matching_body_passes_and_another_users_bytes_fail(self):
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)])
        [passed] = matrix.run({"userA": client}, ITEMS, [case()])
        self.assertTrue(passed["ok"])
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, b"0 9 3\n")])
        [failed] = matrix.run({"userA": client}, ITEMS, [case()])
        self.assertFalse(failed["ok"])

    def test_a_default_must_not_answer_an_expected_error(self):
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)])
        [result] = matrix.run({"userA": client}, ITEMS, [case(status=422, sha256=None)])
        self.assertFalse(result["ok"])

    def test_a_missing_private_cache_header_fails(self):
        client = FakeClient([jellyfin_api.Response(200, {"Content-Type": "text/plain"}, OWN)])
        self.assertFalse(matrix.run({"userA": client}, ITEMS, [case()])[0]["ok"])

    def test_the_mcf_route_needs_vtt(self):
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)])
        self.assertFalse(matrix.run({"userA": client}, ITEMS, [case(format="mcf")])[0]["ok"])

    def test_version_and_earlier_etag_are_sent(self):
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN), jellyfin_api.Response(304, PRIVATE, b"")])
        cases = [case(version="/m/B/B.mkv"), case(name="again", ifNoneMatch="c", status=304, sha256=None)]
        results = matrix.run({"userA": client}, ITEMS, cases)
        self.assertEqual([result["ok"] for result in results], [True, True])
        self.assertEqual(client.requests[0], ("/Plugins/Edl/ByItem/i1?mediaSourceId=s2", {}))
        self.assertEqual(client.requests[1][1], {"If-None-Match": '"e1"'})

    def test_until_repeats_while_old_bytes_serve_and_records_every_digest(self):
        new = b"5 6 3\n"
        new_sha = hashlib.sha256(new).hexdigest()
        answers = [jellyfin_api.Response(200, PRIVATE, OWN)] * 3 + [jellyfin_api.Response(200, PRIVATE, new)]
        client = FakeClient(answers)
        [result] = matrix.run({"userA": client}, ITEMS, [case(sha256=[OWN_SHA, new_sha], until=new_sha)], pause=lambda _: None)
        self.assertTrue(result["ok"])
        self.assertEqual(result["answers"], 4)
        self.assertEqual(result["digestsSeen"], sorted([OWN_SHA, new_sha]))

    def test_until_fails_on_a_gap_or_a_timeout(self):
        new_sha = "f" * 64
        gap = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN), jellyfin_api.Response(404, PRIVATE, b"")])
        [result] = matrix.run({"userA": gap}, ITEMS, [case(sha256=[OWN_SHA, new_sha], until=new_sha)], pause=lambda _: None)
        self.assertFalse(result["ok"])
        self.assertEqual(result["statusesSeen"], [200, 404])
        ticks = iter(range(1000))
        stuck = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)])
        [result] = matrix.run({"userA": stuck}, ITEMS, [case(sha256=[OWN_SHA, new_sha], until=new_sha, seconds=5)],
                              clock=lambda: next(ticks), pause=lambda _: None)
        self.assertFalse(result["ok"])

    def test_until_changed_accepts_one_switch_and_rejects_a_gap(self):
        new = b"5 6 3\n"
        switch = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)] * 2 + [jellyfin_api.Response(200, PRIVATE, new)])
        [result] = matrix.run({"admin": switch}, ITEMS, [case(principal="admin", untilChanged=True)], pause=lambda _: None)
        self.assertTrue(result["ok"])
        self.assertEqual(result["bodySha256"], hashlib.sha256(new).hexdigest())
        gap = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN), jellyfin_api.Response(404, PRIVATE, b""),
                          jellyfin_api.Response(200, PRIVATE, new)])
        [result] = matrix.run({"admin": gap}, ITEMS, [case(principal="admin", untilChanged=True)], pause=lambda _: None)
        self.assertFalse(result["ok"])
        ticks = iter(range(1000))
        stuck = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)])
        [result] = matrix.run({"admin": stuck}, ITEMS, [case(principal="admin", untilChanged=True, seconds=3)],
                              clock=lambda: next(ticks), pause=lambda _: None)
        self.assertFalse(result["ok"])

    def test_a_repeating_case_records_when_each_answer_run_was_sent_and_answered(self):
        new = b"5 6 3\n"
        switch = FakeClient([jellyfin_api.Response(200, PRIVATE, OWN)] * 2 + [jellyfin_api.Response(200, PRIVATE, new)])
        ticks = iter(range(100, 200))
        [result] = matrix.run({"admin": switch}, ITEMS, [case(principal="admin", untilChanged=True)],
                              pause=lambda _: None, now=lambda: next(ticks))
        self.assertEqual(result["timeline"], [
            {"status": 200, "bodySha256": OWN_SHA, "answers": 2, "firstSentAt": 100, "firstReceivedAt": 101,
             "lastSentAt": 102, "lastReceivedAt": 103},
            {"status": 200, "bodySha256": hashlib.sha256(new).hexdigest(), "answers": 1, "firstSentAt": 104,
             "firstReceivedAt": 105, "lastSentAt": 104, "lastReceivedAt": 105},
        ])

    def test_until_changed_must_start_on_an_accepted_digest(self):
        client = FakeClient([jellyfin_api.Response(200, PRIVATE, b"other\n")])
        [result] = matrix.run({"admin": client}, ITEMS, [case(principal="admin", untilChanged=True)], pause=lambda _: None)
        self.assertFalse(result["ok"])

    def test_a_connection_failure_is_a_recorded_gap_not_a_crash(self):
        class Refusing:
            def request(self, *args, **kwargs):
                raise ConnectionResetError("reset by peer")

        [result] = matrix.run({"admin": Refusing()}, ITEMS, [case(principal="admin", untilChanged=True)], pause=lambda _: None)
        self.assertFalse(result["ok"])
        self.assertIn("ConnectionResetError", result["error"])


class InteropOptionTests(unittest.TestCase):
    def test_the_tree_must_be_an_absolute_directory_without_links(self):
        with tempfile.TemporaryDirectory() as directory:
            real = Path(directory).resolve()
            self.assertEqual(verify.interop_tree(str(real)), real)
            (real / "link").symlink_to(real)
            for value in ("relative", str(real / "missing"), str(real / "link")):
                with self.assertRaises(ValueError):
                    verify.interop_tree(value)

    def test_labels_add_to_the_run_label(self):
        with unittest.mock.patch.object(instance, "EXTRA_LABELS", ["mu44.task=mu-44"]):
            self.assertEqual(instance.labels("r1"), ["--label", "jfedl.run=r1", "--label", "mu44.task=mu-44"])

    def test_an_interop_container_mounts_the_tree_at_its_own_path_on_the_requested_port(self):
        calls = []

        def docker(*args, **_):
            calls.append(args)
            return unittest.mock.Mock(stdout="127.0.0.1:9221\n")

        run = {"runId": "r1", "container": "r1-jellyfin", "volumes": ["r1-config", "r1-cache"], "scratch": "/s",
               "interop": "/tree", "hostPort": 9221}
        with unittest.mock.patch.object(instance, "docker", docker):
            self.assertEqual(instance.start(run, Path("/dev/null")), 9221)
        create = next(args for args in calls if args[0] == "create")
        self.assertIn("/tree:/tree:ro", create)
        self.assertIn("127.0.0.1:9221:8096", create)

    def test_an_unknown_launch_option_or_label_is_usage(self):
        self.assertEqual(verify.main(["launch", "--bogus", "x"]), 64)
        self.assertEqual(verify.main(["launch", "--label", "novalue"]), 64)
        self.assertEqual(verify.main(["matrix", "a", "b"]), 64)
        self.assertEqual(verify.main(["cleanup-label", "novalue"]), 64)


class LabelledRecoveryTests(unittest.TestCase):
    """A caller that killed a launch reclaims it by the label it passed."""

    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: __import__("shutil").rmtree(self.root))
        patch = unittest.mock.patch.object(verify, "EVIDENCE_ROOT", self.root)
        patch.start()
        self.addCleanup(patch.stop)
        self.finished = []

    def record(self, name, labels, **fields):
        (self.root / name).mkdir()
        (self.root / name / "run.json").write_text(json.dumps({"runId": name, "labels": labels, **fields}))

    def cleanup(self, label, finish=lambda directory, run: True, docker=None):
        def finishing(directory, run):
            self.finished.append(directory.name)
            return finish(directory, run)

        output = io.StringIO()
        docker = docker or (lambda *args, **_: unittest.mock.Mock(returncode=0, stdout=""))
        with unittest.mock.patch.object(verify, "finish", finishing), \
                unittest.mock.patch.object(instance, "docker", docker), contextlib.redirect_stdout(output):
            code = verify.main(["cleanup-label", label])
        return code, output.getvalue()

    def test_only_runs_carrying_the_label_are_finished(self):
        self.record("jfedl-1-a", ["editarr.verify.run=x", "mu44.task=mu-44"], container="jfedl-1-a-jellyfin")
        self.record("jfedl-2-b", ["editarr.verify.run=y"], container="jfedl-2-b-jellyfin")
        code, output = self.cleanup("editarr.verify.run=x")
        self.assertEqual((code, self.finished), (0, ["jfedl-1-a"]))
        self.assertIn(f"evidence: {self.root / 'jfedl-1-a'}", output)

    def test_a_failed_finish_or_a_labelled_leftover_is_not_clean(self):
        self.record("jfedl-1-a", ["editarr.verify.run=x"], container="jfedl-1-a-jellyfin")

        def broken(directory, run):
            raise RuntimeError("scratch gone")

        self.assertEqual(self.cleanup("editarr.verify.run=x", finish=broken)[0], 1)
        leftover = lambda *args, **_: unittest.mock.Mock(returncode=0, stdout="abc\n" if args[0] == "ps" else "")
        code, output = self.cleanup("editarr.verify.run=x", docker=leftover)
        self.assertEqual(code, 1)
        self.assertIn("abc", output)

    def test_a_run_blocked_before_any_container_needs_no_daemon(self):
        self.record("jfedl-1-a", ["editarr.verify.run=x"])
        unreachable = lambda *args, **_: unittest.mock.Mock(returncode=1, stdout="")
        self.assertEqual(self.cleanup("editarr.verify.run=x", docker=unreachable)[0], 0)

    def test_launch_names_its_evidence_before_it_starts_anything(self):
        output = io.StringIO()

        def launching(directory, run):
            self.assertIn(f"evidence: {directory}", output.getvalue())
            raise instance.Blocked("docker daemon is not reachable")

        with unittest.mock.patch.object(verify, "launch", launching), \
                unittest.mock.patch.object(verify, "finish", lambda directory, run: True), \
                unittest.mock.patch.object(verify, "git", lambda *args: "c"), \
                unittest.mock.patch.object(instance, "EXTRA_LABELS", []), contextlib.redirect_stdout(output):
            self.assertEqual(verify.main(["launch", "--label", "editarr.verify.run=x"]), 2)
        [directory] = self.root.iterdir()
        self.assertIn("editarr.verify.run=x", json.loads((directory / "run.json").read_text())["labels"])


if __name__ == "__main__":
    unittest.main()
