"""Long-path IO must never change the paths sent to the worker or MCP."""
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from filesystem import canonical_path, canonical_paths, io_name, io_path
from bridge import _absolute
from snapshots import fingerprint

class FilesystemTests(unittest.TestCase):
    def test_deep_io_reconstruction_copy_hash_containment_and_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            deep = root.joinpath(*("synthetic-path-segment-" + str(i) for i in range(14)))
            io_path(deep).mkdir(parents=True)
            source = deep / "source.bin"
            io_path(source).write_bytes(b"synthetic retained source bytes")
            self.assertGreater(len(str(source)), 260)
            self.assertEqual(_absolute(io_name(source)), source)
            self.assertEqual(canonical_path(Path(io_name(source))), source)
            self.assertTrue(_absolute(io_name(source)).is_relative_to(root))
            self.assertNotIn(chr(92) * 2 + "?", json.dumps({"path": str(_absolute(io_name(source)))}))
            copied = root / "copy"
            try:
                before = fingerprint(deep)
                shutil.copytree(io_path(deep), io_path(copied))
                self.assertEqual(fingerprint(copied), before)
                self.assertEqual(list(canonical_paths(io_path(deep).glob("*.bin"))), [source])
                self.assertEqual(io_path(source).read_bytes(), b"synthetic retained source bytes")
                if os.name == "nt":
                    self.assertTrue(io_name(source).startswith(chr(92) * 2 + "?" + chr(92)))
            finally:
                shutil.rmtree(io_path(copied))
                shutil.rmtree(io_path(root / "synthetic-path-segment-0"))
            self.assertFalse((root / "synthetic-path-segment-0").exists())

if __name__ == "__main__":
    unittest.main()
