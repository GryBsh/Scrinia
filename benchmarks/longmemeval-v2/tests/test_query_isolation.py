import json
from pathlib import Path
import unittest
from unittest.mock import patch
import test_memory_contract
from bridge import BridgeError

class QueryIsolationTests(unittest.TestCase):
    setUp=test_memory_contract.MemoryContractTests.setUp
    memory=test_memory_contract.MemoryContractTests.memory
    def test_queries_use_separate_copies_and_never_expose_prior_logs(self):
        memory=self.memory(learning={"mcp_executable":str(self.root / "scri.exe"),"mcp_prefix_arguments":[],"tool_catalog_path":str(self.root / "tools.json"),"processor_assets":str(self.root),"served_context_tokens":65536})
        (self.root / "scri.exe").write_bytes(b"synthetic")
        (self.root / "tools.json").write_text(json.dumps([{"name":n,"inputSchema":{"type":"object"}} for n in ("search","list","recall")]))
        snapshot=Path(memory.snapshot_path)
        (snapshot/"workspace").mkdir(parents=True,exist_ok=True)
        (snapshot/"home").mkdir(exist_ok=True)
        (snapshot/"workspace"/"source.txt").write_text("immutable")
        seen=[]
        def run(operation,request,cancel):
            path=Path(request["paths"]["workspace"])
            self.assertNotEqual(path,snapshot/"workspace")
            self.assertEqual((path/"source.txt").read_text(),"immutable")
            self.assertFalse((path/"prior-query.txt").exists())
            (path/"prior-query.txt").write_text("write contained by copy")
            seen.append(request)
            return {"evidence":[]}
        # Incidental writes are contained, but still invalidate the successful result.
        with patch.object(memory.bridge,"run",side_effect=run):
            for question in ("first?","second?"):
                with self.assertRaisesRegex(BridgeError,"QUERY_COPY_CHANGED"):
                    memory.query(question)
        self.assertEqual(len(seen),2)
        self.assertNotEqual(seen[0]["snapshotPath"],seen[1]["snapshotPath"])
        self.assertEqual((snapshot/"workspace"/"source.txt").read_text(),"immutable")
        self.assertFalse((snapshot/"workspace"/"prior-query.txt").exists())
    def test_authoritative_mutation_and_worker_failure_propagate(self):
        memory=self.memory()
        snapshot=Path(memory.snapshot_path);snapshot.mkdir(exist_ok=True)
        (snapshot/"source.txt").write_text("original")
        with patch.object(memory,"_retrieval_configuration",return_value={}):
            with patch.object(memory.bridge,"run",side_effect=BridgeError("MODEL_FAILED")):
                with self.assertRaisesRegex(BridgeError,"MODEL_FAILED"):memory.query("q")

    def test_repeated_question_is_not_cached_and_selected_original_images_survive_cleanup(self):
        import hashlib
        memory=self.memory();snapshot=Path(memory.snapshot_path)
        data=b"synthetic-original-image";digest=hashlib.sha256(data).hexdigest()
        (snapshot/"sources").mkdir();(snapshot/"sources"/"image.png").write_bytes(data)
        (snapshot/"catalogs").mkdir()
        (snapshot/"catalogs"/"source.json").write_text(json.dumps({"trajectory":{"states":[{"screenshot":{"path":"sources/image.png","sha256":digest,"byteLength":len(data)}}]}}))
        calls=[]
        def run(operation,request,cancel):
            calls.append(request)
            return {"evidence":[{"type":"text","value":"original context"},{"type":"image","value":str(Path(request["snapshotPath"])/"sources"/"image.png")}]}
        with patch.object(memory.bridge,"run",side_effect=run):
            for _ in range(2):
                evidence=memory.query("same question")
                self.assertEqual(Path(evidence[1]["value"]).read_bytes(),data)
                self.assertFalse(Path(calls[-1]["snapshotPath"]).exists())
        self.assertEqual(len(calls),2)
        self.assertNotEqual(calls[0]["paths"]["home"],calls[1]["paths"]["home"])
    def test_arbitrary_worker_image_and_authoritative_mutation_raise(self):
        memory=self.memory();snapshot=Path(memory.snapshot_path)
        original=snapshot/"workspace"/"source.txt";original.write_text("original")
        unrelated=Path(memory.paths["root"])/"unrelated.png";unrelated.write_bytes(b"unrelated")
        with patch.object(memory.bridge,"run",return_value={"evidence":[{"type":"image","value":str(unrelated)}]}):
            with self.assertRaisesRegex(BridgeError,"INVALID_EVIDENCE_IMAGE"):memory.query("q")
        def mutate(*args):
            original.write_text("changed")
            return {"evidence":[]}
        with patch.object(memory.bridge,"run",side_effect=mutate):
            with self.assertRaisesRegex(BridgeError,"AUTHORITATIVE_SNAPSHOT_CHANGED"):memory.query("q")
    def test_snapshot_symlink_or_junction_is_rejected_before_worker(self):
        import os
        import subprocess
        memory=self.memory();snapshot=Path(memory.snapshot_path)
        outside=self.root/"outside";outside.mkdir();(outside/"file.txt").write_text("outside")
        link=snapshot/"workspace"/"escape"
        if os.name=="nt":subprocess.run(["cmd","/c","mklink","/J",str(link),str(outside)],check=True,capture_output=True)
        else:link.symlink_to(outside,target_is_directory=True)
        try:
            with patch.object(memory.bridge,"run") as run:
                with self.assertRaises(BridgeError):memory.query("q")
                run.assert_not_called()
        finally:link.rmdir() if os.name=="nt" else link.unlink()

if __name__=="__main__":unittest.main()
