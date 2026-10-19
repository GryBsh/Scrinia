"""Real offline frozen pair, image-only evidence and official package integration."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

ADAPTER = Path(__file__).resolve().parents[1]
ROOT = ADAPTER.parents[1]
sys.path[:0] = [str(ADAPTER), str(ADAPTER / "tests"), str(ADAPTER / "tests/fixtures")]
import test_official_harness as harness_fixture
from fake_openai_server import FakeOpenAIServer
from filesystem import io_path, canonical_paths
from freeze_experiment import MODEL, freeze_experiment, verify_frozen
from package_submission import package_submission

class FrozenPackageTests(unittest.TestCase):
    def test_real_frozen_pair_preserves_image_only_evidence_and_official_packaging(self):
        helper = harness_fixture.OfficialHarnessTests()
        validation_root = os.environ.get("SCRINIA_V2_VALIDATION_ROOT")
        if validation_root:
            helper.root = Path(validation_root).absolute() / "synthetic-frozen-pair"
            helper.root.mkdir(parents=True, exist_ok=False)
        else:
            helper.setUp()
            self.addCleanup(helper.doCleanups)
        from PIL import Image
        with FakeOpenAIServer(image_only=True) as server:
            web = helper.experiment(server)
            web["controller"]["model"] = MODEL
            web["reader"]["model"] = MODEL
            web["judge"]["model"] = "gpt-5.2"
            web["memory"]["learning"]["served_context_tokens"] = 220000
            request = helper.root / "capture.json"
            capture = helper.root / "actual-capture"
            request.write_text(json.dumps({"root": str(capture),
                "executable": web["memory"]["learning"]["mcp_executable"], "prefixArguments": []}), encoding="utf-8")
            worker = web["memory"]["worker_command"][0]
            subprocess.run([worker, "--capture-catalog", str(request)], check=True, capture_output=True, timeout=90)
            web["memory"]["learning"]["tool_catalog_path"] = str(capture / "tools.json")
            enterprise = copy.deepcopy(web)
            enterprise["domain"] = "enterprise"
            enterprise["run_root"] = str(helper.root / "runs-enterprise")
            enterprise["data"]["root"] = str(helper.root / "data-enterprise")
            shutil.copytree(io_path(ADAPTER / "tests/fixtures/synthetic_enterprise"), io_path(Path(enterprise["data"]["root"])))
            pair = {"version": 1, "source_root": str(ROOT), "upstream_root": str(ROOT / "artifacts/longmemeval-v2/upstream"),
                "guide_path": str(capture / "guide.txt"), "experiments": {"web": web, "enterprise": enterprise},
                "policies": {"image": "original-bytes-no-resize", "query_learning": False},
                "deployment": {"precision": "BF16", "mtp": True, "hardware": "synthetic-protocol-peer",
                    "reader_thinking_default": "unverified", "model_revision": "unverified"},
                "exposure_log": [], "measurements": []}
            for domain, config in pair["experiments"].items():
                data = Path(config["data"]["root"])
                trajectory = data / "trajectories.jsonl"
                trajectory.write_text(trajectory.read_text(encoding="utf-8").replace("amber", "visible-on-screenshot").replace("cobalt", "visible-on-screenshot"), encoding="utf-8")
                rows = [json.loads(line) for line in (data / "questions.jsonl").read_text(encoding="utf-8").splitlines()]
                for row in rows:
                    row.pop("image", None)
                (data / "questions.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
                color = (0, 71, 171) if domain == "web" else (255, 191, 0)
                Image.new("RGB", (96, 64), color).save(io_path(data / "screen.png"))
            folder = freeze_experiment(pair, helper.root / "frozen")
            self.assertIn("src/Scrinia.Mcp/MemoryTools.Core.cs", verify_frozen(folder)["source_files"])
            env = {**os.environ, "SYNTHETIC_CONTROLLER_KEY": "offline-placeholder",
                "SYNTHETIC_READER_KEY": "offline-placeholder", "SYNTHETIC_JUDGE_KEY": "offline-placeholder",
                "PYTHONUTF8": "1", "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"}
            outputs = {}
            for domain in ("web", "enterprise"):
                outputs[domain] = helper.root / ("output-" + domain)
                result = subprocess.run([sys.executable, "-X", "utf8", str(ADAPTER / "run_scrinia.py"),
                    "--frozen-experiment", str(folder), "--domain", domain, "--output-dir", str(outputs[domain])],
                    env=env, capture_output=True, text=True, timeout=240)
                self.assertEqual(result.returncode, 0, result.stderr)
                estimates = json.loads(io_path(outputs[domain] / "estimates.json").read_text(encoding="utf-8"))
                self.assertGreater(estimates["build"]["attempts"], 0)
                self.assertEqual(estimates["query"]["attempts"], 2)
                self.assertEqual(estimates["query"]["performance_evidence"], "synthetic-only")
                self.assertIsNone(estimates["query"]["cost_usd_total"])
                records = [json.loads(line) for line in io_path(outputs[domain] / "per_question.jsonl").read_text(encoding="utf-8").splitlines()]
                self.assertEqual({r["response_parsed_boxed"] for r in records}, {"cobalt" if domain == "web" else "amber"})
            readers = [c for c in server.calls if c["model"] == MODEL and not c.get("tools")]
            self.assertEqual(len(readers), 4)
            for request in readers:
                self.assertNotIn("amber", json.dumps(request["messages"]))
                self.assertNotIn("cobalt", json.dumps(request["messages"]))
                self.assertEqual(sum(p.get("type") == "image_url" for m in request["messages"]
                    for p in (m["content"] if isinstance(m["content"], list) else [])), 1)
            archive = package_submission(folder, outputs["web"], outputs["enterprise"],
                helper.root / "package", "synthetic-scrinia")
            self.assertTrue(io_path(archive).is_file())
            self.assertFalse(server.errors)
            tampered = helper.root / "tampered-web"
            shutil.copytree(io_path(outputs["web"]), io_path(tampered))
            questions = json.loads(io_path(tampered / "runtime_inputs/questions.json").read_text(encoding="utf-8"))
            io_path(tampered / "runtime_inputs/questions.json").write_text(json.dumps(questions[:1]), encoding="utf-8")
            rows = io_path(tampered / "per_question.jsonl").read_text(encoding="utf-8").splitlines()
            io_path(tampered / "per_question.jsonl").write_text(rows[0] + "\n", encoding="utf-8")
            metrics = json.loads(io_path(tampered / "aggregated_metrics.json").read_text(encoding="utf-8"))
            metrics["overall"]["count_all_questions"] = 1
            io_path(tampered / "aggregated_metrics.json").write_text(json.dumps(metrics), encoding="utf-8")
            with self.assertRaises(ValueError):
                package_submission(folder, tampered, outputs["enterprise"], helper.root / "omitted-package", "omitted")
            # Changed run settings cannot be packaged as this frozen pair.
            path = outputs["web"] / "experiment.json"
            value = json.loads(io_path(path).read_text(encoding="utf-8"))
            value["reader"]["model"] = "changed"
            io_path(path).write_text(json.dumps(value), encoding="utf-8")
            with self.assertRaises(ValueError):
                package_submission(folder, outputs["web"], outputs["enterprise"], helper.root / "bad-package", "bad")
