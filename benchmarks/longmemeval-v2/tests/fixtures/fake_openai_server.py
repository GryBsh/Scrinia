"""Local-only scripted OpenAI protocol peer for invented integration fixtures."""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import re
import threading
import time

class FakeOpenAIServer:
    def __init__(self, *, retry_once=False, controller_delay=0.05, image_only=False):
        self.image_only = image_only
        self.calls = []
        self.errors = []
        self.retry_once = retry_once
        self.controller_delay = controller_delay
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                owner.calls.append(body)
                model = body["model"]
                tools = body.get("tools", [])
                message = {"role": "assistant", "content": None}
                finish = "stop"
                if model == "synthetic-controller" or (model == "unsloth/Qwen3.5-9B-MTP-GGUF:BF16" and tools):
                    time.sleep(owner.controller_delay)
                    if {t["function"]["name"] for t in tools} == {"search", "list", "recall"}:
                        if owner.retry_once:
                            owner.retry_once = False
                            self.send_response(503)
                            self.send_header("Content-Length", "0")
                            self.end_headers()
                            return
                        tool_messages = [m for m in body["messages"] if m["role"] == "tool"]
                        if not tool_messages:
                            message["tool_calls"] = [{"id": "call_synthetic_list", "type": "function",
                                "function": {"name": "list", "arguments": json.dumps({"mode": "full"})}}]
                            finish = "tool_calls"
                        else:
                            text = json.dumps(tool_messages)
                            match = re.search(r"source/([0-9a-f]{64})/000000/000000", text)
                            if match is None:
                                # Source IDs are always derived from actual listed immutable paths.
                                owner.errors.append("Synthetic source path absent from actual MCP list output")
                                message["content"] = '{"references":[]}'
                            source = None if match is None else hashlib.sha256((match.group(1) + ":0:0").encode()).hexdigest()
                            if source is not None:
                                message["content"] = json.dumps({"references": [
                                    {"kind": "source", "sourceId": source, "stateIndex": 0}]})
                    else:
                        message["content"] = "Learning complete."
                elif model in ("synthetic-Qwen3.5-9B-reader", "unsloth/Qwen3.5-9B-MTP-GGUF:BF16"):
                    text = json.dumps(body["messages"])
                    answer = "amber" if "amber" in text else "cobalt"
                    if owner.image_only:
                        import base64
                        from io import BytesIO
                        from PIL import Image
                        urls = [p["image_url"]["url"] for m in body["messages"]
                            for p in (m["content"] if isinstance(m.get("content"), list) else [])
                            if p.get("type") == "image_url"]
                        if len(urls) != 1:
                            raise ValueError("The image-only fixture requires one retrieved source image.")
                        with Image.open(BytesIO(base64.b64decode(urls[0].split(",", 1)[1]))) as image:
                            answer = "cobalt" if image.convert("RGB").getpixel((0, 0)) == (0, 71, 171) else "amber"
                    message["content"] = "\\boxed{" + answer + "}"
                elif model in ("synthetic-gpt-5.2-judge", "gpt-5.2"):
                    message["content"] = json.dumps({"label": 1, "reason": "Invented insight matches."})
                else:
                    self.send_error(400, "Unexpected synthetic model")
                    return
                payload = json.dumps({"id": "chatcmpl-synthetic", "object": "chat.completion",
                    "created": 1, "model": model, "choices": [{"index": 0, "message": message,
                    "finish_reason": finish}], "usage": {"prompt_tokens": 10, "completion_tokens": 5,
                    "total_tokens": 15}}).encode()
                try:
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
    @property
    def endpoint(self):
        return "http://127.0.0.1:" + str(self.server.server_port) + "/v1"
    def __enter__(self):
        self.thread.start()
        return self
    def __exit__(self, *args):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
