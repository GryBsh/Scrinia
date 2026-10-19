"""Offline integration against the pinned, unchanged official evaluator."""
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[3]
ADAPTER = Path(__file__).resolve().parents[1]
UPSTREAM = ROOT / "artifacts/longmemeval-v2/upstream"
PYTHON = ROOT / "artifacts/longmemeval-v2/venv/Scripts/python.exe"
sys.path.insert(0, str(UPSTREAM))
sys.path.insert(0, str(ADAPTER))
from experiment_config import validate_experiment
from run_scrinia import run_experiment
from scrinia_memory import ScriniaMemory
from memory_modules.memory import load_memory
from snapshots import atomic_json, fingerprint
from filesystem import Path, io_name, io_path, canonical_path, canonical_paths
from bridge import BridgeError
sys.path.insert(0, str(ADAPTER / "tests/fixtures"))
from fake_openai_server import FakeOpenAIServer

class OfficialHarnessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.addCleanup(lambda: shutil.rmtree(io_path(self.root), ignore_errors=False))

    def params(self, location="runtime"):
        runtime = self.root / location
        io_path(runtime).mkdir(exist_ok=True)
        (io_path(runtime / "worker.exe")).write_bytes(b"synthetic worker")
        (io_path(runtime / "scri.exe")).write_bytes(b"synthetic mcp")
        (io_path(runtime / "processor")).mkdir(exist_ok=True)
        (io_path(runtime / "processor/config.json")).write_text("{}")
        (io_path(runtime / "tools.json")).write_text(json.dumps([
            {"name": n, "inputSchema": {"type": "object"}}
            for n in ("remember", "recall", "search", "list")]))
        return {"run_root": str(runtime / "runs"),
                "worker_command": [str(runtime / "worker.exe")],
                "trajectories_root_dir": str(runtime),
                "learning": {"mcp_executable": str(runtime / "scri.exe"),
                    "mcp_prefix_arguments": [], "processor_assets": str(runtime / "processor"),
                    "tool_catalog_path": str(runtime / "tools.json"), "served_context_tokens": 65536}}

    def committed_memory(self):
        memory = ScriniaMemory(self.params())
        from portability import method_identity, identity_hash
        identity = method_identity(memory.memory_params)
        memory.memory_params["method_identity"] = identity
        generation = "a" * 32
        target = memory.snapshots.root / "generations" / generation
        (io_path(target / "workspace")).mkdir(parents=True)
        (io_path(target / "home")).mkdir()
        (io_path(target / "sources")).mkdir()
        (io_path(target / "sources/image.png")).write_bytes(b"synthetic")
        (io_path(target / "catalogs")).mkdir()
        (io_path(target / "catalogs/catalog.json")).write_text(json.dumps({"trajectory": {
            "states": [{"screenshot": {"path": "sources/image.png",
            "sha256": hashlib.sha256(b"synthetic").hexdigest(), "byteLength": 9}}]}}))
        value = fingerprint(target)
        receipt = {"sourceHash": "b" * 64, "previousSnapshotHash": None,
                   "newSnapshotHash": value, "committedOperationId": "c" * 32}
        meta = {"version": 1, "configurationHash": identity_hash(identity), "fingerprint": value,
                "ledger": {"invented": {"sourceHash": "b" * 64, "receipt": receipt}},
                "receipt": receipt}
        atomic_json(target / ".generation.json", meta)
        atomic_json(memory.snapshots.root / "current.json", {
            "generation": generation, "fingerprint": value,
            "metadataHash": hashlib.sha256((io_path(target / ".generation.json")).read_bytes()).hexdigest()})
        memory.snapshot_path = str(target)
        memory.bridge.run = lambda *args: {}
        return memory

    def catalog(self):
        executable = ROOT / "tests/Scrinia.LongMemEvalV2.Worker.Tests/bin/Debug/net10.0/scrinia-payload/scri.exe"
        import queue
        import uuid
        folder = self.root / ("catalog-" + uuid.uuid4().hex)
        io_path(folder).mkdir()
        workspace = folder / ("memory-" + uuid.uuid4().hex) / "snapshots/generations" / uuid.uuid4().hex / "workspace"
        io_path(workspace).mkdir(parents=True)
        home = folder / "home"
        io_path(home).mkdir()
        process = subprocess.Popen([str(executable), "serve", "--no-auto-setup", "--workspace-root", str(workspace)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, env={**os.environ, "SCRINIA_HOME": str(home)})
        responses = queue.Queue()
        def read():
            for line in process.stdout:
                responses.put(json.loads(line))
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        def send(value):
            process.stdin.write(json.dumps(value) + "\n")
            process.stdin.flush()
        def receive(ident):
            while True:
                row = responses.get(timeout=30)
                if row.get("id") == ident:
                    self.assertNotIn("error", row)
                    return row["result"]
        try:
            send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "synthetic-fixture", "version": "1"}}})
            receive(1)
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
            tools = receive(2)["tools"]
            # Protocol.Tool's source-generated serialization includes null optional fields.
            fields = ("name", "title", "description", "inputSchema", "outputSchema",
                      "annotations", "icons", "_meta")
            self.assertTrue(all(not (set(tool) - set(fields) - {"execution"}) for tool in tools))
            self.assertTrue(all(tool.get("execution") == {"taskSupport": "optional"} for tool in tools), [tool.get("execution") for tool in tools])
            tools = [{field: tool.get(field) for field in fields} for tool in tools]
            send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
                "name": "remember", "arguments": {"path": "/source/" + "a" * 64 + "/000000/000000",
                "content": ["Synthetic source"], "description": "Synthetic deep-path test"}}})
            seeded = receive(3)
            self.assertNotIn("status: error", json.dumps(seeded), seeded)
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            reader.join(timeout=5)
            process.stdout.close()
        path = self.root / "catalog.json"
        io_path(path).write_text(json.dumps(tools), encoding="utf-8")
        return executable, path

    def experiment(self, server, domain="web", distinct=False):
        from experiment_config import method_fingerprint
        from scrinia_memory import _DEFAULT_CONTROLLER
        executable, catalog = self.catalog()
        data = self.root / ("data-" + domain)
        shutil.copytree(io_path(ADAPTER / "tests/fixtures" / ("synthetic_" + domain)), io_path(data))
        if distinct:
            haystack = data / "haystacks/lme_v2_small.json"
            value = json.loads(io_path(haystack).read_text())
            value[domain + "-question-2"] = [domain + "-invented-2"]
            io_path(haystack).write_text(json.dumps(value))
        config = {"version": 1, "mode": "synthetic", "domain": domain, "tier": "small",
            "data": {"root": str(data), "revision": "synthetic-invented-v1"},
            "processor": {"assets": str(ROOT / "artifacts/longmemeval-v2/processor"),
                          "model": "Qwen/Qwen3.5-9B", "revision": "c202236235762e1c871ad0ccb60c8ee5ba337b9a"},
            "run_root": str(self.root / ("runs-" + domain)),
            "memory": {"worker_command": [str(ROOT / "benchmarks/Scrinia.LongMemEvalV2.Worker/bin/Debug/net10.0/scrinia-lme-v2-worker.exe")],
                "learning": {"mcp_executable": str(executable), "mcp_prefix_arguments": [],
                    "tool_catalog_path": str(catalog), "served_context_tokens": 65536}},
            "controller": {**_DEFAULT_CONTROLLER, "endpoint": server.endpoint,
                           "model": "synthetic-controller", "apiKeyEnvironmentVariable": "SYNTHETIC_CONTROLLER_KEY"},
            "reader": {"model": "synthetic-Qwen3.5-9B-reader", "base_url": server.endpoint,
                "api_key_env": "SYNTHETIC_READER_KEY", "temperature": 0.6, "top_p": 0.95, "top_k": 20,
                "enable_thinking": True, "max_completion_tokens": 20000,
                "memory_context_max_tokens": 200000, "timeout_seconds": 60},
            "judge": {"model": "synthetic-gpt-5.2-judge", "base_url": server.endpoint,
                "api_key_env": "SYNTHETIC_JUDGE_KEY", "reasoning_effort": "medium",
                "max_completion_tokens": 4096, "timeout_seconds": 60},
            "limits": {"limit": None, "question_ids": None,
                "prompt_build_max_workers": 1, "reader_max_concurrent_requests": 1},
            "method_fingerprint": "0" * 64, "save_memory": not distinct}
        config["method_fingerprint"] = method_fingerprint(config)
        return config

    def launch(self, config, output, *, cancel=False):
        path = self.root / (output.name + "-config.json")
        io_path(path).write_text(json.dumps(config), encoding="utf-8")
        env = {**os.environ, "SYNTHETIC_CONTROLLER_KEY": "fixture-controller",
               "SYNTHETIC_READER_KEY": "fixture-reader", "SYNTHETIC_JUDGE_KEY": "fixture-judge",
               "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"}
        if cancel:
            env["SYNTHETIC_CANCEL"] = "1"
        result = subprocess.run([str(PYTHON), str(ADAPTER / "tests/fixtures/launch_with_faults.py"),
            "--upstream-root", str(UPSTREAM), "--config", str(path), "--output-dir", str(output)],
            capture_output=True, text=True, timeout=180, env=env)
        if result.returncode:
            for diagnostic in canonical_paths(io_path(Path(config["run_root"])).glob("memory-*/events/*.jsonl")):
                rows = [json.loads(line) for line in io_path(diagnostic).read_text(encoding="utf-8").splitlines()]
                result.stderr += "\nSYNTHETIC EVENTS " + json.dumps([
                    {key: row[key] for key in ("phase", "kind", "status", "errorCode", "data")
                     if key in row and (key != "data" or row.get("kind") in ("stderr", "connected"))}
                    for row in rows])
                for row in rows:
                    if row.get("kind") == "catalog":
                        from batching import catalog_hash
                        actual = row["data"]["tools"]
                        expected = json.loads(io_path(Path(config["memory"]["learning"]["tool_catalog_path"])).read_text())
                        result.stderr += " CATALOG " + json.dumps({"actual": catalog_hash(actual),
                            "expected": catalog_hash(expected), "equal": actual == expected})
                        for a, b in zip(sorted(actual, key=lambda x:x["name"]), sorted(expected, key=lambda x:x["name"])):
                            if a != b:
                                result.stderr += " DIFF " + json.dumps({"actual": a, "expected": b})[:1000]
        return result

    def test_unchanged_official_harness_shared_and_distinct_multimodal_histories(self):
        from run_scrinia import upstream_hashes
        before = upstream_hashes(UPSTREAM)
        for domain, distinct in (("web", False), ("enterprise", True)):
            with self.subTest(domain=domain), FakeOpenAIServer(retry_once=True) as server:
                config = self.experiment(server, domain, distinct)
                output = self.root / ("output-" + domain)
                result = self.launch(config, output)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(server.errors, [])
                status = json.loads((io_path(output / "scrinia_run.json")).read_text())
                self.assertEqual(status["status"], "complete")
                self.assertTrue(status["upstreamUnchanged"])
                for name in ("run_args.json", "prompt_rows.jsonl", "prompt_build_summary.json",
                             "per_question.jsonl", "aggregated_metrics.json"):
                    self.assertTrue((io_path(output / name)).is_file(), name)
                records = [json.loads(line) for line in (io_path(output / "per_question.jsonl")).read_text().splitlines()]
                self.assertEqual(len(records), 2)
                self.assertGreaterEqual(records[0]["memory_query_duration_seconds"], 5.35)
                self.assertGreaterEqual(records[1]["memory_query_duration_seconds"], 0.35)
                learning = [c for c in server.calls if c["model"] == "synthetic-controller" and
                            len(c.get("tools", [])) == 4]
                self.assertEqual(len(learning), 2 if distinct else 1)
                readers = [c for c in server.calls if c["model"] == "synthetic-Qwen3.5-9B-reader"]
                judges = [c for c in server.calls if c["model"] == "synthetic-gpt-5.2-judge"]
                self.assertEqual(len(readers), 2)
                self.assertEqual(len(judges), 2)
                from evaluation.harness import DOMAIN_SYSTEM_PROMPTS
                from evaluation.qa_eval_metrics import _GOTCHAS_JUDGE_SYSTEM_PROMPT
                for call in readers:
                    self.assertEqual(call["messages"][0]["content"], DOMAIN_SYSTEM_PROMPTS[domain])
                    self.assertNotIn("tools", call)
                    self.assertEqual((call["temperature"], call["top_p"], call["top_k"], call["max_tokens"]),
                                     (0.6, 0.95, 20, 20000))
                    parts = call["messages"][1]["content"]
                    self.assertIn("Synthetic observation", json.dumps(parts))
                    self.assertEqual(sum(p["type"] == "image_url" for p in parts), 2)
                    self.assertTrue(all(p["image_url"]["url"].startswith("data:image/png;base64,")
                                        for p in parts if p["type"] == "image_url"))
                for call in judges:
                    self.assertEqual(call["messages"][0]["content"], _GOTCHAS_JUDGE_SYSTEM_PROMPT)
                    self.assertEqual(call["reasoning_effort"], "medium")
                    self.assertEqual(call["max_completion_tokens"], 4096)
                    self.assertNotIn("tools", call)
                    self.assertIn("Reference answer", call["messages"][1]["content"])
                requests = [json.loads(io_path(p).read_text()) for p in canonical_paths(io_path(Path(config["run_root"])).rglob("request.json"))]
                for request in requests:
                    payload = json.dumps(request)
                    for forbidden in ("answer_gold", "eval_function", "question_type", "questions.json", "-question-"):
                        self.assertNotIn(forbidden, payload)
                if not distinct:
                    self.assertTrue((io_path(output / "memory_state/bundle.json")).is_file())
                    moved = self.root / "relocated-bundle"
                    shutil.copytree(io_path(output / "memory_state"), io_path(moved))
                    restored = copy.deepcopy(config)
                    restored["run_root"] = str(self.root / "restored-runs")
                    restored["load_memory_dir"] = str(moved)
                    restored["save_memory"] = False
                    reload_output = self.root / "reloaded-output"
                    again = self.launch(restored, reload_output)
                    self.assertEqual(again.returncode, 0, again.stdout + again.stderr)
                    self.assertEqual(json.loads((io_path(reload_output / "scrinia_run.json")).read_text())["status"], "complete")
                    self.assertEqual(sum(c["model"] == "synthetic-controller" and len(c.get("tools", [])) == 4
                                         for c in server.calls), 1)
                    self.assertEqual(len(list(canonical_paths(io_path(moved / "snapshot/catalogs").glob("*.json")))), 1)
                    self.assertFalse(any(p.name == "queries" for p in canonical_paths(io_path(moved).rglob("*"))))
                self.assertEqual(upstream_hashes(UPSTREAM), before)

    def test_cancelled_run_is_incomplete_and_official_package_validation_refuses_it(self):
        with FakeOpenAIServer(controller_delay=1) as server:
            config = self.experiment(server)
            output = self.root / "interrupted"
            result = self.launch(config, output, cancel=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("CANCELLED", result.stderr)
            self.assertEqual(json.loads((io_path(output / "scrinia_run.json")).read_text())["status"], "incomplete")
            from leaderboard.submission_utils import validate_run, SubmissionError
            with self.assertRaises(SubmissionError):
                validate_run(output, "web", "small", method_override="scrinia")
            # A new invocation cannot continue by skipping already graded queries.
            again = self.launch(config, output)
            self.assertNotEqual(again.returncode, 0)
            self.assertIn("must be new", again.stderr)

    def test_submission_rejects_limited_or_selected_questions_and_synthetic_remote_endpoints(self):
        with FakeOpenAIServer() as server:
            config = self.experiment(server)
            for selection in ({"limit": 1}, {"question_ids": ["invented"]}):
                selected = copy.deepcopy(config)
                selected["mode"] = "submission"
                selected["limits"].update(selection)
                with self.assertRaises(ValueError):
                    validate_experiment(selected)
            config["reader"]["base_url"] = "https://example.invalid/v1"
            with self.assertRaises(ValueError):
                validate_experiment(config)

    def test_verified_save_load_relocates_runtime_and_assets(self):
        memory = self.committed_memory()
        (io_path(Path(memory.paths["root"]) / "queries")).mkdir()
        (io_path(Path(memory.paths["root"]) / "queries/private.log")).write_text("not learned state")
        saved = self.root / "saved"
        memory.save_memory(io_path(saved))
        moved = self.root / "moved"
        shutil.copytree(io_path(saved), io_path(moved))
        params = self.params("relocated")
        from unittest.mock import patch
        with patch("bridge.WorkerBridge.run", return_value={}):
            loaded = load_memory(moved, {"memory_type": "scrinia", "memory_params": params})
        self.assertEqual(loaded.snapshots.current(), memory.snapshots.current())
        self.assertEqual(fingerprint(loaded.snapshot_path), fingerprint(memory.snapshot_path))
        self.assertFalse(any(p.name == "queries" for p in canonical_paths(io_path(saved).rglob("*"))))
        self.assertTrue((io_path(Path(loaded.snapshot_path) / "sources/image.png")).is_file())
        with patch("bridge.WorkerBridge.run", return_value={}):
            unchanged = load_memory(saved)
        self.assertEqual(unchanged.snapshots.current(), memory.snapshots.current())
        bad = copy.deepcopy(params)
        bad["learning"]["served_context_tokens"] = 70000
        with self.assertRaises((ValueError, BridgeError, RuntimeError)):
            load_memory(moved, {"memory_type": "scrinia", "memory_params": bad})

    def test_relocation_rejects_changed_runtime_bytes_catalog_and_controller(self):
        memory = self.committed_memory()
        saved = self.root / "saved"
        memory.save_memory(io_path(saved))
        for index, changed in enumerate(("worker", "mcp", "processor", "catalog", "controller")):
            with self.subTest(changed=changed):
                params = self.params("changed-" + str(index))
                if changed == "worker":
                    io_path(Path(params["worker_command"][0])).write_bytes(b"changed worker")
                elif changed == "mcp":
                    io_path(Path(params["learning"]["mcp_executable"])).write_bytes(b"changed mcp")
                elif changed == "processor":
                    io_path(Path(params["learning"]["processor_assets"]) / "config.json").write_text("changed")
                elif changed == "catalog":
                    path = Path(params["learning"]["tool_catalog_path"])
                    tools = json.loads(io_path(path).read_text())
                    tools[0]["description"] = "changed tool"
                    io_path(path).write_text(json.dumps(tools))
                else:
                    params["controller"] = {"temperature": 0.2}
                with self.assertRaises((ValueError, BridgeError, RuntimeError)):
                    load_memory(io_path(saved), {"memory_type": "scrinia", "memory_params": params})

    def test_bundle_refuses_uncertainty_missing_assets_and_fingerprint_changes(self):
        memory = self.committed_memory()
        saved = self.root / "saved"
        memory.save_memory(io_path(saved))
        (io_path(saved / "snapshot/sources/image.png")).unlink()
        with self.assertRaises((ValueError, BridgeError, RuntimeError)):
            load_memory(saved)
        uncertain = memory.snapshots.root / "uncertain/pending.json"
        io_path(uncertain.parent).mkdir()
        io_path(uncertain).write_text("{}")
        with self.assertRaises((ValueError, BridgeError, RuntimeError)):
            memory.save_memory(self.root / "uncertain-save")
        io_path(uncertain).unlink()
        (io_path(Path(memory.snapshot_path) / "workspace/changed")).write_text("changed")
        with self.assertRaises((ValueError, BridgeError, RuntimeError)):
            memory.save_memory(self.root / "changed-save")

if __name__ == "__main__":
    unittest.main()
