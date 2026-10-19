"""Synthetic ownership boundaries, including the built worker executable."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bridge import BridgeError, WorkerBridge, validate_request
from test_bridge import FIXTURE, request

def process_running(pid):
    if os.name != "nt":
        try:
            os.kill(pid, 0)
            return True
        except ProcessLookupError:
            return False
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x100000, False, pid)
    if not handle:
        return False
    try:
        return kernel.WaitForSingleObject(handle, 0) == 258
    finally:
        kernel.CloseHandle(handle)

class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
    def test_overlapping_workspace_home_are_rejected(self):
        body = request(self.root)
        body["paths"]["home"] = str(self.root / "workspace" / "nested")
        with self.assertRaises(BridgeError):
            validate_request(body)
    def test_remote_and_unowned_images_are_rejected(self):
        for value in ("https://example.invalid/image.png", str(self.root.parent / "outside.png")):
            body = request(self.root)
            body["questionImage"] = {"path": value, "sha256": "a" * 64,
                                     "mediaType": "image/png", "byteLength": 1}
            with self.subTest(value=value), self.assertRaises(BridgeError):
                validate_request(body)
    def test_force_cleanup_terminates_owned_grandchild(self):
        event = threading.Event()
        ready = self.root / "child.pid"
        def cancel_when_child_ready():
            for _ in range(200):
                if ready.exists():
                    event.set()
                    return
                threading.Event().wait(0.02)
            event.set()
        thread = threading.Thread(target=cancel_when_child_ready)
        thread.start()
        self.addCleanup(thread.join)
        bridge = WorkerBridge([sys.executable, str(FIXTURE), "ignore-cancel"],
                              self.root / "operations", cleanup_seconds=0.2)
        with self.assertRaisesRegex(BridgeError, "CANCELLED"):
            bridge.run("retrieve", request(self.root), event)
        self.assertTrue(ready.exists())
        self.assertFalse(process_running(int(ready.read_text())))
    def test_dotnet_worker_open_control_pipe_does_not_block_result_publication(self):
        dll = Path(__file__).resolve().parents[2] / "Scrinia.LongMemEvalV2.Worker/bin/Debug/net10.0/scrinia-lme-v2-worker.dll"
        self.assertTrue(dll.exists(), "Build the protocol test project first.")
        event = threading.Event()
        timer = threading.Timer(5, event.set)
        timer.start()
        self.addCleanup(timer.cancel)
        bridge = WorkerBridge(["dotnet", str(dll)], self.root / "operations", cleanup_seconds=0.2)
        with self.assertRaisesRegex(BridgeError, "WORKER_EXIT_FAILED"):
            bridge.run("retrieve", request(self.root), event)
        result_path = next((self.root / "operations").glob("*/result.json"))
        self.assertEqual(json.loads(result_path.read_text())["errorCode"], "UNSUPPORTED_OPERATION")

    def test_dotnet_worker_refuses_result_outside_owned_root(self):
        dll = Path(__file__).resolve().parents[2] / "Scrinia.LongMemEvalV2.Worker/bin/Debug/net10.0/scrinia-lme-v2-worker.dll"
        owned_root = self.root / "owned"
        owned_root.mkdir()
        source, target = owned_root / "request.json", self.root / "outside.json"
        source.write_text(json.dumps(request(owned_root)))
        completed = subprocess.run(["dotnet", str(dll), "--request", str(source),
                                   "--result", str(target)], input="", capture_output=True,
                                   text=True, timeout=10, check=False)
        self.assertEqual(completed.returncode, 2)
        self.assertFalse(target.exists())

    def test_dotnet_worker_eof_returns_cancelled_or_unsupported_without_hanging(self):
        dll = Path(__file__).resolve().parents[2] / "Scrinia.LongMemEvalV2.Worker/bin/Debug/net10.0/scrinia-lme-v2-worker.dll"
        self.assertTrue(dll.exists(), "Build the protocol test project before running all bridge tests.")
        source, target = self.root / "request.json", self.root / "result.json"
        source.write_text(json.dumps(request(self.root)))
        completed = subprocess.run(["dotnet", str(dll), "--request", str(source),
                                    "--result", str(target)], input="", capture_output=True,
                                   text=True, timeout=10, check=False)
        self.assertIn(completed.returncode, (3, 4))
        result = json.loads(target.read_text())
        self.assertIn(result["errorCode"], ("CANCELLED", "UNSUPPORTED_OPERATION"))
        self.assertIsNone(result["evidence"])

if __name__ == "__main__":
    unittest.main()
