import copy
import hashlib
import json
import sys
import shutil
import threading
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).parent))
import test_official_harness as harness_fixture
from test_snapshots import FixtureBridge
from snapshots import SnapshotStore, atomic_json
from bridge import BridgeError
from run_scrinia import expected_history
from portability import identity_hash, method_identity
from scrinia_memory import ScriniaMemory
from memory_modules.memory import load_memory
from filesystem import io_path

class HistoryRecoveryTests(harness_fixture.OfficialHarnessTests):
    # Inherit fixture helpers only, not unrelated integration test methods.
    def test_loaded_memory_requires_external_expected_history(self):
        memory=self.committed_memory()
        saved=self.root/"saved";memory.save_memory(saved)
        with patch("bridge.WorkerBridge.run",return_value={}) as run:
            with self.assertRaisesRegex((ValueError,BridgeError,RuntimeError),"HISTORY"):
                load_memory(saved)
            self.assertEqual(run.call_count,0)

    def test_loaded_history_rejects_permuted_missing_extra_and_changed_content(self):
        memory=self.committed_memory()
        saved=self.root/"saved";memory.save_memory(saved)
        expected=[{"id":"invented","sourceHash":"b"*64}]
        cases=[[],expected+[{"id":"extra","sourceHash":"c"*64}],
               [{"id":"invented","sourceHash":"c"*64}]]
        for history in cases:
            params=self.params("runtime-"+str(len(list(self.root.iterdir()))))
            params["expected_history"]=history
            with patch("bridge.WorkerBridge.run",return_value={}) as run:
                with self.assertRaisesRegex((ValueError,BridgeError,RuntimeError),"HISTORY"):
                    load_memory(saved,{"memory_type":"scrinia","memory_params":params})
                self.assertEqual(run.call_count,0)

    def test_ack_loss_recovery_can_save_and_reload_without_relearning(self):
        memory=ScriniaMemory(self.params())
        identity=method_identity(memory.memory_params)
        memory.memory_params["method_identity"]=identity
        bridge=FixtureBridge()
        memory.snapshots.request_factory=lambda operation,**fields:{"operationId":"1"*32,**fields}
        memory.snapshots.fault=lambda point:(_ for _ in ()).throw(OSError("lost ack")) if point=="after-publication" else None
        trajectory={"id":"invented","sourceHash":"b"*64,"states":[],"fragments":[],"batches":[]}
        with self.assertRaises(OSError):
            memory.snapshots.insert(trajectory,identity_hash(identity),bridge,memory.cancel_event)
        reopened=SnapshotStore(memory.snapshots.root,memory.snapshots.request_factory)
        calls=list(bridge.calls)
        reopened.insert(trajectory,identity_hash(identity),bridge,memory.cancel_event)
        self.assertEqual(calls,bridge.calls)
        self.assertEqual(list(io_path(reopened.root/"uncertain").glob("*.json")),[])
        memory.snapshots=reopened;memory.snapshot_path=str(reopened.generation_path)
        memory.bridge.run=lambda *args:{}
        saved=self.root/"recovered";memory.save_memory(saved)
        params=self.params("relocated")
        params["expected_history"]=[{"id":"invented","sourceHash":"b"*64}]
        with patch("bridge.WorkerBridge.run",return_value={}):
            loaded=load_memory(saved,{"memory_type":"scrinia","memory_params":params})
        self.assertEqual(loaded.snapshots.current(),memory.snapshots.current())

    def test_forged_committed_marker_is_retained_and_blocks_idempotence(self):
        store=SnapshotStore(self.root/"snapshots",lambda operation,**fields:{"operationId":"1"*32,**fields})
        trajectory={"id":"invented","sourceHash":"b"*64,"states":[],"fragments":[],"batches":[]}
        bridge=FixtureBridge();store.insert(trajectory,"a"*64,bridge,threading.Event())
        marker=store.root/"uncertain"/(hashlib.sha256(b"invented").hexdigest()+".json")
        atomic_json(marker,{"trajectoryId":"invented","sourceHash":"b"*64,
            "generation":store.generation_path.name,"operationId":"2"*32})
        with self.assertRaisesRegex(BridgeError,"UNCERTAIN"):
            store.insert(trajectory,"a"*64,bridge,threading.Event())
        self.assertTrue(io_path(marker).is_file())

    def test_history_is_explicit_order_not_sorted_ledger_order(self):
        store=SnapshotStore(self.root/"snapshots",lambda operation,**fields:{"operationId":"1"*32,**fields})
        bridge=FixtureBridge()
        for ident,source in (("z","b"),("a","c")):
            store.insert({"id":ident,"sourceHash":source*64,"states":[],"fragments":[],"batches":[]},"a"*64,bridge,threading.Event())
        self.assertEqual(store.current().get("history"),[{"id":"z","sourceHash":"b"*64},{"id":"a","sourceHash":"c"*64}])

    def test_actual_history_relocation_permutation_text_and_image_changes(self):
        source=self.root/"source"
        shutil.copytree(harness_fixture.ADAPTER/"tests/fixtures/synthetic_web",source)
        trajectories=source/"trajectories.jsonl"
        haystack=self.root/"haystack.json"
        ids=["web-invented-2","web-invented-1"]
        haystack.write_text(json.dumps({"question":ids}))
        expected=expected_history(haystack,trajectories,source)
        memory=ScriniaMemory(self.params())
        identity=method_identity(memory.memory_params)
        memory.memory_params["method_identity"]=identity
        from trajectory_input import normalize_trajectory
        originals={v["id"]:v for v in map(json.loads,trajectories.read_text().splitlines())}
        bridge=FixtureBridge()
        memory.snapshots.request_factory=lambda operation,**fields:{"operationId":"1"*32,**fields}
        for ident in ids:
            normalized=normalize_trajectory(originals[ident],source)
            memory.snapshots.insert({**normalized,"fragments":[],"batches":[]},identity_hash(identity),bridge,memory.cancel_event)
        memory.snapshot_path=str(memory.snapshots.generation_path);memory.bridge.run=lambda *args:{}
        saved=self.root/"saved-history";memory.save_memory(saved)
        relocated=self.root/"relocated-sources";shutil.copytree(source,relocated)
        self.assertEqual(expected_history(haystack,relocated/"trajectories.jsonl",relocated),expected)
        params=self.params("moved-runtime");params["expected_history"]=expected
        with patch("bridge.WorkerBridge.run",return_value={}):
            loaded=load_memory(saved,{"memory_type":"scrinia","memory_params":params})
        self.assertEqual(loaded.snapshots.current()["history"],expected)
        for ordered in (ids[::-1],ids[:1],ids+[ids[0]]):
            haystack.write_text(json.dumps({"question":ordered}))
            with self.assertRaises((BridgeError,ValueError,RuntimeError)):
                params["expected_history"]=expected_history(haystack,trajectories,source)
                with patch("bridge.WorkerBridge.run",side_effect=AssertionError("worker reached")):
                    load_memory(saved,{"memory_type":"scrinia","memory_params":params})
        haystack.write_text(json.dumps({"question":ids}))
        changed=copy.deepcopy(originals);changed[ids[0]]["states"][0]["text"]+=" changed"
        trajectories.write_text("\n".join(json.dumps(v) for v in changed.values())+"\n")
        changed_text=expected_history(haystack,trajectories,source)
        self.assertNotEqual(changed_text,expected)
        from PIL import Image
        Image.new("RGB",(2,2),"red").save(source/"screen.png")
        changed_image=expected_history(haystack,trajectories,source)
        self.assertNotEqual(changed_image,changed_text)
        for history in (changed_text,changed_image):
            params["expected_history"]=history
            with patch("bridge.WorkerBridge.run",side_effect=AssertionError("worker reached")):
                with self.assertRaisesRegex(BridgeError,"HISTORY"):
                    load_memory(saved,{"memory_type":"scrinia","memory_params":params})
        loaded.memory_params["expected_history"]=changed_image
        with patch.object(loaded.bridge,"run",side_effect=AssertionError("provider reached")):
            with self.assertRaisesRegex(BridgeError,"HISTORY"):
                loaded.query("invented query")

    def test_old_generation_without_explicit_history_fails_clearly(self):
        memory=self.committed_memory()
        metadata=memory.snapshots.generation_path/".generation.json"
        value=json.loads(io_path(metadata).read_text());value.pop("history");value["version"]=1
        atomic_json(metadata,value)
        pointer=memory.snapshots.root/"current.json"
        value=json.loads(io_path(pointer).read_text())
        value["metadataHash"]=hashlib.sha256(io_path(metadata).read_bytes()).hexdigest()
        atomic_json(pointer,value)
        with self.assertRaisesRegex(BridgeError,"HISTORY"):
            memory.snapshots.current()

# unittest otherwise inherits the expensive upstream harness tests as duplicates.
for name in list(harness_fixture.OfficialHarnessTests.__dict__):
    if name.startswith("test_") and name not in HistoryRecoveryTests.__dict__:
        setattr(HistoryRecoveryTests,name,None)
