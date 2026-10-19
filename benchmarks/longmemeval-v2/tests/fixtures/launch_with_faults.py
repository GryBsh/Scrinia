"""Test-only injection outside upstream: account for bridge startup and cleanup."""
import os
from pathlib import Path
import shutil
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import run_scrinia
prepare = run_scrinia._processor_cache
def setup(config, output):
    result = prepare(config, output)
    from bridge import WorkerBridge
    original = WorkerBridge.run
    def run(self, operation, request, cancel):
        if operation == "retrieve":
            time.sleep(0.15)
            if os.environ.get("SYNTHETIC_CANCEL") == "1":
                started = time.monotonic()
                parent = cancel
                class Cancellation:
                    def is_set(self):
                        return parent.is_set() or time.monotonic() - started >= 0.25
                cancel = Cancellation()
        return original(self, operation, request, cancel)
    WorkerBridge.run = run
    remove = shutil.rmtree
    def cleanup(path, *args, **kwargs):
        if Path(path).name == "snapshot":
            time.sleep(0.2)
        return remove(path, *args, **kwargs)
    shutil.rmtree = cleanup
    return result
run_scrinia._processor_cache = setup
run_scrinia.main()
