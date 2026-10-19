import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
UPSTREAM = Path(os.environ.get("LONGMEMEVAL_V2_UPSTREAM", ROOT / "artifacts/longmemeval-v2/upstream"))
sys.path.insert(0, str(UPSTREAM))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scrinia_memory import ScriniaMemory
from memory_modules.memory import build_memory
from bridge import BridgeError, validate_request

class MemoryContractTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
    def memory(self, **kwargs):
        return build_memory({"memory_type": "scrinia", "memory_params":
            {"run_root": str(self.root), "worker_command": ["fixture-worker"], **kwargs}})
    def test_external_registration_and_unique_instance_paths(self):
        first, second = self.memory(), self.memory()
        self.assertIsInstance(first, ScriniaMemory)
        self.assertNotEqual(first.paths["workspace"], second.paths["workspace"])
        self.assertNotEqual(first.paths["home"], second.paths["home"])
        self.assertTrue(Path(first.paths["workspace"]).is_relative_to(self.root))
        self.assertTrue(Path(first.paths["home"]).is_relative_to(self.root))
    def test_requires_explicit_root_and_rejects_credential_values(self):
        for params in ({}, {"run_root": str(self.root), "api_key": "sentinel"},
                       {"run_root": str(self.root), "controller": {"apiKey": "sentinel"}}):
            with self.subTest(params=params), self.assertRaises((ValueError, BridgeError)):
                ScriniaMemory(params)
    def test_runtime_cancel_event_is_not_persisted_or_sent(self):
        memory = self.memory()
        event = threading.Event()
        memory.configure_runtime(cancel_event=event, query_trace_dir=self.root / "upstream-traces",
                                 generation_temperature=0.3, generation_top_p=0.8)
        captured = {}
        def run(operation, body, cancel):
            captured.update(body)
            self.assertIs(cancel, event)
            return {"evidence": [{"type": "text", "value": "Synthetic evidence."}]}
        with (patch.dict(os.environ, {"SCRINIA_CONTROLLER_API_KEY": "sentinel-credential-value"}),
              patch.object(memory.bridge, "run", side_effect=run)):
            memory.snapshot_path = str(Path(memory.paths["root"]) / "snapshot")
            self.assertEqual(memory.query("Synthetic question?")[0]["value"], "Synthetic evidence.")
        validate_request(captured)
        self.assertNotIn("cancel_event", memory.memory_config["memory_params"])
        self.assertEqual(captured["configuration"]["temperature"], 0.6)
        self.assertEqual(captured["configuration"]["topP"], 0.95)
        self.assertNotIn("upstream-traces", json.dumps(captured))
        self.assertNotIn("apiKey", captured["configuration"])
        self.assertNotIn("sentinel-credential-value", json.dumps(captured))
    def test_question_image_is_owned_and_hashed_and_remote_images_refused(self):
        memory = self.memory()
        memory.snapshot_path = str(Path(memory.paths["root"]) / "snapshot")
        image = self.root / "synthetic.png"
        image.write_bytes(b"\x89PNG\r\n\x1a\nsynthetic")
        captured = {}
        def run(operation, body, cancel):
            captured.update(body)
            return {"evidence": []}
        with patch.object(memory.bridge, "run", side_effect=run):
            memory.query("Synthetic question?", str(image))
            with self.assertRaises((ValueError, BridgeError)):
                memory.query("Synthetic question?", "https://example.invalid/a.png")
        ref = captured["questionImage"]
        self.assertEqual(ref["byteLength"], image.stat().st_size)
        self.assertTrue(Path(ref["path"]).is_relative_to(Path(memory.paths["root"])))
        self.assertEqual(len(ref["sha256"]), 64)
    def test_unimplemented_learning_and_snapshot_hooks_fail_explicitly(self):
        memory = self.memory()
        with self.assertRaises(ValueError):
            memory.insert({"id": "synthetic"})
        with self.assertRaises(NotImplementedError):
            memory._save_backend(self.root / "save")
        with self.assertRaises(NotImplementedError):
            memory._load_backend(self.root / "save")
    def test_shared_fixture_is_valid_python_protocol(self):
        fixture = json.loads((Path(__file__).parent / "fixtures/protocol-retrieve.json").read_text())
        # Keep the same serialized fixture for .NET; adjust its root for POSIX.
        if os.name != "nt":
            fixture = json.loads(json.dumps(fixture).replace("C:/lme-fixture", "/tmp/lme-fixture"))
        validate_request(fixture)
        self.assertEqual(fixture["question"], "Synthetic question?")
        self.assertEqual(fixture["configuration"]["apiKeyEnvironmentVariable"], "SCRINIA_TEST_KEY")

if __name__ == "__main__":
    unittest.main()
