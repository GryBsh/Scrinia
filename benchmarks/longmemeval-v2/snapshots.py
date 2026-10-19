"""Immutable generations. Only a validated receipt can replace current.json."""
from contextlib import contextmanager
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid
from bridge import BridgeError, _absolute, read_json

def file_entries(root):
    root = _absolute(str(root))
    entries = []
    for path in sorted(root.rglob("*"), key=lambda p:p.relative_to(root).as_posix()):
        _absolute(str(path))
        if path.is_file() and path.relative_to(root).as_posix() != ".generation.json":
            relative = path.relative_to(root).as_posix()
            if "\n" in relative or "\r" in relative: raise BridgeError("INVALID_PATH")
            entries.append((relative, hashlib.sha256(path.read_bytes()).hexdigest()))
        elif not path.is_dir() and not path.is_file():
            raise BridgeError("INVALID_PATH")
    return entries

def fingerprint(root):
    value = "".join(name + "\0" + value + "\n" for name,value in file_entries(root))
    return hashlib.sha256(value.encode("utf-8")).hexdigest()

def atomic_json(path, value):
    path = _absolute(str(path)); path.parent.mkdir(parents=True,exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("x",encoding="utf-8",newline="\n") as stream:
            json.dump(value, stream, sort_keys=True, ensure_ascii=True, separators=(",",":"))
            stream.flush(); os.fsync(stream.fileno())
        os.replace(temp,path)
    finally:
        if temp.exists(): temp.unlink()

class SnapshotStore:
    def __init__(self,root,request_factory,*,fault=None):
        self.root = _absolute(str(root)); self.root.mkdir(parents=True,exist_ok=True)
        self.request_factory = request_factory
        self.fault = fault or (lambda point:None)
    @contextmanager
    def _lock(self):
        path=self.root/"snapshot.lock"
        _absolute(str(path))
        with path.open("a+b") as stream:
            stream.seek(0); stream.write(b"\0"); stream.flush(); stream.seek(0)
            if os.name=="nt":
                import msvcrt
                msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
            try: yield
            finally:
                stream.seek(0)
                if os.name=="nt": msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,1)
                else: fcntl.flock(stream.fileno(),fcntl.LOCK_UN)
    def current(self):
        pointer=self.root/"current.json"; _absolute(str(pointer))
        if not pointer.exists(): return None
        value=read_json(pointer)
        if set(value)!={"generation","fingerprint","metadataHash"} or not isinstance(value["generation"],str) or len(value["generation"])!=32 or any(c not in "0123456789abcdef" for c in value["generation"]):
            raise BridgeError("INVALID_SNAPSHOT")
        generation=self.root/"generations"/value["generation"]
        _absolute(str(generation))
        metadata_path=generation/".generation.json"
        _absolute(str(metadata_path))
        if hashlib.sha256(metadata_path.read_bytes()).hexdigest()!=value["metadataHash"]:
            raise BridgeError("INVALID_SNAPSHOT")
        meta=read_json(metadata_path)
        if (meta["fingerprint"]!=value["fingerprint"] or fingerprint(generation)!=value["fingerprint"]
            or meta["receipt"]["newSnapshotHash"]!=value["fingerprint"]):
            raise BridgeError("INVALID_SNAPSHOT")
        return meta
    @property
    def generation_path(self):
        value=read_json(self.root/"current.json")
        self.current()
        return self.root/"generations"/value["generation"]
    def insert(self,trajectory_manifest,configuration_hash,bridge,cancel_event):
        with self._lock():
            return self._insert(trajectory_manifest,configuration_hash,bridge,cancel_event)
    def _insert(self,trajectory_manifest,configuration_hash,bridge,cancel):
        if cancel.is_set(): raise BridgeError("CANCELLED")
        previous=self.current()
        ident=trajectory_manifest["id"]; source_hash=trajectory_manifest["sourceHash"]
        ledger={} if previous is None else copy.deepcopy(previous["ledger"])
        if previous is not None and previous["configurationHash"]!=configuration_hash:
            raise BridgeError("CONFIGURATION_CONFLICT")
        if ident in ledger:
            entry=ledger[ident]
            if entry["sourceHash"]!=source_hash: raise BridgeError("INSERTION_CONFLICT")
            return entry["receipt"]
        uncertain=self.root/"uncertain"/(hashlib.sha256(ident.encode()).hexdigest()+".json")
        _absolute(str(uncertain))
        if uncertain.exists(): raise BridgeError("INSERTION_UNCERTAIN")
        generation=uuid.uuid4().hex
        stage=self.root/"generations"/generation
        _absolute(str(stage)); stage.parent.mkdir(parents=True,exist_ok=True)
        if previous is None:
            (stage/"workspace").mkdir(parents=True); (stage/"home").mkdir()
        else:
            # Verify before copying; no link-following is accepted.
            file_entries(self.generation_path)
            shutil.copytree(self.generation_path,stage)
            (stage/".generation.json").unlink()
        value=copy.deepcopy(trajectory_manifest)
        fragments=value.pop("fragments"); batches=value.pop("batches")
        for state in value["states"]:
            image=state["screenshot"]; source=_absolute(image["path"])
            suffix={"image/png":".png","image/jpeg":".jpg","image/webp":".webp"}[image["mediaType"]]
            target=stage/"sources"/(image["sha256"]+suffix)
            target.parent.mkdir(parents=True,exist_ok=True)
            if hashlib.sha256(source.read_bytes()).hexdigest()!=image["sha256"]: raise BridgeError("SOURCE_CHANGED")
            if not target.exists(): shutil.copyfile(source,target)
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
            meta={"version":1,"configurationHash":configuration_hash,"ledger":ledger,"receipt":receipt,"fingerprint":actual}
            self.fault("persist-result")
            atomic_json(stage/".generation.json",meta)
            self.fault("before-publication")
            if cancel.is_set(): raise BridgeError("CANCELLED")
            atomic_json(self.root/"current.json",{"generation":generation,"fingerprint":actual,
                "metadataHash":hashlib.sha256((stage/".generation.json").read_bytes()).hexdigest()})
            self.fault("after-publication")
            uncertain.unlink()
            return receipt
        except BaseException:
            # Preserve intent and staged state for diagnosis. current.json remains authoritative.
            raise
