"""Strict protocol and lifetime boundary for one owned V2 worker process."""
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import time
import uuid
from jsonschema import Draft202012Validator

_SCHEMA = json.loads(Path(__file__).with_name("protocol.schema.json").read_text())
_REQUEST = Draft202012Validator(_SCHEMA)
_RESULT = Draft202012Validator({"$ref": "#/$defs/result", "$defs": _SCHEMA["$defs"]})

class BridgeError(RuntimeError):
    """A worker failed without returning usable evidence."""

def _json_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise BridgeError("INVALID_JSON")
        result[key] = value
    return result

def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=_json_object)

def _absolute(value):
    path = Path(value)
    if not path.is_absolute():
        raise BridgeError("INVALID_PATH")
    for entry in (path, *path.parents):
        try:
            attributes = entry.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode) or getattr(attributes, "st_file_attributes", 0) & 0x400:
            raise BridgeError("INVALID_PATH")
    return path.resolve()

def _inside(root, path):
    if not path.is_relative_to(root):
        raise BridgeError("PATH_OUTSIDE_OWNED_ROOT")

def validate_request(request):
    if not _REQUEST.is_valid(request):
        raise BridgeError("INVALID_REQUEST")
    root = _absolute(request["paths"]["root"])
    for value in request["paths"].values():
        _inside(root, _absolute(value))
    workspace = _absolute(request["paths"]["workspace"])
    home = _absolute(request["paths"]["home"])
    if workspace.is_relative_to(home) or home.is_relative_to(workspace):
        raise BridgeError("INVALID_PATH")
    for name in ("snapshotPath", "priorSnapshotPath", "stagingPath"):
        if request.get(name) is not None:
            _inside(root, _absolute(request[name]))
    images = ([request.get("questionImage")] if request["operation"] == "retrieve" else
              [s["screenshot"] for s in request.get("trajectory", {}).get("states", [])])
    for image in images:
        if image is not None:
            _inside(root, _absolute(image["path"]))

def validate_result(result, request):
    if not _RESULT.is_valid(result):
        raise BridgeError("INVALID_RESULT")
    if result["operationId"] != request["operationId"]:
        raise BridgeError("RESULT_ID_MISMATCH")
    if result["eventLogPath"] != request["paths"]["eventLogPath"]:
        raise BridgeError("RESULT_PATH_MISMATCH")
    if result["status"] != "ok":
        raise BridgeError(result["errorCode"] or "WORKER_FAILED")
    if result["errorCode"] is not None:
        raise BridgeError("INVALID_RESULT")
    if request["operation"] == "learn":
        receipt = result["receipt"]
        if receipt is None or result["evidence"] is not None:
            raise BridgeError("INVALID_RECEIPT")
        if receipt["committedOperationId"] != request["operationId"]:
            raise BridgeError("INVALID_RECEIPT")
        if receipt["sourceHash"] != request["trajectory"]["sourceHash"]:
            raise BridgeError("INVALID_RECEIPT")
    elif request["operation"] == "retrieve":
        if result["evidence"] is None or result["receipt"] is not None:
            raise BridgeError("INVALID_EVIDENCE")
        for item in result["evidence"]:
            if item["type"] == "image":
                _inside(_absolute(request["paths"]["root"]), _absolute(item["value"]))
    elif result["receipt"] is not None or result["evidence"] is not None:
        raise BridgeError("INVALID_RESULT")

