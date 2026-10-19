"""External LongMemEval V2 Memory adapter; import before building the harness."""
import hashlib
import json
import shutil
import time
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
        allowed = {"run_root", "worker_command", "controller", "trajectories_root_dir", "learning"}
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
        self.learning_params = memory_params.get("learning")
        instance_root = run_root / ("memory-" + uuid.uuid4().hex)
        self.paths = {"root": str(instance_root), "workspace": str(instance_root / "workspace"),
                      "home": str(instance_root / "home"),
                      "eventLogPath": str(instance_root / "events" / "initial.jsonl")}
        self.snapshot_path = str(instance_root / "snapshot")
        # Validate before creating directories or writing protocol files.
        validation=self._request("retrieve",question="configuration validation",snapshotPath=self.snapshot_path,
                                 questionImage=None,expectedFingerprint="0"*64,
                                 retrieval={"executable":str(instance_root/"validation.exe"),"prefixArguments":[],"catalogHash":"0"*64,"instructionsHash":"0"*64})
        validation["paths"]={**validation["paths"],"workspace":str(Path(self.snapshot_path)/"workspace"),"home":str(Path(self.snapshot_path)/"home")}
        validate_request(validation)
        super().__init__(memory_params)
        for name in ("workspace", "home"):
            Path(self.paths[name]).mkdir(parents=True, exist_ok=False)
        self.bridge = WorkerBridge(command, instance_root / "operations")
        self.cancel_event = threading.Event()
        from snapshots import SnapshotStore
        self.snapshots = SnapshotStore(instance_root / "snapshots", self._learning_request)

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

    def _learning_request(self, operation, **fields):
        if operation == "learn":
            fields["learning"] = dict(self._learning_configuration)
        return self._request(operation, **fields)

    def insert(self, trajectory: dict[str, object]) -> None:
        import json
        from batching import BatchPlanner, ProcessorCounter, catalog_hash, instruction_text
        from trajectory_input import normalize_trajectory, canonical, digest, PROCESSOR_REVISION
        settings = self.learning_params
        required = {"mcp_executable", "mcp_prefix_arguments", "processor_assets", "tool_catalog_path", "served_context_tokens"}
        if not isinstance(settings, dict) or set(settings) != required:
            raise ValueError("Learning requires explicit MCP launch, pinned processor assets, tool catalog and served context.")
        executable = _absolute(settings["mcp_executable"])
        assets = _absolute(settings["processor_assets"])
        descriptor = _absolute(settings["tool_catalog_path"])
        if not executable.is_file() or not descriptor.is_file() or not assets.is_dir():
            raise ValueError("Learning runtime assets are missing.")
        tools = json.loads(descriptor.read_text(encoding="utf-8"))
        if not isinstance(tools, list) or {t.get("name") for t in tools}.issuperset({"remember","recall","search","list"}) is False:
            raise ValueError("Learning catalog must contain the required tools.")
        if (not isinstance(settings["mcp_prefix_arguments"], list)
            or not all(isinstance(a,str) for a in settings["mcp_prefix_arguments"])):
            raise ValueError("MCP prefix arguments must be an argument list.")
        self._learning_configuration = {"executable":str(executable),"prefixArguments":settings["mcp_prefix_arguments"],
            "toolCatalogJson":canonical(tools),"catalogHash":catalog_hash(tools),
            "instructionsHash":digest(instruction_text().encode("utf-8")),"processorRevision":PROCESSOR_REVISION,
            "inputTokenLimit":24576,"fragmentTokenLimit":4096,"imageLimit":4,"servedContextTokens":settings["served_context_tokens"]}
        root_value = self.memory_params.get("trajectories_root_dir")
        if not isinstance(root_value,str):
            raise ValueError("An explicit trajectories_root_dir is required for learning.")
        normalized = normalize_trajectory(trajectory,_absolute(root_value))
        counter = ProcessorCounter(assets)
        planned = BatchPlanner(counter,tools,output_tokens=self.configuration["maxOutputTokens"],
            served_context=settings["served_context_tokens"]).prepare(normalized)
        configuration_hash = digest(canonical({"controller":self.configuration,
            "learning":self._learning_configuration,
            "executableHash":digest(executable.read_bytes())}).encode("utf-8"))
        self.snapshots.insert(planned,configuration_hash,self.bridge,self.cancel_event)
        self.snapshot_path = str(self.snapshots.generation_path)

    def _own_image(self, value, target_root=None):
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
        target = (Path(self.paths["root"]) if target_root is None else target_root) / "images" / (digest + suffix)
        _absolute(str(target))
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if target.read_bytes() != data:
                raise BridgeError("IMAGE_HASH_MISMATCH")
        else:
            with target.open("xb") as stream:
                stream.write(data)
        return {"path": str(target), "sha256": digest, "mediaType": media, "byteLength": len(data)}

    def _retrieval_configuration(self):
        from batching import catalog_hash
        settings=self.learning_params
        if not isinstance(settings,dict) or not {"mcp_executable","mcp_prefix_arguments","tool_catalog_path"}.issubset(settings):
            raise ValueError("Retrieval requires explicit MCP executable, prefix arguments and captured catalog.")
        executable=_absolute(settings["mcp_executable"])
        descriptor=_absolute(settings["tool_catalog_path"])
        arguments=settings["mcp_prefix_arguments"]
        if not executable.is_file() or not descriptor.is_file() or not isinstance(arguments,list) or not all(isinstance(a,str) for a in arguments):
            raise ValueError("Retrieval runtime assets are invalid.")
        tools=json.loads(descriptor.read_text(encoding="utf-8"))
        if not isinstance(tools,list) or not {"search","list","recall"}.issubset(t.get("name") for t in tools):
            raise ValueError("Retrieval catalog lacks required read tools.")
        instructions=Path(__file__).parents[1]/"Scrinia.LongMemEvalV2.Worker"/"Retrieval"/"retrieval-instructions.txt"
        return {"executable":str(executable),"prefixArguments":arguments,"catalogHash":catalog_hash(tools),
                "instructionsHash":hashlib.sha256(instructions.read_bytes()).hexdigest()}

    def query(self, query: str, query_image: str | None = None) -> list[dict[str, str]]:
        from snapshots import fingerprint, file_entries
        started=time.monotonic()
        deadline=started+self.configuration["episodeSeconds"]
        parent=self.cancel_event
        class QueryCancellation:
            def is_set(self):return parent.is_set() or time.monotonic()>=deadline
        cancel=QueryCancellation()
        if cancel.is_set():raise BridgeError("CANCELLED")
        if not isinstance(query,str) or not query:raise ValueError("Question must be non-empty text.")
        runtime=self._retrieval_configuration()
        source=_absolute(self.snapshot_path)
        if not source.is_dir():raise BridgeError("SNAPSHOT_MISSING")
        if (self.snapshots.root/"current.json").exists():
            self.snapshots.current()
            if source!=self.snapshots.generation_path:raise BridgeError("SNAPSHOT_POINTER_MISMATCH")
        before=fingerprint(source)
        metadata=source/".generation.json"
        metadata_before=hashlib.sha256(metadata.read_bytes()).hexdigest() if metadata.exists() else None
        operation=Path(self.paths["root"])/"queries"/uuid.uuid4().hex
        payload=operation/"snapshot"
        _absolute(str(payload));operation.mkdir(parents=True)
        copied=False
        try:
            # Preflight every path before copytree; reject links rather than following them.
            file_entries(source)
            shutil.copytree(source,payload);copied=True
            if fingerprint(payload)!=before:raise BridgeError("QUERY_COPY_CHANGED")
            image=None if query_image is None else self._own_image(query_image,operation)
            request=self._request("retrieve",snapshotPath=str(payload),expectedFingerprint=before,
                                  retrieval=runtime,question=query,questionImage=image)
            request["paths"]={**request["paths"],"workspace":str(payload/"workspace"),"home":str(payload/"home"),
                              "eventLogPath":str(operation/"events.jsonl")}
            if cancel.is_set():raise BridgeError("CANCELLED")
            result=self.bridge.run("retrieve",request,cancel)
            # Only original catalog images may cross the reader boundary, even if a
            # compromised worker reports another owned path.
            originals={}
            for catalog in (payload/"catalogs").glob("*.json"):
                for state in json.loads(catalog.read_text(encoding="utf-8"))["trajectory"]["states"]:
                    image_ref=state["screenshot"]
                    path=_absolute(str(payload/image_ref["path"]))
                    if not path.is_relative_to(payload):raise BridgeError("INVALID_EVIDENCE_IMAGE")
                    originals[path]=image_ref
            evidence=[]
            for item in result["evidence"]:
                if item["type"]=="image":
                    path=_absolute(item["value"])
                    ref=originals.get(path)
                    if ref is None:raise BridgeError("INVALID_EVIDENCE_IMAGE")
                    data=path.read_bytes()
                    if len(data)!=ref["byteLength"] or hashlib.sha256(data).hexdigest()!=ref["sha256"]:
                        raise BridgeError("EVIDENCE_IMAGE_CHANGED")
                    target=operation/"evidence"/(ref["sha256"]+path.suffix)
                    target.parent.mkdir(exist_ok=True)
                    target.write_bytes(data)
                    evidence.append({"type":"image","value":str(target)})
                else:evidence.append(dict(item))
            return evidence
        finally:
            try:
                metadata_after=hashlib.sha256(metadata.read_bytes()).hexdigest() if metadata.exists() else None
                if fingerprint(source)!=before or metadata_after!=metadata_before:raise BridgeError("AUTHORITATIVE_SNAPSHOT_CHANGED")
                copied_metadata=payload/".generation.json"
                if copied and (hashlib.sha256(copied_metadata.read_bytes()).hexdigest() if copied_metadata.exists() else None)!=metadata_before:
                    raise BridgeError("QUERY_COPY_CHANGED")
                if copied and fingerprint(payload)!=before:raise BridgeError("QUERY_COPY_CHANGED")
            finally:
                # This UUID directory is created here and contained in the owned run root.
                checked=_absolute(str(payload))
                if not checked.is_relative_to(operation) or not operation.is_relative_to(Path(self.paths["root"])):
                    raise BridgeError("INVALID_PATH")
                if payload.exists():shutil.rmtree(payload)
                if cancel.is_set():raise BridgeError("CANCELLED")

    def _save_backend(self, output_dir: Path) -> None:
        raise NotImplementedError("V2 snapshot persistence is not implemented yet.")

    def _load_backend(self, input_dir: Path) -> None:
        raise NotImplementedError("V2 snapshot restoration is not implemented yet.")
