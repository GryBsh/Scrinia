from pathlib import Path
import sys, tempfile, threading, unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from snapshots import SnapshotStore, fingerprint
from bridge import BridgeError

class FixtureBridge:
    def __init__(self): self.calls=[]; self.fail=False
    def run(self,operation,request,cancel):
        self.calls.append(operation)
        if operation=="learn":
            if self.fail: raise BridgeError("TOOL_COMPLETION_UNCERTAIN")
            stage=Path(request["stagingPath"])
            (stage/"workspace"/"synthetic.txt").write_text(request["trajectory"]["sourceHash"])
            return {"receipt":{"sourceHash":request["trajectory"]["sourceHash"],"previousSnapshotHash":request.get("previousFingerprint"),"newSnapshotHash":fingerprint(stage),"committedOperationId":request["operationId"]}}
        if fingerprint(Path(request["snapshotPath"]))!=request["expectedFingerprint"]: raise BridgeError("SNAPSHOT_INVALID")
        return {}

class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name); self.bridge=FixtureBridge(); self.cancel=threading.Event()
        self.store=SnapshotStore(self.root,lambda op,**fields:{"operation":op,"operationId":"1"*32,**fields})
    def trajectory(self,source="a",ident="synthetic"):
        return {"id":ident,"sourceHash":source*64,"states":[],"fragments":[],"batches":[]}
    def test_verified_publication_and_conflict(self):
        receipt=self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel)
        self.assertEqual(self.store.current()["receipt"],receipt)
        calls=len(self.bridge.calls)
        self.assertEqual(self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel),receipt)
        self.assertEqual(len(self.bridge.calls),calls)
        with self.assertRaises(BridgeError): self.store.insert(self.trajectory("c"),"b"*64,self.bridge,self.cancel)
    def test_uncertain_write_blocks_replay_and_preserves_previous_generation(self):
        self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel)
        previous=self.store.current(); self.bridge.fail=True
        with self.assertRaises(BridgeError): self.store.insert(self.trajectory("c","second"),"b"*64,self.bridge,self.cancel)
        self.assertEqual(self.store.current(),previous); self.bridge.fail=False
        with self.assertRaises(BridgeError): self.store.insert(self.trajectory("c","second"),"b"*64,self.bridge,self.cancel)
        self.assertEqual(len(list((self.root/"uncertain").glob("*.json"))),1)
    def test_cancel_before_publication_and_restart_after_publication_ack_loss(self):
        self.store.fault=lambda p:self.cancel.set() if p=="before-publication" else None
        with self.assertRaises(BridgeError): self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel)
        self.assertIsNone(self.store.current()); self.cancel.clear()
        self.store.fault=lambda p:(_ for _ in ()).throw(OSError("lost ack")) if p=="after-publication" else None
        with self.assertRaises(OSError): self.store.insert(self.trajectory("d","next"),"b"*64,self.bridge,self.cancel)
        resumed=SnapshotStore(self.root,self.store.request_factory)
        self.assertEqual(resumed.insert(self.trajectory("d","next"),"b"*64,self.bridge,self.cancel),resumed.current()["receipt"])
    def test_persistence_failure_never_publishes_and_tampering_fails_closed(self):
        self.store.fault=lambda p:(_ for _ in ()).throw(OSError("disk")) if p=="persist-result" else None
        with self.assertRaises(OSError): self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel)
        self.assertIsNone(self.store.current()); self.store.fault=lambda p:None
        self.store.insert(self.trajectory("d","next"),"b"*64,self.bridge,self.cancel)
        (self.store.generation_path/"workspace"/"synthetic.txt").write_text("corrupt")
        with self.assertRaises(BridgeError): self.store.current()


    def test_metadata_rename_failure_retains_previous_and_receipt_tampering_is_rejected(self):
        self.store.insert(self.trajectory(),"b"*64,self.bridge,self.cancel)
        previous=self.store.current()
        import snapshots
        replace=snapshots.os.replace
        def fail_metadata(source,destination):
            if Path(destination).name==".generation.json": raise OSError("Synthetic persistence rename failure")
            return replace(source,destination)
        with patch("snapshots.os.replace",side_effect=fail_metadata):
            with self.assertRaises(OSError): self.store.insert(self.trajectory("c","second"),"b"*64,self.bridge,self.cancel)
        self.assertEqual(self.store.current(),previous)
        metadata=self.store.generation_path/".generation.json"
        metadata.write_text(metadata.read_text().replace("b"*64,"f"*64))
        with self.assertRaises(BridgeError): self.store.current()
