from pathlib import Path
import sys,tempfile,unittest
ROOT=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/"artifacts/longmemeval-v2/upstream"))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from batching import BatchPlanner,ProcessorCounter
from trajectory_input import normalize_trajectory
from PIL import Image
class BatchingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.counter=ProcessorCounter(ROOT/"artifacts/longmemeval-v2/processor")
    def test_actual_processor_fragments_preserve_text_dates_and_images(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); Image.new("RGB",(64,64),"blue").save(root/"s.png")
            text="2026-03-04 caf\u00e9 \U0001f600 original source. "*60
            raw={"id":"synthetic","goal":"Synthetic goal","start_url":"https://synthetic.invalid","states":[{"step":7,"date":"2026-03-04","url":"https://synthetic.invalid","text":text,"screenshot":"s.png"}]}
            trajectory=normalize_trajectory(raw,root)
            planner=BatchPlanner(self.counter,[],fragment_tokens=64); planned=planner.prepare(trajectory)
            fragments=planned["fragments"]
            self.assertGreater(len(fragments),1)
            self.assertEqual(b"".join(f["text"].encode("utf-8") for f in fragments),text.encode("utf-8"))
            self.assertEqual(fragments[-1]["endByte"],len(text.encode("utf-8")))
            self.assertIn("2026-03-04",trajectory["originalJson"])
            self.assertTrue(all(b["inputTokens"]<=24576 and len(b["images"])<=4 for b in planned["batches"]))
            self.assertGreater(planned["batches"][0]["inputTokens"],self.counter.text_tokens(planned["batches"][0]["messageText"]))
            self.assertEqual(planned,planner.prepare(trajectory))
    def test_rejects_image_that_cannot_fit_and_insufficient_context(self):
        with self.assertRaises(ValueError): BatchPlanner(self.counter,[],served_context=30000)
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); Image.new("RGB",(1024,1024),"red").save(root/"s.png")
            t=normalize_trajectory({"id":"s","goal":"x","start_url":"x","states":[{"url":"x","text":"x","screenshot":"s.png"}]},root)
            with self.assertRaises(ValueError): BatchPlanner(self.counter,[],input_tokens=100,served_context=20000).prepare(t)
    def test_rejects_remote_screenshot(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError): normalize_trajectory({"id":"s","goal":"x","start_url":"x","states":[{"url":"x","text":"x","screenshot":"https://example.invalid/a.png"}]},Path(temp))
