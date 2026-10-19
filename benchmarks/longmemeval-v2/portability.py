"""Byte-verified local runtime bindings and committed-snapshot transport."""
import copy
import hashlib
import json
from filesystem import Path, io_path, canonical_path, canonical_paths
import shutil
from bridge import BridgeError, _absolute, read_json
from snapshots import atomic_json, file_entries, fingerprint

def identity_hash(identity):
    return hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

def _file(path):
    path = _absolute(str(path))
    if not io_path(path).is_file():
        raise ValueError("A configured runtime file is missing.")
    return hashlib.sha256(io_path(path).read_bytes()).hexdigest()

def _command(arguments):
    if not isinstance(arguments, list) or not arguments or not all(isinstance(a, str) and a for a in arguments):
        raise ValueError("Runtime commands must be nonempty argument lists.")
    result = []
    for index, argument in enumerate(arguments):
        path = Path(argument)
        if index == 0 and not path.is_absolute():
            located = shutil.which(argument)
            if located is None:
                raise ValueError("Runtime executable cannot be resolved.")
            path = Path(located)
        if path.is_absolute():
            result.append({"fileHash": _file(path)})
        else:
            if io_path(path).is_file():
                raise ValueError("Runtime file arguments must be absolute.")
            result.append({"argument": argument})
    return result

def method_identity(params):
    from experiment_config import _DEFAULT_CONTROLLER
    from batching import catalog_hash
    from trajectory_input import PROCESSOR_REVISION
    settings = params.get("learning")
    required = {"mcp_executable", "mcp_prefix_arguments", "processor_assets", "tool_catalog_path", "served_context_tokens"}
    if not isinstance(settings, dict) or set(settings) != required:
        raise ValueError("Portable memory requires complete explicit learning settings.")
    tools = read_json(_absolute(settings["tool_catalog_path"]))
    if not isinstance(tools, list) or not {"remember", "recall", "search", "list"}.issubset(t["name"] for t in tools):
        raise ValueError("Captured tool catalog is incomplete.")
    assets = _absolute(settings["processor_assets"])
    if not io_path(assets).is_dir() or not file_entries(assets):
        raise ValueError("Pinned processor assets are missing.")
    source = Path(__file__).parents[1] / "Scrinia.LongMemEvalV2.Worker"
    return {"version": 1, "controller": {**_DEFAULT_CONTROLLER, **params.get("controller", {})},
            "worker": _command(params["worker_command"]),
            "mcp": _command([settings["mcp_executable"], *settings["mcp_prefix_arguments"]]),
            "catalogHash": catalog_hash(tools), "processorRevision": PROCESSOR_REVISION,
            "processorFiles": [list(entry) for entry in file_entries(assets)],
            "servedContextTokens": settings["served_context_tokens"],
            "learningInstructionsHash": _file(source / "Learning/learning-instructions.txt"),
            "retrievalInstructionsHash": _file(source / "Retrieval/retrieval-instructions.txt"),
            "batchPolicy": {"inputTokens": 24576, "fragmentTokens": 4096, "images": 4,
                            "operationOverheadEpisodes": 2}}

def reconcile(saved, requested):
    if saved.get("memory_type") != "scrinia" or (requested and requested.get("memory_type") != "scrinia"):
        raise ValueError("Saved and requested memory types must be scrinia.")
    recorded = saved["memory_params"].get("method_identity")
    if not isinstance(recorded, dict):
        raise ValueError("Saved memory lacks a portable method identity.")
    effective = copy.deepcopy(saved if requested is None else requested)
    params = effective["memory_params"]
    actual = method_identity(params)
    if actual != recorded or ("method_identity" in params and params["method_identity"] != actual):
        raise BridgeError("METHOD_IDENTITY_MISMATCH")
    params["method_identity"] = actual
    return effective

def _assets(snapshot):
    # SourceCatalog requires relative owned images. Do not interpret source originalJson as a path.
    for catalog in canonical_paths((io_path(snapshot / "catalogs")).glob("*.json")):
        value = read_json(catalog)
        for state in value["trajectory"]["states"]:
            ref = state["screenshot"]
            relative = Path(ref["path"])
            if relative.is_absolute() or relative.as_posix().split("/")[0] != "sources" or ".." in relative.parts:
                raise BridgeError("INVALID_BUNDLE_ASSET")
            path = _absolute(str(snapshot / relative))
            if not path.is_relative_to(snapshot) or not io_path(path).is_file():
                raise BridgeError("MISSING_BUNDLE_ASSET")
            data = io_path(path).read_bytes()
            if len(data) != ref["byteLength"] or hashlib.sha256(data).hexdigest() != ref["sha256"]:
                raise BridgeError("BUNDLE_ASSET_CHANGED")

