"""Synthetic subprocess fixture. Never imported by production code."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

mode = sys.argv[1]
args = sys.argv[2:]
body = json.loads(Path(args[args.index("--request") + 1]).read_text())
target = Path(args[args.index("--result") + 1])
root = Path(body["paths"]["root"])
if mode in ("cancel", "ignore-cancel", "closed-stdin"):
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"],
                             stdin=subprocess.DEVNULL)
    (root / "child.pid").write_text(str(child.pid))
    try:
        if mode == "closed-stdin":
            os.close(sys.stdin.fileno())
            (root / "stdin-closed").touch()
            time.sleep(0.8)
        elif mode == "ignore-cancel":
            time.sleep(60)
        else:
            line = sys.stdin.readline()
            assert not line or json.loads(line)["operation"] == "cancel"
    finally:
        child.terminate()
        child.wait()
        (root / "child-exited").touch()
        (root / "worker-exited").touch()
    sys.exit(3)
if mode == "missing":
    sys.exit(0)
if mode == "partial":
    target.write_text('{"version":')
    sys.exit(0)
result = {"version": 1, "operationId": body["operationId"], "status": "ok",
          "eventLogPath": body["paths"]["eventLogPath"], "receipt": None,
          "evidence": [], "errorCode": None}
if mode == "mismatch":
    result["operationId"] = "b" * 32
if mode == "unknown":
    result["goldAnswer"] = "forbidden"
if mode == "version":
    result["version"] = 2
target.write_text(json.dumps(result))
sys.exit(7 if mode == "nonzero" else 0)
