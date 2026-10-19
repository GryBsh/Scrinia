"""Offline tests for strict live-probe evidence handling; no endpoint calls."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from live_probe import validate_attestation, verify_response, verify_wire
from freeze_experiment import MODEL

class LiveEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        path = Path(self.temp.name) / "server-config.txt"
        path.write_text("invented effective server configuration", encoding="utf-8")
        self.config = {"controller": {"model": MODEL, "endpoint": "https://invented.invalid/v1",
            "temperature": 0.6, "topP": 0.95, "topK": 20, "thinking": True,
            "maxOutputTokens": 16384},
            "reader": {"model": MODEL, "base_url": "https://invented.invalid/v1",
                "temperature": 0.6, "top_p": 0.95, "top_k": 20, "max_completion_tokens": 20000},
            "judge": {"model": "gpt-5.2", "base_url": "https://judge.invalid/v1",
                "reasoning_effort": "medium", "max_completion_tokens": 4096}}
        self.attestation = {"version": 1, "model": MODEL, "model_revision": "a" * 40,
            "precision": "BF16", "mtp": True, "hardware": "invented hardware",
            "context_tokens": 220000, "reader_thinking_default": True,
            "endpoint": self.config["reader"]["base_url"],
            "applied_options": {"temperature": 0.6, "top_p": 0.95, "top_k": 20,
                "controller_output": 16384, "reader_output": 20000},
            "verification_method": "Operator inspected effective server launch/config/log artifacts; no overrides or clamping.",
            "evidence": [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}]}

    def test_deployment_evidence_stays_attested_and_conflicts_or_unknown_fail(self):
        result = validate_attestation(self.attestation, self.config)
        self.assertEqual(result["classification"], "operator-attested")
        for key, bad in (("model", "wrong"), ("reader_thinking_default", False),
                         ("context_tokens", 65536), ("model_revision", "unverified"),
                         ("evidence", []), ("applied_options", {})):
            with self.subTest(key=key):
                value = copy.deepcopy(self.attestation)
                value[key] = bad
                with self.assertRaises(ValueError):
                    validate_attestation(value, self.config)
        Path(self.attestation["evidence"][0]["path"]).write_text("changed", encoding="utf-8")
        with self.assertRaises(ValueError):
            validate_attestation(self.attestation, self.config)

    def test_identity_mismatch_and_omitted_image_answer_fail_even_with_http_success(self):
        response = SimpleNamespace(model=MODEL, choices=[SimpleNamespace(
            message=SimpleNamespace(content="592731"), finish_reason="stop")])
        verify_response(response, MODEL, "592731")
        response.model = "other"
        with self.assertRaises(ValueError):
            verify_response(response, MODEL, "592731")
        response.model = MODEL
        response.choices[0].message.content = "I cannot see an image"
        with self.assertRaises(ValueError):
            verify_response(response, MODEL, "592731")

    def test_controller_explicit_thinking_and_official_reader_default_remain_distinct(self):
        controller = {"model": MODEL, "max_completion_tokens": 16384,
            "extra_body": {"top_k": 20, "chat_template_kwargs": {"enable_thinking": True}},
            "messages": [{"role": "user", "content": [{"type": "image_url",
                "image_url": {"url": "data:image/png;base64,aW52ZW50ZWQ="}}]}]}
        verify_wire(controller, "controller")
        for change in ("thinking", "image", "limit"):
            value = copy.deepcopy(controller)
            if change == "thinking": value["extra_body"] = {"top_k": 20}
            if change == "image": value["messages"][0]["content"] = []
            if change == "limit": value["max_completion_tokens"] = 20000
            with self.assertRaises(ValueError):
                verify_wire(value, "controller")
        reader = copy.deepcopy(controller)
        reader.pop("max_completion_tokens")
        reader["max_tokens"] = 20000
        reader["extra_body"] = {"top_k": 20}
        verify_wire(reader, "reader")
        reader["extra_body"]["chat_template_kwargs"] = {"enable_thinking": True}
        with self.assertRaises(ValueError):
            verify_wire(reader, "reader")

if __name__ == "__main__":
    unittest.main()
