"""Explicit independent runtime settings for one official V2 run."""
import copy
import json
from filesystem import Path, io_path, canonical_path, canonical_paths
import re
from urllib.parse import urlsplit

UPSTREAM_REVISION = "2cc8c540bdb87fe6761629b585e727e1c4704520"
PROCESSOR_REVISION = "c202236235762e1c871ad0ccb60c8ee5ba337b9a"

_DEFAULT_CONTROLLER = {
    "endpoint": "https://ai-srv-md.saga-augmented.ts.net/v1",
    "model": "unsloth/Qwen3.5-9B-MTP-GGUF:BF16",
    "apiKeyEnvironmentVariable": "SCRINIA_CONTROLLER_API_KEY",
    "temperature": 0.6, "topP": 0.95, "topK": 20, "thinking": True,
    "maxOutputTokens": 16384, "networkTimeoutSeconds": 180, "episodeSeconds": 300,
    "maxModelRequests": 40, "maxAttempts": 5,
}

def _keys(value, required, optional=()):
    if not isinstance(value, dict) or set(value) - set(required) - set(optional) or set(required) - set(value):
        raise ValueError("Experiment object has missing or unsupported fields.")

def _endpoint(value):
    parsed = urlsplit(value)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("Model endpoints must be explicit HTTP(S) URLs without credentials.")

def validate_experiment(value):
    _keys(value, ("version", "mode", "domain", "tier", "data", "processor", "run_root",
                  "memory", "controller", "reader", "judge", "limits", "method_fingerprint"),
          ("load_memory_dir", "save_memory"))
    result = copy.deepcopy(value)
    if value["version"] != 1 or value["mode"] not in ("synthetic", "submission"):
        raise ValueError("Unsupported experiment version or mode.")
    if value["domain"] not in ("web", "enterprise") or value["tier"] not in ("small", "medium"):
        raise ValueError("An explicit supported domain and tier are required.")
    _keys(value["data"], ("root", "revision"))
    _keys(value["processor"], ("assets", "model", "revision"))
    if value["processor"]["model"] != "Qwen/Qwen3.5-9B" or value["processor"]["revision"] != PROCESSOR_REVISION:
        raise ValueError("Processor model and revision must match the pinned method.")
    if not isinstance(value["data"]["revision"], str) or not value["data"]["revision"]:
        raise ValueError("An explicit immutable data revision is required.")
    for path in (value["data"]["root"], value["processor"]["assets"], value["run_root"]):
        if not isinstance(path, str) or not Path(path).is_absolute():
            raise ValueError("Experiment paths must be absolute.")
    _keys(value["controller"], ("endpoint", "model", "apiKeyEnvironmentVariable", "temperature",
          "topP", "topK", "thinking", "maxOutputTokens", "networkTimeoutSeconds",
          "episodeSeconds", "maxModelRequests", "maxAttempts"))
    _keys(value["memory"], ("worker_command", "learning"))
    _keys(value["memory"]["learning"], ("mcp_executable", "mcp_prefix_arguments", "tool_catalog_path", "served_context_tokens"))
    _keys(value["reader"], ("model", "base_url", "api_key_env", "temperature", "top_p", "top_k",
                            "enable_thinking", "max_completion_tokens", "memory_context_max_tokens", "timeout_seconds"))
    _keys(value["judge"], ("model", "base_url", "api_key_env", "reasoning_effort",
                           "max_completion_tokens", "timeout_seconds"))
    from urllib.parse import urlsplit
    for settings, endpoint, key in ((value["controller"], "endpoint", "apiKeyEnvironmentVariable"),
                                     (value["reader"], "base_url", "api_key_env"),
                                     (value["judge"], "base_url", "api_key_env")):
        _endpoint(settings[endpoint])
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", settings[key]):
            raise ValueError("Only credential environment-variable names may be configured.")
        if value["mode"] == "synthetic" and urlsplit(settings[endpoint]).hostname not in ("localhost", "127.0.0.1", "::1"):
            raise ValueError("Synthetic runs may use only localhost model endpoints.")
    if (value["reader"]["enable_thinking"] is not True
            or value["reader"]["max_completion_tokens"] != 20000
            or value["reader"]["memory_context_max_tokens"] != 200000
            or (value["reader"]["temperature"], value["reader"]["top_p"], value["reader"]["top_k"]) != (0.6, 0.95, 20)):
        raise ValueError("The official reader operating point must be explicit and unchanged.")
    if value["judge"]["reasoning_effort"] != "medium" or value["judge"]["max_completion_tokens"] != 4096:
        raise ValueError("The official judge operating point must be explicit and unchanged.")
    _keys(value["limits"], ("limit", "question_ids", "prompt_build_max_workers", "reader_max_concurrent_requests"))
    if value["limits"]["prompt_build_max_workers"] != 1 or value["limits"]["reader_max_concurrent_requests"] != 1:
        raise ValueError("This operating point requires one prompt worker and one reader request.")
    if value["mode"] == "submission" and (value["limits"]["limit"] is not None or value["limits"]["question_ids"] is not None):
        raise ValueError("Submission runs forbid question limits and question-ID selection.")
    if value["limits"]["limit"] is not None and (type(value["limits"]["limit"]) is not int or value["limits"]["limit"] <= 0):
        raise ValueError("Question limit must be a positive integer.")
    ids = value["limits"]["question_ids"]
    if ids is not None and (not isinstance(ids, list) or not ids or not all(isinstance(i, str) and i for i in ids)):
        raise ValueError("Question IDs must be a nonempty string list.")
    if not isinstance(value["method_fingerprint"], str) or not re.fullmatch("[0-9a-f]{64}", value["method_fingerprint"]):
        raise ValueError("An exact method fingerprint is required.")
    if "save_memory" in value and type(value["save_memory"]) is not bool:
        raise ValueError("save_memory must be boolean.")
    if value.get("load_memory_dir") is not None and not Path(value["load_memory_dir"]).is_absolute():
        raise ValueError("load_memory_dir must be absolute.")
    return result

def memory_params(config):
    return {"run_root": config["run_root"], "worker_command": config["memory"]["worker_command"],
            "controller": config["controller"], "trajectories_root_dir": config["data"]["root"],
            "learning": {**config["memory"]["learning"], "processor_assets": config["processor"]["assets"]}}

def method_fingerprint(config):
    from portability import method_identity, identity_hash
    return identity_hash({"memory": method_identity(memory_params(config)), "reader": config["reader"],
                          "judge": config["judge"], "upstreamRevision": UPSTREAM_REVISION})
