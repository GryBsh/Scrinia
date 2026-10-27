"""Immutable generations. Only a validated receipt can replace current.json."""
from contextlib import contextmanager, ExitStack
import copy
import hashlib
import json
import os
from filesystem import Path, io_path, canonical_path, canonical_paths
import shutil
import uuid
from bridge import BridgeError, _absolute, read_json

def file_entries(root):
    root = _absolute(str(root))
    entries = []
    for path in sorted(canonical_paths(io_path(root).rglob("*")), key=lambda p:p.relative_to(root).as_posix()):
        _absolute(str(path))
        if io_path(path).is_file() and path.relative_to(root).as_posix() != ".generation.json":
            relative = path.relative_to(root).as_posix()
            if "\n" in relative or "\r" in relative: raise BridgeError("INVALID_PATH")
            entries.append((relative, hashlib.sha256(io_path(path).read_bytes()).hexdigest()))
        elif not io_path(path).is_dir() and not io_path(path).is_file():
            raise BridgeError("INVALID_PATH")
    return entries

def fingerprint(root):
    value = "".join(name + "\0" + value + "\n" for name,value in file_entries(root))
    return hashlib.sha256(value.encode("utf-8")).hexdigest()

def atomic_json(path, value):
    path = _absolute(str(path)); io_path(path.parent).mkdir(parents=True,exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with io_path(temp).open("x",encoding="utf-8",newline="\n") as stream:
            json.dump(value, stream, sort_keys=True, ensure_ascii=True, separators=(",",":"))
            stream.flush(); os.fsync(stream.fileno())
        os.replace(io_path(temp),io_path(path))
    finally:
        if io_path(temp).exists(): io_path(temp).unlink()


def _sync_file(path):
    # Windows FlushFileBuffers (used by fsync) requires a writable handle.
    with io_path(_absolute(str(path))).open("r+b") as stream:
        os.fsync(stream.fileno())


def _sync_directory(path):
    """Use Core DurableFile's directory handles; unsupported flushes fail closed."""
    path = _absolute(str(path))
    if os.name != "nt":
        descriptor = os.open(io_path(path), os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        return
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.FlushFileBuffers.argtypes = [wintypes.HANDLE]
    kernel.FlushFileBuffers.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    # GENERIC_WRITE, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS. Match Core's
    # full share set, falling back when FILE_SHARE_EXECUTE is unsupported.
    for share in (0x17, 0x7):
        handle = kernel.CreateFileW(str(io_path(path)), 0x40000000, share, None, 3, 0x02000000, None)
        if handle != ctypes.c_void_p(-1).value:
            try:
                if not kernel.FlushFileBuffers(handle):
                    raise ctypes.WinError(ctypes.get_last_error())
            finally:
                kernel.CloseHandle(handle)
            return
        error = ctypes.get_last_error()
        if error != 87 or share == 0x7:
            raise ctypes.WinError(error)

def validate_history(history):
    if (not isinstance(history, list) or not history
        or any(not isinstance(item, dict) or set(item) != {"id", "sourceHash"}
               or not isinstance(item["id"], str) or not item["id"]
               or not isinstance(item["sourceHash"], str) or len(item["sourceHash"]) != 64
               or any(c not in "0123456789abcdef" for c in item["sourceHash"]) for item in history)
        or len({item["id"] for item in history}) != len(history)):
        raise BridgeError("HISTORY_IDENTITY_REQUIRED")
    return history

def validate_metadata_history(meta):
    if meta.get("version") != 2:
        raise BridgeError("HISTORY_IDENTITY_REQUIRED")
    history = validate_history(meta.get("history"))
    ledger = meta.get("ledger")
    if (not isinstance(ledger, dict) or set(ledger) != {item["id"] for item in history}
        or any(ledger[item["id"]].get("sourceHash") != item["sourceHash"]
               or ledger[item["id"]].get("receipt", {}).get("sourceHash") != item["sourceHash"]
               for item in history)
        or ledger[history[-1]["id"]].get("receipt") != meta.get("receipt")):
        raise BridgeError("HISTORY_IDENTITY_MISMATCH")
    return history


@contextmanager
def _file_lock(path, *, shared=False):
    """Lease files stay outside immutable payloads and are never unlinked."""
    path = _absolute(str(path))
    io_path(path.parent).mkdir(parents=True, exist_ok=True)
    with io_path(path).open("a+b") as stream:
        if stream.seek(0, os.SEEK_END) == 0:
            stream.write(b"\0"); stream.flush()
        stream.seek(0)
        if os.name == "nt":
            # CRT LK_NBRLCK is exclusive on Windows; use the native shared lock
            # so simultaneous queries can pin the same immutable generation.
            import ctypes
            from ctypes import wintypes
            import msvcrt
            class Overlapped(ctypes.Structure):
                _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                            ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD),
                            ("hEvent", wintypes.HANDLE)]
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.LockFileEx.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD,
                                         wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(Overlapped)]
            kernel.LockFileEx.restype = wintypes.BOOL
            kernel.UnlockFileEx.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD,
                                           wintypes.DWORD, ctypes.POINTER(Overlapped)]
            kernel.UnlockFileEx.restype = wintypes.BOOL
            handle = msvcrt.get_osfhandle(stream.fileno())
            offset = Overlapped()
            # LOCKFILE_FAIL_IMMEDIATELY, plus LOCKFILE_EXCLUSIVE_LOCK for writers.
            if not kernel.LockFileEx(handle, 1 if shared else 3, 0, 1, 0, ctypes.byref(offset)):
                raise ctypes.WinError(ctypes.get_last_error())
        else:
            import fcntl
            fcntl.flock(stream.fileno(), (fcntl.LOCK_SH if shared else fcntl.LOCK_EX) | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                if not kernel.UnlockFileEx(handle, 0, 1, 0, ctypes.byref(offset)):
                    raise ctypes.WinError(ctypes.get_last_error())
            else: fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def _generation_id(value):
    return isinstance(value, str) and len(value) == 32 and all(c in "0123456789abcdef" for c in value)

class SnapshotStore:
    def __init__(self,root,request_factory,*,fault=None):
        self.root = _absolute(str(root)); io_path(self.root).mkdir(parents=True,exist_ok=True)
        self.request_factory = request_factory
        self.fault = fault or (lambda point:None)
    def _lock(self, *, shared=False):
        return _file_lock(self.root / "snapshot.lock", shared=shared)

    def _reader_lock(self, source, *, shared=False):
        name = hashlib.sha256(str(source).encode("utf-8")).hexdigest() + ".lock"
        return _file_lock(self.root / "readers" / name, shared=shared)

    @contextmanager
    def read_lease(self, source):
        """Choose and pin a source before allowing publication/retirement to resume."""
        source = _absolute(str(source))
        with ExitStack() as leases:
            with self._lock(shared=True):
                meta = self.current()
                if meta is not None and source != self.generation_path:
                    raise BridgeError("SNAPSHOT_POINTER_MISMATCH")
                leases.enter_context(self._reader_lock(source, shared=True))
            yield meta

    def current(self):
        pointer=self.root/"current.json"; _absolute(str(pointer))
        if not io_path(pointer).exists(): return None
        return self._validate_generation(read_json(pointer))

    def _validate_generation(self, value):
        if set(value)!={"generation","fingerprint","metadataHash"} or not _generation_id(value["generation"]):
            raise BridgeError("INVALID_SNAPSHOT")
        generation=self.root/"generations"/value["generation"]
        _absolute(str(generation))
        metadata_path=generation/".generation.json"
        _absolute(str(metadata_path))
        if hashlib.sha256(io_path(metadata_path).read_bytes()).hexdigest()!=value["metadataHash"]:
            raise BridgeError("INVALID_SNAPSHOT")
        meta=read_json(metadata_path)
        if (meta["fingerprint"]!=value["fingerprint"] or fingerprint(generation)!=value["fingerprint"]
            or meta["receipt"]["newSnapshotHash"]!=value["fingerprint"]):
            raise BridgeError("INVALID_SNAPSHOT")
        validate_metadata_history(meta)
        if meta.get("previousGeneration") is not None and not _generation_id(meta["previousGeneration"]):
            raise BridgeError("INVALID_SNAPSHOT")
        return meta

    def _retire_generations(self):
        """Best-effort cleanup under _lock; retain ambiguous or failed evidence.

        Keep current and its immediate predecessor. A failed/uncertain operation
        suppresses retirement altogether. Unticketed generations are never inferred
        to be committed. Permanent small lease files avoid split-inode lock races.
        """
        try:
            if any(io_path(self.root / "uncertain").glob("*.json")):
                return
            pointer = read_json(self.root / "current.json")
            meta = self._validate_generation(pointer)
            protected = {pointer["generation"], meta.get("previousGeneration")}
            tickets = list(canonical_paths(io_path(self.root / "retired").glob("*.json")))
            self._flush_publication(pointer)
        except (BridgeError, OSError, ValueError, TypeError, KeyError, AttributeError):
            return
        for ticket in tickets:
            try:
                _absolute(str(ticket))
                value = read_json(ticket)
                if (set(value) != {"generation", "fingerprint", "metadataHash"}
                    or not _generation_id(value["generation"])
                    or ticket.name != value["generation"] + ".json"
                    or value["generation"] in protected):
                    continue
                source = _absolute(str(self.root / "generations" / value["generation"]))
                with self._reader_lock(source):
                    if io_path(source).exists():
                        self._validate_generation(value)
                        shutil.rmtree(io_path(source))
                    io_path(ticket).unlink()
            except (BridgeError, OSError, ValueError, TypeError, KeyError, AttributeError):
                # Reader leases, corrupt candidates and partial cleanup retain evidence.
                # A committed receipt must not become a failed insertion due to cleanup.
                continue

    def _flush_publication(self, pointer):
        """Make the selected payload and pointer durable before deleting any backup.

        Called under snapshot admission after validation. Flushing enumerates paths,
        without rehashing payloads. Failure leaves all retirement tickets and data.
        """
        generation = _absolute(str(self.root / "generations" / pointer["generation"]))
        directories = [generation]
        for path in canonical_paths(io_path(generation).rglob("*")):
            path = _absolute(str(path))
            if io_path(path).is_file():
                self.fault("retirement-flush-file")
                _sync_file(path)
            elif io_path(path).is_dir():
                directories.append(path)
            else:
                raise BridgeError("INVALID_PATH")
        for path in sorted(directories, key=lambda item: len(item.parts), reverse=True):
            self.fault("retirement-flush-directory")
            _sync_directory(path)
        self.fault("retirement-flush-generations")
        _sync_directory(generation.parent)
        self.fault("retirement-flush-pointer")
        _sync_file(self.root / "current.json")
        self.fault("retirement-flush-root")
        _sync_directory(self.root)
        self.fault("retirement-flush-ancestor")
        _sync_directory(self.root.parent)
    @property
    def generation_path(self):
        value=read_json(self.root/"current.json")
        self.current()
        return self.root/"generations"/value["generation"]
    def reconcile_committed_markers(self, meta):
        """Caller holds _lock. Retire only intent exactly proven by current committed state."""
        unresolved = []
        pointer = None if meta is None else read_json(self.root / "current.json")
        for marker in canonical_paths(io_path(self.root / "uncertain").glob("*.json")):
            _absolute(str(marker))
            try:
                value = read_json(marker)
                ident = value.get("trajectoryId")
                entry = None if meta is None else meta["ledger"].get(ident)
                proven = (set(value) == {"trajectoryId", "sourceHash", "generation", "operationId"}
                    and isinstance(ident, str)
                    and marker.name == hashlib.sha256(ident.encode()).hexdigest() + ".json"
                    and entry is not None and entry["sourceHash"] == value["sourceHash"]
                    and entry["receipt"] == meta["receipt"]
                    and entry["receipt"]["committedOperationId"] == value["operationId"]
                    and entry["receipt"]["newSnapshotHash"] == pointer["fingerprint"]
                    and value["generation"] == pointer["generation"])
            except (ValueError, TypeError, KeyError):
                proven = False
            if proven:
                io_path(marker).unlink()
            else:
                unresolved.append(marker)
        return unresolved

    def insert(self,trajectory_manifest,configuration_hash,bridge,cancel_event):
        with self._lock():
            return self._insert(trajectory_manifest,configuration_hash,bridge,cancel_event)
    def _insert(self,trajectory_manifest,configuration_hash,bridge,cancel):
        if cancel.is_set(): raise BridgeError("CANCELLED")
        previous=self.current()
        previous_pointer = None if previous is None else read_json(self.root / "current.json")
        unresolved = self.reconcile_committed_markers(previous)
        ident=trajectory_manifest["id"]; source_hash=trajectory_manifest["sourceHash"]
        uncertain=self.root/"uncertain"/(hashlib.sha256(ident.encode()).hexdigest()+".json")
        if uncertain in unresolved: raise BridgeError("INSERTION_UNCERTAIN")
        ledger={} if previous is None else copy.deepcopy(previous["ledger"])
        if previous is not None and previous["configurationHash"]!=configuration_hash:
            raise BridgeError("CONFIGURATION_CONFLICT")
        if ident in ledger:
            entry=ledger[ident]
            if entry["sourceHash"]!=source_hash: raise BridgeError("INSERTION_CONFLICT")
            self._retire_generations()
            return entry["receipt"]
        uncertain=self.root/"uncertain"/(hashlib.sha256(ident.encode()).hexdigest()+".json")
        _absolute(str(uncertain))
        if io_path(uncertain).exists(): raise BridgeError("INSERTION_UNCERTAIN")
        generation=uuid.uuid4().hex
        stage=self.root/"generations"/generation
        _absolute(str(stage)); io_path(stage.parent).mkdir(parents=True,exist_ok=True)
        if previous is None:
            (io_path(stage/"workspace")).mkdir(parents=True); (io_path(stage/"home")).mkdir()
        else:
            # Verify before copying; no link-following is accepted.
            file_entries(self.generation_path)
            shutil.copytree(io_path(self.generation_path),io_path(stage))
            (io_path(stage/".generation.json")).unlink()
        value=copy.deepcopy(trajectory_manifest)
        fragments=value.pop("fragments"); batches=value.pop("batches")
        for state in value["states"]:
            image=state["screenshot"]; source=_absolute(image["path"])
            suffix={"image/png":".png","image/jpeg":".jpg","image/webp":".webp"}[image["mediaType"]]
            target=stage/"sources"/(image["sha256"]+suffix)
            io_path(target.parent).mkdir(parents=True,exist_ok=True)
            if hashlib.sha256(io_path(source).read_bytes()).hexdigest()!=image["sha256"]: raise BridgeError("SOURCE_CHANGED")
            if not io_path(target).exists(): shutil.copyfile(io_path(source),io_path(target))
            image["path"]=str(target)
        # Batches refer to owned copies, never caller paths.
        images={s["screenshot"]["sha256"]:s["screenshot"] for s in value["states"]}
        for batch in batches: batch["images"]=[images[i["sha256"]] for i in batch["images"]]
        request=self.request_factory("learn",trajectory=value,priorSnapshotPath=None if previous is None else str(self.generation_path),
            stagingPath=str(stage),previousFingerprint=None if previous is None else previous["fingerprint"],
            fragments=fragments,batches=batches)
        if "paths" in request: request["paths"]={**request["paths"],"workspace":str(stage/"workspace"),"home":str(stage/"home")}
        # Durable intent precedes worker dispatch. An uncertain operation is never replayed.
        atomic_json(uncertain,{"trajectoryId":ident,"sourceHash":source_hash,"generation":generation,"operationId":request["operationId"]})
        try:
            result=bridge.run("learn",request,cancel)
            receipt=result["receipt"]
            if cancel.is_set(): raise BridgeError("CANCELLED")
            actual=fingerprint(stage)
            if (receipt["sourceHash"]!=source_hash or receipt["newSnapshotHash"]!=actual
                or receipt["previousSnapshotHash"]!=(None if previous is None else previous["fingerprint"])
                or receipt["committedOperationId"]!=request["operationId"]):
                raise BridgeError("INVALID_RECEIPT")
            validation=self.request_factory("validate-snapshot",snapshotPath=str(stage),expectedFingerprint=actual)
            bridge.run("validate-snapshot",validation,cancel)
            ledger[ident]={"sourceHash":source_hash,"receipt":receipt}
            history = [] if previous is None else copy.deepcopy(previous["history"])
            history.append({"id": ident, "sourceHash": source_hash})
            meta={"version":2,"configurationHash":configuration_hash,"history":history,
                  "ledger":ledger,"receipt":receipt,"fingerprint":actual}
            meta["previousGeneration"] = None if previous_pointer is None else previous_pointer["generation"]
            self.fault("persist-result")
            atomic_json(stage/".generation.json",meta)
            if previous_pointer is not None:
                # Ticket binds the exact validated prior pointer; never nominate an
                # orphan merely because its metadata looks like a committed snapshot.
                atomic_json(self.root / "retired" / (previous_pointer["generation"] + ".json"), previous_pointer)
            self.fault("before-publication")
            if cancel.is_set(): raise BridgeError("CANCELLED")
            atomic_json(self.root/"current.json",{"generation":generation,"fingerprint":actual,
                "metadataHash":hashlib.sha256((io_path(stage/".generation.json")).read_bytes()).hexdigest()})
            self.fault("after-publication")
            io_path(uncertain).unlink()
            self._retire_generations()
            return receipt
        except BaseException:
            # Preserve intent and staged state for diagnosis. current.json remains authoritative.
            raise
