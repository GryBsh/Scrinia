"""Register Scrinia and invoke the pinned official harness in this process."""
import argparse
import hashlib
import json
import os
from filesystem import Path, io_name, io_path, canonical_path, canonical_paths
import shutil
import subprocess
import sys
from experiment_config import (PROCESSOR_REVISION, UPSTREAM_REVISION, memory_params,
                               method_fingerprint, validate_experiment)

def upstream_hashes(root):
    files = []
    for directory in ("evaluation", "memory_modules", "data", "leaderboard"):
        files.extend(canonical_paths((io_path(root / directory)).rglob("*.py")))
    return {p.relative_to(root).as_posix(): hashlib.sha256(io_path(p).read_bytes()).hexdigest()
            for p in sorted(files)}

def _processor_cache(config, output):
    assets = Path(config["processor"]["assets"])
    cache = output / "processor-cache"
    snapshot = cache / "hub/models--Qwen--Qwen3.5-9B/snapshots" / PROCESSOR_REVISION
    io_path(snapshot.parent).mkdir(parents=True)
    shutil.copytree(io_path(assets), io_path(snapshot))
    refs = snapshot.parent.parent / "refs"
    io_path(refs).mkdir()
    (io_path(refs / "main")).write_text(PROCESSOR_REVISION, encoding="utf-8")
    os.environ["HF_HOME"] = io_name(cache)
    os.environ["HF_HUB_CACHE"] = io_name(cache / "hub")
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    # Enforce before eager memory backend imports; never patch the official processor.
    if "huggingface_hub.constants" in sys.modules or "transformers" in sys.modules:
        raise RuntimeError("Run the launcher in a fresh Python process before Hugging Face imports.")
    return snapshot

def run_experiment(upstream_root, config, output_dir):
    config = validate_experiment(config)
    upstream_root = canonical_path(io_path(Path(upstream_root)).resolve())
    output = Path(output_dir).absolute()
    if io_path(output).exists():
        raise ValueError("Output directory must be new; graded queries are never resumed or merged.")
    revision = subprocess.check_output(["git", "-C", str(upstream_root), "rev-parse", "HEAD"], text=True).strip()
    if revision != UPSTREAM_REVISION:
        raise ValueError("Official upstream revision does not match the pin.")
    subprocess.run(["git", "-C", str(upstream_root), "diff", "--exit-code", UPSTREAM_REVISION, "--",
                    "evaluation", "memory_modules", "data", "leaderboard"], check=True, capture_output=True)
    before = upstream_hashes(upstream_root)
    io_path(output).mkdir(parents=True)
    status = {"status": "incomplete", "upstreamRevision": revision, "methodFingerprint": config["method_fingerprint"]}
    def write(name, value):
        (io_path(output / name)).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    write("scrinia_run.json", status)
    try:
        snapshot = _processor_cache(config, output)
        sys.path.insert(0, str(upstream_root))
        import scrinia_memory  # Registers external backend before official harness imports.
        from portability import method_identity
        from snapshots import file_entries
        if method_fingerprint(config) != config["method_fingerprint"]:
            raise ValueError("Experiment method fingerprint does not match configured bytes/settings.")
        params = memory_params(config)
        params["method_identity"] = method_identity(params)
        write("experiment.json", config)
        write("upstream_hashes.json", before)
        write("processor_hashes.json", dict(file_entries(snapshot)))
        runtime = output / "runtime_inputs"
        io_path(runtime).mkdir()
        from data.public_data import materialize_runtime_questions, materialize_runtime_haystack
        selected = materialize_runtime_questions(data_root=Path(io_name(config["data"]["root"])), domain=config["domain"],
            question_ids=config["limits"]["question_ids"], limit=config["limits"]["limit"],
            output_path=Path(io_name(runtime / "questions.json")))
        materialize_runtime_haystack(data_root=Path(io_name(config["data"]["root"])), tier=config["tier"],
            selected_questions=selected, output_path=Path(io_name(runtime / "haystack.json")))
        (io_path(runtime / "memory_config.json")).write_text(json.dumps(
            {"memory_type": "scrinia", "memory_params": params}, indent=2), encoding="utf-8")
        args = ["evaluation.harness", "--domain", config["domain"],
                "--questions-path", io_name(runtime / "questions.json"),
                "--haystack-path", io_name(runtime / "haystack.json"),
                "--trajectories-path", io_name(Path(config["data"]["root"]) / "trajectories.jsonl"),
                "--memory-config-path", io_name(runtime / "memory_config.json"), "--output-dir", io_name(output)]
        for key, option in (("model", "model"), ("base_url", "base-url"), ("api_key_env", "api-key-env"),
                            ("temperature", "temperature"), ("top_p", "top-p"), ("top_k", "top-k"),
                            ("max_completion_tokens", "max-completion-tokens"),
                            ("memory_context_max_tokens", "memory-context-max-tokens"), ("timeout_seconds", "timeout-seconds")):
            args.extend(["--" + option, str(config["reader"][key])])
        args.append("--reader-enable-thinking")
        for key in ("model", "base_url", "api_key_env", "reasoning_effort", "max_completion_tokens", "timeout_seconds"):
            args.extend(["--evaluator-" + key.replace("_", "-"), str(config["judge"][key])])
        for key in ("prompt_build_max_workers", "reader_max_concurrent_requests"):
            args.extend(["--" + key.replace("_", "-"), str(config["limits"][key])])
        if config.get("save_memory"):
            args.append("--save-memory")
        if config.get("load_memory_dir"):
            args.extend(["--load-memory-dir", io_name(config["load_memory_dir"])])
        old_argv = sys.argv
        try:
            sys.argv = args
            from evaluation.harness import main
            main()
        finally:
            sys.argv = old_argv
        # Use upstream completeness validation, without creating or publishing a package.
        from leaderboard.submission_utils import validate_run
        validate_run(Path(io_name(output)), config["domain"], config["tier"], method_override="scrinia")
        status["status"] = "complete"
    finally:
        after = upstream_hashes(upstream_root)
        status["upstreamUnchanged"] = after == before
        if after != before:
            status["status"] = "incomplete"
        write("scrinia_run.json", status)
        if after != before:
            raise RuntimeError("Official evaluator files changed during the run.")
    return output

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream-root", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()
    run_experiment(args.upstream_root, json.loads(io_path(Path(args.config)).read_text(encoding="utf-8")), args.output_dir)

if __name__ == "__main__":
    main()
