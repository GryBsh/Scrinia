import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bridge import WorkerBridge, BridgeError, validate_request

FIXTURE = Path(__file__).parent / "fixtures" / "fake_worker.py"

def request(root):
    root = Path(root).resolve()
    return {"version": 1, "operation": "retrieve", "operationId": "a" * 32,
            "configuration": {"endpoint": "https://example.invalid/v1", "model": "fixture",
                "apiKeyEnvironmentVariable": "SCRINIA_TEST_KEY", "temperature": 0.6,
                "topP": 0.95, "topK": 20, "thinking": True, "maxOutputTokens": 16384,
                "networkTimeoutSeconds": 180, "episodeSeconds": 300,
                "maxModelRequests": 40, "maxAttempts": 5},
            "paths": {"root": str(root), "workspace": str(root / "workspace"),
                "home": str(root / "home"), "eventLogPath": str(root / "events.jsonl")},
            "snapshotPath": str(root / "snapshot"), "question": "Synthetic question?",
            "questionImage": None}

class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
    def bridge(self, mode):
        return WorkerBridge([sys.executable, str(FIXTURE), mode], self.root / "operations",
                            cleanup_seconds=0.5)
    def test_accepts_only_valid_output_after_successful_exit(self):
        result = self.bridge("success").run("retrieve", request(self.root), threading.Event())
        self.assertEqual(result["evidence"], [])
    def test_rejects_id_mismatch_nonzero_missing_partial_and_unknown_output(self):
        for mode in ("mismatch", "nonzero", "missing", "partial", "unknown", "version"):
            with self.subTest(mode=mode), self.assertRaises(BridgeError):
                self.bridge(mode).run("retrieve", request(self.root), threading.Event())
    def test_rejects_unknown_operation_version_and_forbidden_fields(self):
        for field, value in (("operation", "grade"), ("version", 2), ("goldAnswer", "secret"),
                             ("questionId", "case"), ("category", "foo"),
                             ("readerResponse", "answer"), ("graderConfiguration", {})):
            body = request(self.root)
            body[field] = value
            with self.subTest(field=field), self.assertRaises(BridgeError):
                validate_request(body)
    def test_rejects_credentials_nested_and_wrong_operation(self):
        body = request(self.root)
        body["configuration"]["apiKey"] = "sentinel-secret"
        with self.assertRaises(BridgeError):
            self.bridge("success").run("retrieve", body, threading.Event())
        with self.assertRaises(BridgeError):
            self.bridge("success").run("learn", request(self.root), threading.Event())
        self.assertFalse((self.root / "operations").exists())
    def test_cancellation_waits_for_owned_worker_and_child_exit(self):
        event = threading.Event()
        timer = threading.Timer(0.3, event.set)
        timer.start()
        self.addCleanup(timer.cancel)
        with self.assertRaisesRegex(BridgeError, "CANCELLED"):
            self.bridge("cancel").run("retrieve", request(self.root), event)
        self.assertTrue((self.root / "child-exited").exists())
        self.assertTrue((self.root / "worker-exited").exists())
    def test_uncooperative_worker_is_terminated_and_failure_recorded(self):
        event = threading.Event()
        timer = threading.Timer(0.2, event.set)
        timer.start()
        self.addCleanup(timer.cancel)
        started = time.monotonic()
        with self.assertRaisesRegex(BridgeError, "CANCELLED"):
            self.bridge("ignore-cancel").run("retrieve", request(self.root), event)
        self.assertLess(time.monotonic() - started, 5)
        failures = list((self.root / "operations").glob("*/failure.json"))
        self.assertEqual(json.loads(failures[0].read_text())["code"], "CANCELLED")

if __name__ == "__main__":
    unittest.main()