class _OwnedTree:
    """Use a job object on Windows and a process group on POSIX."""
    def __init__(self, process):
        self.process = process
        self.handle = None
        self.closed = False
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes
            class Basic(ctypes.Structure):
                _fields_ = [("perProcess", ctypes.c_longlong), ("perJob", ctypes.c_longlong),
                            ("flags", wintypes.DWORD), ("minimum", ctypes.c_size_t),
                            ("maximum", ctypes.c_size_t), ("active", wintypes.DWORD),
                            ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD),
                            ("scheduling", wintypes.DWORD)]
            class Io(ctypes.Structure):
                _fields_ = [(name, ctypes.c_ulonglong) for name in
                            ("readOps", "writeOps", "otherOps", "read", "write", "other")]
            class Extended(ctypes.Structure):
                _fields_ = [("basic", Basic), ("io", Io), ("processMemory", ctypes.c_size_t),
                            ("jobMemory", ctypes.c_size_t), ("peakProcess", ctypes.c_size_t),
                            ("peakJob", ctypes.c_size_t)]
            self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            self.kernel.CreateJobObjectW.restype = wintypes.HANDLE
            self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
            self.kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                                          ctypes.c_void_p, wintypes.DWORD]
            self.kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
            self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            self.kernel.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
            self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            self.kernel.OpenProcess.restype = wintypes.HANDLE
            self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            self.kernel.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int,
                                                              ctypes.c_void_p, wintypes.DWORD,
                                                              ctypes.c_void_p]
            self.handle = self.kernel.CreateJobObjectW(None, None)
            limits = Extended()
            limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (not self.handle or not self.kernel.SetInformationJobObject(
                    self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits))
                    or not self.kernel.AssignProcessToJobObject(self.handle, int(process._handle))):
                self.close()
                raise BridgeError("OWNERSHIP_FAILED")
    def close(self):
        if self.closed:
            return
        self.closed = True
        if self.handle:
            import ctypes
            from ctypes import wintypes
            class Accounting(ctypes.Structure):
                _fields_ = [(name, ctypes.c_longlong) for name in
                            ("user", "kernel", "periodUser", "periodKernel")] + [
                            (name, wintypes.DWORD) for name in
                            ("faults", "totalProcesses", "activeProcesses", "terminated")]
            class ProcessList(ctypes.Structure):
                _fields_ = [("assigned", wintypes.DWORD), ("count", wintypes.DWORD),
                            ("ids", ctypes.c_size_t * 4096)]
            processes = ProcessList()
            handles = []
            try:
                if not self.kernel.QueryInformationJobObject(self.handle, 3,
                        ctypes.byref(processes), ctypes.sizeof(processes), None):
                    raise BridgeError("OWNED_TREE_CLEANUP_FAILED")
                for pid in processes.ids[:processes.count]:
                    handle = self.kernel.OpenProcess(0x100000, False, pid)
                    if handle:
                        handles.append(handle)
                if not self.kernel.TerminateJobObject(self.handle, 1):
                    raise BridgeError("OWNED_TREE_CLEANUP_FAILED")
                deadline = time.monotonic() + 5
                while True:
                    accounting = Accounting()
                    if not self.kernel.QueryInformationJobObject(self.handle, 1,
                            ctypes.byref(accounting), ctypes.sizeof(accounting), None):
                        raise BridgeError("OWNED_TREE_CLEANUP_FAILED")
                    if accounting.activeProcesses == 0:
                        break
                    if time.monotonic() >= deadline:
                        raise BridgeError("OWNED_TREE_CLEANUP_FAILED")
                    time.sleep(0.01)
                for handle in handles:
                    if self.kernel.WaitForSingleObject(handle, 5000) != 0:
                        raise BridgeError("OWNED_TREE_CLEANUP_FAILED")
            finally:
                for handle in handles:
                    self.kernel.CloseHandle(handle)
                self.kernel.CloseHandle(self.handle)
                self.handle = None
        elif os.name != "nt":
            try:
                os.killpg(self.process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass

def _close_control_pipe(process):
    if process.stdin is not None and not process.stdin.closed:
        try:
            process.stdin.close()
        except OSError:
            # Closing buffered stdin can flush again after an earlier broken-pipe error.
            pass

class WorkerBridge:
    def __init__(self, command, operation_root, *, cleanup_seconds=30):
        if not command or cleanup_seconds <= 0 or cleanup_seconds > 30:
            raise ValueError("Invalid worker command or cleanup grace.")
        self.command = list(command)
        self.operation_root = _absolute(str(Path(operation_root).absolute()))
        self.cleanup_seconds = cleanup_seconds

    def run(self, operation: str, request: dict, cancel_event) -> dict:
        validate_request(request)
        if operation != request["operation"]:
            raise BridgeError("OPERATION_MISMATCH")
        _inside(_absolute(request["paths"]["root"]), self.operation_root)
        if cancel_event.is_set():
            raise BridgeError("CANCELLED")
        directory = self.operation_root / uuid.uuid4().hex
        directory.mkdir(parents=True)
        request_path = directory / "request.json"
        result_path = directory / "result.json"
        request_path.write_text(json.dumps(request, allow_nan=False), encoding="utf-8")
        process = None
        owned = None
        try:
            process = subprocess.Popen(
                [*self.command, "--request", str(request_path), "--result", str(result_path)],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                text=True, encoding="utf-8", shell=False, start_new_session=os.name != "nt",
                creationflags=0x4 if os.name == "nt" else 0)  # CREATE_SUSPENDED
            owned = _OwnedTree(process)
            if os.name == "nt":
                import ctypes
                from ctypes import wintypes
                resume = ctypes.WinDLL("ntdll").NtResumeProcess
                resume.argtypes = [wintypes.HANDLE]
                resume.restype = ctypes.c_long
                if resume(int(process._handle)) != 0:
                    raise BridgeError("WORKER_RESUME_FAILED")
            cancelled = False
            deadline = None
            while process.poll() is None:
                if cancel_event.is_set() and not cancelled:
                    cancelled = True
                    deadline = time.monotonic() + self.cleanup_seconds
                    try:
                        process.stdin.write(json.dumps({"operation": "cancel"}) + "\n")
                        process.stdin.flush()
                    except (BrokenPipeError, OSError):
                        pass
                    _close_control_pipe(process)
                if deadline is not None and time.monotonic() >= deadline:
                    owned.close()
                    process.kill()
                    break
                time.sleep(0.02)
            process.wait()
            if cancelled or cancel_event.is_set():
                raise BridgeError("CANCELLED")
            if process.returncode != 0:
                raise BridgeError("WORKER_EXIT_FAILED")
            _absolute(str(result_path))
            if not result_path.is_file() or result_path.stat().st_size > 16 * 1024 * 1024:
                raise BridgeError("MISSING_OR_OVERSIZE_RESULT")
            try:
                result = read_json(result_path)
            except (ValueError, OSError) as exc:
                raise BridgeError("INVALID_RESULT_JSON") from exc
            validate_result(result, request)
            return result
        except BaseException as exc:
            code = str(exc) if isinstance(exc, BridgeError) else "BRIDGE_FAILED"
            (directory / "failure.json").write_text(json.dumps({"code": code}), encoding="utf-8")
            raise
        finally:
            if process is not None:
                try:
                    _close_control_pipe(process)
                finally:
                    try:
                        if owned is not None:
                            owned.close()
                    finally:
                        try:
                            if process.poll() is None:
                                process.kill()
                        finally:
                            process.wait()
