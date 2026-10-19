"""Verify a frozen experiment offline, or probe explicitly configured live endpoints."""
import argparse
import json
import os
from pathlib import Path
import sys
from freeze_experiment import verify_frozen, read
from filesystem import io_path
from snapshots import atomic_json

def offline_preflight(folder):
    manifest = verify_frozen(folder)
    pair = read(Path(folder) / "resolved_pair.json")
    # Exercise the unchanged official request builder without a network connection.
    sys.path.insert(0, pair["upstream_root"])
    from types import SimpleNamespace
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    from evaluation.harness import build_reader_request
    config = pair["experiments"]["web"]
    settings = config["reader"]
    args = SimpleNamespace(**settings, reader_enable_thinking=True,
        repetition_penalty=None, reasoning_effort=None, presence_penalty=None)
    messages = [{"role": "user", "content": [{"type": "text", "text": "Invented offline probe"},
        {"type": "image_url", "image_url": {"url": "data:image/png;base64,aW52ZW50ZWQ="}}]}]
    request = build_reader_request(args, messages)
    if (request["messages"] != messages or request["max_tokens"] != 20000
            or request.get("extra_body") != {"top_k": 20}
            or config["controller"]["maxOutputTokens"] != 16384
            or config["controller"]["thinking"] is not True):
        raise ValueError("Offline reader/controller protocol contract changed.")
    credential_names = sorted({config["controller"]["apiKeyEnvironmentVariable"],
        settings["api_key_env"], config["judge"]["api_key_env"]})
    return {"offline": "verified", "live": "unverified", "image_serialization": "wire-only",
        "reader_thinking": "requires-server-default", "credential_variable_names": credential_names,
        "credentials_present": {name: bool(os.environ.get(name, "").strip()) for name in credential_names},
        "capabilities": manifest["capabilities"],
        "source_archive_acceptance": manifest["source_archive_acceptance"]}

def live_preflight(folder, attestation):
    # Revoke earlier readiness before validating any part of a new live attempt.
    folder = Path(folder).absolute()
    readiness = folder.parent / (folder.name + "-live-preflight.json")
    io_path(readiness).unlink(missing_ok=True)
    if not attestation:
        raise ValueError("--live requires --deployment-attestation with independently verified deployment evidence.")
    # --live authorizes synthetic endpoint calls only, never evaluation or submission.
    offline = offline_preflight(folder)
    if not all(offline["credentials_present"].values()):
        raise ValueError("Required credential environment variables are missing.")
    from live_probe import probe_pair
    result = probe_pair(folder, attestation)
    atomic_json(readiness, result)
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frozen-experiment", required=True)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--offline", action="store_true")
    modes.add_argument("--live", action="store_true")
    parser.add_argument("--deployment-attestation")
    args = parser.parse_args()
    result = live_preflight(args.frozen_experiment, args.deployment_attestation) if args.live else offline_preflight(args.frozen_experiment)
    print(json.dumps(result, indent=2, sort_keys=True))

if __name__ == "__main__":
    main()
