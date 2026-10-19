"""Offline freeze boundary tests; all data and payloads are invented."""
import copy
from contextlib import nullcontext
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
ADAPTER = ROOT / "benchmarks/longmemeval-v2"
UPSTREAM = ROOT / "artifacts/longmemeval-v2/upstream"
sys.path[:0] = [str(ADAPTER), str(UPSTREAM)]
from freeze_experiment import freeze_experiment, verify_frozen, resolve_pair, estimate_measurements
from preflight import offline_preflight
from package_submission import validate_runs
from experiment_config import method_fingerprint
from scrinia_memory import _DEFAULT_CONTROLLER

class FreezeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        source = self.root / "source"
        (source / "src").mkdir(parents=True)
        (source / "src/prompt.txt").write_text("invented prompt", encoding="utf-8")
        (source / "Directory.Build.props").write_text("<Project/>", encoding="utf-8")
        payload = self.root / "payload"
        payload.mkdir()
        for name in ("worker.exe", "scri.exe", "sibling.dll"):
            (payload / name).write_bytes(name.encode())
        processor = self.root / "processor"
        processor.mkdir()
        (processor / "config.json").write_text("{}", encoding="utf-8")
        catalog = self.root / "tools.json"
        catalog.write_text(json.dumps([{"name": n, "inputSchema": {"type": "object"}}
            for n in ("remember", "recall", "search", "list")]), encoding="utf-8")
        guide = self.root / "guide.txt"
        guide.write_text("invented guide", encoding="utf-8")
        self.pair = {"version": 1, "source_root": str(source), "upstream_root": str(UPSTREAM),
            "guide_path": str(guide), "experiments": {},
            "policies": {"image": "original-bytes-no-resize", "query_learning": False},
            "deployment": {"precision": "BF16", "mtp": True, "hardware": "unverified",
                "reader_thinking_default": "unverified", "model_revision": "unverified"},
            "exposure_log": [], "measurements": []}
        for domain in ("web", "enterprise"):
            data = self.root / domain
            data.mkdir()
            (data / "invented.json").write_text('{"synthetic":true}', encoding="utf-8")
            config = {"version": 1, "mode": "synthetic", "domain": domain, "tier": "small",
                "data": {"root": str(data), "revision": "invented-v1"},
                "processor": {"assets": str(processor), "model": "Qwen/Qwen3.5-9B",
                    "revision": "c202236235762e1c871ad0ccb60c8ee5ba337b9a"},
                "run_root": str(self.root / ("runs-" + domain)),
                "memory": {"worker_command": [str(payload / "worker.exe")],
                    "learning": {"mcp_executable": str(payload / "scri.exe"), "mcp_prefix_arguments": [],
                        "tool_catalog_path": str(catalog), "served_context_tokens": 220000}},
                "controller": {**_DEFAULT_CONTROLLER, "endpoint": "http://localhost:1/v1",
                    "model": "unsloth/Qwen3.5-9B-MTP-GGUF:BF16"},
                "reader": {"model": "unsloth/Qwen3.5-9B-MTP-GGUF:BF16", "base_url": "http://localhost:1/v1",
                    "api_key_env": "BENCH_AGENT_API_KEY", "temperature": 0.6, "top_p": 0.95,
                    "top_k": 20, "enable_thinking": True, "max_completion_tokens": 20000,
                    "memory_context_max_tokens": 200000, "timeout_seconds": 180},
                "judge": {"model": "gpt-5.2", "base_url": "http://localhost:1/v1",
                    "api_key_env": "GPT52_API_KEY", "reasoning_effort": "medium",
                    "max_completion_tokens": 4096, "timeout_seconds": 180},
                "limits": {"limit": None, "question_ids": None, "prompt_build_max_workers": 1,
                    "reader_max_concurrent_requests": 1}, "method_fingerprint": "0" * 64}
            self.pair["experiments"][domain] = config
        self.source_location = patch("freeze_experiment.EXECUTING_SOURCE_ROOT", source, create=True)
        self.source_location.start()
        self.addCleanup(self.source_location.stop)
        self.dependencies = patch("freeze_experiment.dependency_inventory",
            return_value={"python": sys.version, "packages": [], "files": []})
        self.dependencies.start()
        self.addCleanup(self.dependencies.stop)

    def freeze(self):
        return freeze_experiment(self.pair, self.root / "frozen")

    def test_freeze_hashes_actual_untracked_source_sibling_payload_and_policies(self):
        folder = self.freeze()
        manifest = verify_frozen(folder)
        self.assertIn("src/prompt.txt", manifest["source_files"])
        self.assertEqual(manifest["capabilities"]["live"], "unverified")
        self.assertTrue((folder / "source.zip").is_file())
        self.assertEqual(offline_preflight(folder)["live"], "unverified")

    def test_mutated_transitive_files_are_rejected(self):
        paths = ["source/src/prompt.txt", "source/Directory.Build.props", "payload/sibling.dll",
                 "payload/worker.exe", "tools.json", "web/invented.json", "processor/config.json", "guide.txt"]
        for relative in paths:
            with self.subTest(relative=relative):
                folder = self.freeze()
                path = self.root / relative
                original = path.read_bytes()
                path.write_bytes(original + b" changed")
                with self.assertRaisesRegex(ValueError, "changed"):
                    verify_frozen(folder)
                path.write_bytes(original)
                shutil.rmtree(folder)

    def test_modified_frozen_policy_and_model_identity_are_rejected(self):
        folder = self.freeze()
        config = folder / "resolved_pair.json"
        original = config.read_text(encoding="utf-8")
        for section in ("policy", "model"):
            value = json.loads(original)
            if section == "policy":
                value["policies"]["image"] = "resize"
            else:
                value["experiments"]["web"]["reader"]["model"] = "changed-model"
            config.write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "changed"):
                verify_frozen(folder)

    def test_missing_credentials_wrong_models_domains_and_operating_point_fail(self):
        cases = [("missing-key", lambda p: p["experiments"]["web"]["judge"].pop("api_key_env")),
            ("judge", lambda p: p["experiments"]["web"]["judge"].update(model="gpt-4")),
            ("reader", lambda p: p["experiments"]["web"]["reader"].update(model="wrong")),
            ("controller", lambda p: p["experiments"]["web"]["controller"].update(thinking=False)),
            ("domain", lambda p: p["experiments"].pop("enterprise")),
            ("pair", lambda p: p["experiments"]["enterprise"]["controller"].update(topK=30))]
        for name, change in cases:
            with self.subTest(name=name):
                value = copy.deepcopy(self.pair)
                change(value)
                with self.assertRaises(ValueError):
                    resolve_pair(value)
        value = copy.deepcopy(self.pair)
        value["experiments"]["web"]["reader"]["model"] += "  "
        value["experiments"]["enterprise"]["reader"]["model"] += "  "
        self.assertFalse(resolve_pair(value)["experiments"]["web"]["reader"]["model"].endswith(" "))

    def test_official_package_validator_rejects_omitted_failed_case(self):
        folder = self.freeze()
        run = self.root / "incomplete"
        (run / "runtime_inputs").mkdir(parents=True)
        def write(name, value):
            (run / name).write_text(json.dumps(value), encoding="utf-8")
        write("runtime_inputs/questions.json", [
            {"question_id": "invented-success", "domain": "web", "question_type": "synthetic"},
            {"question_id": "invented-failed", "domain": "web", "question_type": "synthetic"}])
        write("runtime_inputs/haystack.json", {})
        write("run_args.json", {"domain": "web", "model": "Qwen3.5-9B", "evaluator_model": "gpt-5.2"})
        write("aggregated_metrics.json", {"overall": {"count_all_questions": 2}})
        (run / "per_question.jsonl").write_text(json.dumps(
            {"question_id": "invented-success", "question_type": "synthetic"}) + "\n", encoding="utf-8")
        for name in ("prompt_rows.jsonl", "prompt_build_summary.json"):
            (run / name).write_text("{}", encoding="utf-8")
        from leaderboard.submission_utils import SubmissionError
        with self.assertRaisesRegex(SubmissionError, "missing"):
            validate_runs(folder, run, run)

    def test_estimates_keep_unknown_cost_unknown_and_include_failures(self):
        result = estimate_measurements([
            {"phase": "build", "seconds": 2.0, "success": True, "cost_usd": 0.2, "provider": "real"},
            {"phase": "build", "seconds": 3.0, "success": False, "cost_usd": None, "provider": "real"},
            {"phase": "query", "seconds": 1.0, "success": False, "cost_usd": None, "provider": "fake"}])
        self.assertEqual(result["build"]["seconds_total"], 5.0)
        self.assertEqual(result["build"]["attempts"], 2)
        self.assertIsNone(result["build"]["cost_usd_total"])
        self.assertEqual(result["query"]["performance_evidence"], "synthetic-only")


    def test_frozen_launcher_refuses_changed_payload_before_inference(self):
        from run_scrinia import run_frozen_experiment
        folder = self.freeze()
        (self.root / "payload/sibling.dll").write_bytes(b"changed")
        with patch("run_scrinia.run_experiment") as launch:
            with self.assertRaisesRegex(ValueError, "changed"):
                run_frozen_experiment(folder, "web", self.root / "never-started")
            launch.assert_not_called()

    def test_dependency_bytes_and_added_python_source_are_bound(self):
        folder = self.freeze()
        self.dependencies.stop()
        with patch("freeze_experiment.dependency_inventory",
                return_value={"python": "changed", "packages": [], "files": []}):
            with self.assertRaisesRegex(ValueError, "dependency payload changed"):
                verify_frozen(folder)
        self.dependencies.start()
        (self.root / "source/src/new.py").write_text("changed=True", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "changed"):
            verify_frozen(folder)


    def test_submission_launch_requires_live_evidence_bound_to_freeze(self):
        from run_scrinia import run_frozen_experiment
        self.pair["deployment"].update(model_revision="b" * 40, hardware="invented", reader_thinking_default=True)
        for config in self.pair["experiments"].values():
            config["mode"] = "submission"
            config["data"]["revision"] = "a" * 40
        folder = self.freeze()
        with patch("run_scrinia.run_experiment") as launch:
            with self.assertRaisesRegex(ValueError, "live preflight"):
                run_frozen_experiment(folder, "web", self.root / "never-started")
            launch.assert_not_called()


    def test_fresh_identity_does_not_import_official_backends_or_transformers(self):
        import subprocess
        config = resolve_pair(self.pair)["experiments"]["web"]
        path = self.root / "fresh-config.json"
        path.write_text(json.dumps(config), encoding="utf-8")
        code = ("import sys,json;sys.path.insert(0,sys.argv[1]);"
            "from experiment_config import method_fingerprint;"
            "method_fingerprint(json.load(open(sys.argv[2],encoding='utf-8')));"
            "assert 'memory_modules.memory' not in sys.modules;"
            "assert 'transformers' not in sys.modules")
        result = subprocess.run([sys.executable, "-X", "utf8", "-c", code, str(ADAPTER), str(path)],
            capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_measurement_records_failed_build_and_successful_query_without_known_cost(self):
        from run_scrinia import measure_memory_calls
        class Memory:
            def insert(self, value):
                raise ValueError("invented failure")
            def query(self, value):
                return []
        original = Memory.insert
        with measure_memory_calls(Memory, self.root, "synthetic"):
            with self.assertRaises(ValueError):
                Memory().insert("invented")
            self.assertEqual(Memory().query("invented"), [])
        self.assertIs(Memory.insert, original)
        records = [json.loads(line) for line in (self.root / "measurements.jsonl").read_text(encoding="utf-8").splitlines()]
        self.assertEqual([(r["phase"], r["success"]) for r in records], [("build", False), ("query", True)])
        self.assertTrue(all(r["seconds"] >= 0 and r["cost_usd"] is None and r["usage"] is None for r in records))
        self.assertEqual(estimate_measurements(records)["build"]["failed_attempts"], 1)


    def test_legacy_submission_config_is_refused_before_cache_or_data_materialization(self):
        from run_scrinia import run_experiment
        config = resolve_pair(self.pair)["experiments"]["web"]
        config["mode"] = "submission"
        with patch("run_scrinia._processor_cache", side_effect=RuntimeError("materialization was reached")) as cache:
            with self.assertRaisesRegex(ValueError, "frozen"):
                run_experiment(UPSTREAM, config, self.root / "never-materialized")
            cache.assert_not_called()

    def test_valid_frozen_submission_evidence_reaches_frozen_runner(self):
        from run_scrinia import run_frozen_experiment
        self.pair["deployment"].update(model_revision="b" * 40, hardware="invented", reader_thinking_default=True)
        for config in self.pair["experiments"].values():
            config["mode"] = "submission"
            config["data"]["revision"] = "a" * 40
        folder = self.freeze()
        from freeze_experiment import digest
        evidence = {"status": "ready", "live": "empirical-probes-passed",
            "frozen_manifest_hash": digest(folder / "manifest.json"), "deployment": {"details": {"invented": True}}}
        (folder.parent / (folder.name + "-live-preflight.json")).write_text(json.dumps(evidence), encoding="utf-8")
        with patch("live_probe.validate_attestation"), patch("run_scrinia.run_experiment", return_value="accepted") as runner:
            self.assertEqual(run_frozen_experiment(folder, "web", self.root / "new-run"), "accepted")
            self.assertEqual(runner.call_args.kwargs["frozen_manifest_hash"], digest(folder / "manifest.json"))

    def test_foreign_adapter_checkout_is_rejected_before_materialization(self):
        import subprocess
        folder = self.freeze()
        other = self.root / "other-checkout"
        adapter = other / "benchmarks/longmemeval-v2"
        adapter.mkdir(parents=True)
        for pattern in ("*.py", "*.json"):
            for path in ADAPTER.glob(pattern):
                shutil.copyfile(path, adapter / path.name)
        for role in ("Learning", "Retrieval"):
            source = ROOT / "benchmarks/Scrinia.LongMemEvalV2.Worker" / role
            target = other / "benchmarks/Scrinia.LongMemEvalV2.Worker" / role
            target.mkdir(parents=True)
            for path in source.glob("*.txt"):
                shutil.copyfile(path, target / path.name)
        code = ("import sys;sys.path.insert(0,sys.argv[1]);"
            "import freeze_experiment,run_scrinia;"
            "freeze_experiment.dependency_inventory=lambda:{'python':sys.version,'packages':[],'files':[]};"
            "run_scrinia.run_experiment=lambda *a,**k:(_ for _ in ()).throw(RuntimeError('MATERIALIZATION_REACHED'));"
            "run_scrinia.run_frozen_experiment(sys.argv[2],'web',sys.argv[3])")
        result = subprocess.run([sys.executable, "-X", "utf8", "-c", code,
            str(adapter), str(folder), str(self.root / "must-not-run")],
            capture_output=True, text=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("executing adapter checkout", result.stderr)
        self.assertNotIn("MATERIALIZATION_REACHED", result.stderr)
        self.assertFalse((self.root / "must-not-run").exists())
        self.source_location.stop()
        with self.assertRaisesRegex(ValueError, "executing adapter checkout"):
            freeze_experiment(self.pair, self.root / "must-not-freeze")
        self.source_location.start()

    def test_failed_live_rerun_invalidates_prior_success_before_any_prerequisites(self):
        from preflight import live_preflight
        from run_scrinia import run_frozen_experiment
        from freeze_experiment import digest
        self.pair["deployment"].update(model_revision="b" * 40, hardware="invented", reader_thinking_default=True)
        for config in self.pair["experiments"].values():
            config["mode"] = "submission"
            config["data"]["revision"] = "a" * 40
        folder = self.freeze()
        ready = folder.parent / (folder.name + "-live-preflight.json")
        result = {"status": "ready", "live": "empirical-probes-passed",
            "frozen_manifest_hash": digest(folder / "manifest.json"),
            "deployment": {"details": {"invented": True}}}
        for failure in ("missing-attestation", "offline", "credentials", "probe", "publish"):
            with self.subTest(failure=failure):
                with patch("preflight.offline_preflight", return_value={"credentials_present": {"KEY": True}}), patch(
                        "live_probe.probe_pair", return_value=result):
                    live_preflight(folder, "invented-attestation.json")
                self.assertTrue(ready.is_file())
                if failure == "missing-attestation":
                    offline = patch("preflight.offline_preflight", side_effect=AssertionError("prerequisites reached"))
                elif failure == "offline":
                    offline = patch("preflight.offline_preflight", side_effect=ValueError("offline failure"))
                else:
                    offline = patch("preflight.offline_preflight", return_value={
                        "credentials_present": {"KEY": failure != "credentials"}})
                probe = patch("live_probe.probe_pair", side_effect=ValueError("probe failure")) if failure == "probe" else patch(
                    "live_probe.probe_pair", return_value=result)
                publication = patch("snapshots.os.replace", side_effect=OSError("publication failure")) if failure == "publish" else nullcontext()
                with offline, probe, publication:
                    with self.assertRaises((ValueError, OSError)):
                        live_preflight(folder, None if failure == "missing-attestation" else "invented-attestation.json")
                self.assertFalse(ready.exists())
                self.assertFalse(list(folder.parent.glob(ready.name + ".*.tmp")))
                with patch("run_scrinia.run_experiment") as runner:
                    with self.assertRaisesRegex(ValueError, "live preflight"):
                        run_frozen_experiment(folder, "web", self.root / "never-started")
                    runner.assert_not_called()

if __name__ == "__main__":
    unittest.main()