def _validated(memory):
    if memory.cancel_event.is_set():
        raise BridgeError("CANCELLED")
    if any(canonical_paths((io_path(memory.snapshots.root / "uncertain")).glob("*.json"))):
        raise BridgeError("INSERTION_UNCERTAIN")
    meta = memory.snapshots.current()
    if meta is None:
        raise BridgeError("SNAPSHOT_MISSING")
    actual = method_identity(memory.memory_params)
    if (memory.memory_params.get("method_identity") != actual or
            meta["configurationHash"] != identity_hash(actual)):
        raise BridgeError("METHOD_IDENTITY_MISMATCH")
    source = memory.snapshots.generation_path
    if any(p.name not in {"workspace", "home", "sources", "catalogs", ".generation.json"} for p in canonical_paths(io_path(source).iterdir())):
        raise BridgeError("UNEXPECTED_SNAPSHOT_PAYLOAD")
    _assets(source)
    memory.bridge.run("validate-snapshot", memory._request(
        "validate-snapshot", snapshotPath=str(source), expectedFingerprint=meta["fingerprint"]), memory.cancel_event)
    memory.snapshots.current()
    return source, meta, actual

def save_snapshot(memory, output):
    output = _absolute(str(output))
    source, meta, identity = _validated(memory)
    target = output / "snapshot"
    if io_path(target).exists() or (io_path(output / "bundle.json")).exists():
        raise BridgeError("BUNDLE_ALREADY_EXISTS")
    shutil.copytree(io_path(source), io_path(target))
    if fingerprint(target) != meta["fingerprint"] or (io_path(target / ".generation.json")).read_bytes() != (io_path(source / ".generation.json")).read_bytes():
        raise BridgeError("BUNDLE_COPY_CHANGED")
    _assets(target)
    if memory.cancel_event.is_set():
        raise BridgeError("CANCELLED")
    if memory.snapshots.current() != meta:
        raise BridgeError("SNAPSHOT_CHANGED")
    atomic_json(output / "bundle.json", {"version": 1, "methodIdentity": identity,
        "generation": source.name, "fingerprint": meta["fingerprint"],
        "metadataHash": _file(target / ".generation.json")})

def load_snapshot(memory, input_dir):
    input_dir = _absolute(str(input_dir))
    bundle = read_json(input_dir / "bundle.json")
    if set(bundle) != {"version", "methodIdentity", "generation", "fingerprint", "metadataHash"} or bundle["version"] != 1:
        raise BridgeError("INVALID_BUNDLE")
    actual = method_identity(memory.memory_params)
    if bundle["methodIdentity"] != actual:
        raise BridgeError("METHOD_IDENTITY_MISMATCH")
    source = input_dir / "snapshot"
    generation = bundle["generation"]
    if not isinstance(generation, str) or len(generation) != 32 or any(c not in "0123456789abcdef" for c in generation):
        raise BridgeError("INVALID_BUNDLE")
    file_entries(source)
    if any(p.name not in {"workspace", "home", "sources", "catalogs", ".generation.json"}
           for p in canonical_paths(io_path(source).iterdir())):
        raise BridgeError("UNEXPECTED_SNAPSHOT_PAYLOAD")
    if _file(source / ".generation.json") != bundle["metadataHash"] or fingerprint(source) != bundle["fingerprint"]:
        raise BridgeError("BUNDLE_FINGERPRINT_MISMATCH")
    meta = read_json(source / ".generation.json")
    if meta["configurationHash"] != identity_hash(actual):
        raise BridgeError("METHOD_IDENTITY_MISMATCH")
    _assets(source)
    if memory.snapshots.current() is not None:
        raise BridgeError("SNAPSHOT_ALREADY_EXISTS")
    target = memory.snapshots.root / "generations" / generation
    io_path(target.parent).mkdir(parents=True, exist_ok=True)
    shutil.copytree(io_path(source), io_path(target))
    memory.bridge.run("validate-snapshot", memory._request("validate-snapshot",
        snapshotPath=str(target), expectedFingerprint=bundle["fingerprint"]), memory.cancel_event)
    if memory.cancel_event.is_set():
        raise BridgeError("CANCELLED")
    if fingerprint(target) != bundle["fingerprint"] or _file(target / ".generation.json") != bundle["metadataHash"]:
        raise BridgeError("BUNDLE_COPY_CHANGED")
    # Verify receipt/pointer contracts before publication as well as after.
    if meta["fingerprint"] != bundle["fingerprint"] or meta["receipt"]["newSnapshotHash"] != bundle["fingerprint"]:
        raise BridgeError("INVALID_BUNDLE")
    atomic_json(memory.snapshots.root / "current.json", {k: bundle[k] for k in
                ("generation", "fingerprint", "metadataHash")})
    memory.snapshots.current()
    memory.snapshot_path = str(target)
