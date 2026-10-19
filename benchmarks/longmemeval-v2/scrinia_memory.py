"""External LongMemEval V2 Memory adapter; import before building the harness."""
import hashlib
from pathlib import Path
import threading
import uuid
from urllib.parse import urlparse

from memory_modules.memory import Memory, register_memory
from bridge import BridgeError, WorkerBridge, _absolute, validate_request

_DEFAULT_CONTROLLER = {
    "endpoint": "https://ai-srv-md.saga-augmented.ts.net/v1",
    "model": "unsloth/Qwen3.5-9B-MTP-GGUF:BF16",
    "apiKeyEnvironmentVariable": "SCRINIA_CONTROLLER_API_KEY",
    "temperature": 0.6, "topP": 0.95, "topK": 20, "thinking": True,
    "maxOutputTokens": 16384, "networkTimeoutSeconds": 180, "episodeSeconds": 300,
    "maxModelRequests": 40, "maxAttempts": 5,
}

@register_memory
class ScriniaMemory(Memory):
    memory_type = "scrinia"

    def __init__(self, memory_params: dict[str, object]) -> None:
        allowed = {"run_root", "worker_command", "controller", "trajectories_root_dir"}
        if not isinstance(memory_params, dict) or set(memory_params) - allowed:
            raise ValueError("Unsupported Scrinia memory parameter.")
        root_value = memory_params.get("run_root")
        if not isinstance(root_value, str) or not root_value:
            raise ValueError("An explicit absolute run_root is required.")
        run_root = _absolute(root_value)
        command = memory_params.get("worker_command")
        if (not isinstance(command, list) or not command
                or not all(isinstance(value, str) and value for value in command)):
            raise ValueError("worker_command must be an explicit argument list.")
        controller = memory_params.get("controller", {})
        if not isinstance(controller, dict) or set(controller) - set(_DEFAULT_CONTROLLER):
            raise ValueError("Unsupported controller setting.")
        self.configuration = {**_DEFAULT_CONTROLLER, **controller}
        instance_root = run_root / ("memory-" + uuid.uuid4().hex)
        self.paths = {"root": str(instance_root), "workspace": str(instance_root / "workspace"),
                      "home": str(instance_root / "home"),
                      "eventLogPath": str(instance_root / "events" / "initial.jsonl")}
        self.snapshot_path = str(instance_root / "snapshot")
        # Validate before creating directories or writing protocol files.
        validate_request(self._request("retrieve", question="configuration validation",
                                       snapshotPath=self.snapshot_path, questionImage=None))
        super().__init__(memory_params)
        for name in ("workspace", "home"):
            Path(self.paths[name]).mkdir(parents=True, exist_ok=False)
        self.bridge = WorkerBridge(command, instance_root / "operations")
        self.cancel_event = threading.Event()

    def configure_runtime(self, **kwargs: object) -> None:
        # The official harness passes reader sampling/trace hints to every backend.
        # Controller sampling and trace paths remain independently owned by this adapter.
        if set(kwargs) - {"cancel_event", "query_trace_dir", "generation_temperature", "generation_top_p"}:
            raise ValueError("Unsupported runtime setting.")
        if "cancel_event" in kwargs:
            event = kwargs["cancel_event"]
            if not callable(getattr(event, "is_set", None)):
                raise ValueError("cancel_event must provide is_set().")
            self.cancel_event = event

    def _request(self, operation, **fields):
        operation_id = uuid.uuid4().hex
        return {"version": 1, "operation": operation, "operationId": operation_id,
                "configuration": dict(self.configuration),
                "paths": {**self.paths, "eventLogPath":
                    str(Path(self.paths["root"]) / "events" / (operation_id + ".jsonl"))},
                **fields}

    def insert(self, trajectory: dict[str, object]) -> None:
        raise NotImplementedError("V2 trajectory learning is not implemented yet.")

    def _own_image(self, value):
        if not isinstance(value, str) or not value or urlparse(value).scheme.lower() in (
                "http", "https", "data", "file", "ftp"):
            raise ValueError("Question images must be local files.")
        source = _absolute(str(Path(value).absolute()))
        if not source.is_file():
            raise ValueError("Question image file is missing.")
        data = source.read_bytes()
        if data.startswith(b"\x89PNG\r\n\x1a\n"):
            media, suffix = "image/png", ".png"
        elif data.startswith(b"\xff\xd8\xff"):
            media, suffix = "image/jpeg", ".jpg"
        elif data.startswith(b"RIFF") and data[8:12] == b"WEBP":
            media, suffix = "image/webp", ".webp"
        else:
            raise ValueError("Unsupported question image format.")
        digest = hashlib.sha256(data).hexdigest()
        target = Path(self.paths["root"]) / "images" / (digest + suffix)
        _absolute(str(target))
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if target.read_bytes() != data:
                raise BridgeError("IMAGE_HASH_MISMATCH")
        else:
            with target.open("xb") as stream:
                stream.write(data)
        return {"path": str(target), "sha256": digest, "mediaType": media, "byteLength": len(data)}

    def query(self, query: str, query_image: str | None = None) -> list[dict[str, str]]:
        if self.cancel_event.is_set():
            raise BridgeError("CANCELLED")
        if not isinstance(query, str) or not query:
            raise ValueError("Question must be non-empty text.")
        image = None if query_image is None else self._own_image(query_image)
        request = self._request("retrieve", snapshotPath=self.snapshot_path,
                                question=query, questionImage=image)
        result = self.bridge.run("retrieve", request, self.cancel_event)
        return result["evidence"]

    def _save_backend(self, output_dir: Path) -> None:
        raise NotImplementedError("V2 snapshot persistence is not implemented yet.")

    def _load_backend(self, input_dir: Path) -> None:
        raise NotImplementedError("V2 snapshot restoration is not implemented yet.")
