"""Pinned upstream normalization plus complete source provenance (no model calls)."""
import hashlib
import json
from pathlib import Path
from urllib.parse import urlsplit
from bridge import _absolute

PROCESSOR_REVISION = "c202236235762e1c871ad0ccb60c8ee5ba337b9a"

def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=True, separators=(",", ":"))

def digest(data):
    return hashlib.sha256(data).hexdigest()

def image_reference(path):
    path = _absolute(str(path))
    data = path.read_bytes()
    if data.startswith(b"\x89PNG\r\n\x1a\n"): media = "image/png"
    elif data.startswith(b"\xff\xd8\xff"): media = "image/jpeg"
    elif data.startswith(b"RIFF") and data[8:12] == b"WEBP": media = "image/webp"
    else: raise ValueError("Unsupported source image.")
    # Decode now: a matching magic header alone is not a usable image.
    from PIL import Image
    with Image.open(path) as image: image.verify()
    return {"path":str(path), "sha256":digest(data), "mediaType":media, "byteLength":len(data)}

def normalize_trajectory(trajectory: dict, asset_root: Path) -> dict:
    # Refuse URIs before invoking upstream's filesystem resolver.
    for state in trajectory.get("states") or trajectory.get("content") or []:
        value = state.get("screenshot")
        # Legacy upstream content nests observation; prepare() remains authoritative.
        if value is None and isinstance(state.get("observation"), dict):
            value = state["observation"].get("screenshot")
        if isinstance(value, str) and (urlsplit(value).scheme and not Path(value).is_absolute()):
            raise ValueError("Source screenshots must be local files.")
        if isinstance(value, str) and value.lower().startswith(("http:", "https:", "data:", "file:", "ftp:")):
            raise ValueError("Source screenshots must be local files.")
    from memory_modules.trajectory_store import prepare_trajectory_insert
    prepared = prepare_trajectory_insert(trajectory, trajectories_root_dir=_absolute(str(asset_root)))
    value = prepared.simplified
    states = []
    for state, path in zip(value["states"], prepared.screenshot_sources, strict=True):
        states.append({"stateIndex":state["state_index"], "step":state["step"], "url":state["url"],
            "action":state["action"], "thoughts":state["thoughts"], "text":state["text"],
            "screenshot":image_reference(path)})
    result = {"id":value["id"], "goal":value["goal"], "outcome":value["outcome"],
        "startUrl":value["start_url"], "actions":value["actions"], "states":states,
        "originalJson":canonical(trajectory)}
    logical = json.loads(canonical(result))
    for state in logical["states"]: state["screenshot"].pop("path")
    result["sourceHash"] = digest(canonical(logical).encode("utf-8"))
    return result
