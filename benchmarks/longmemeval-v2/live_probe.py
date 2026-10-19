"""Synthetic live probes. Imported by preflight; never runs during offline checks."""
import asyncio
import base64
from io import BytesIO
import json
import os
from pathlib import Path
import secrets
import tempfile
import time
from types import SimpleNamespace
from freeze_experiment import MODEL, digest, read
from filesystem import io_path, io_name

def validate_attestation(value, config):
    expected = {"temperature": 0.6, "top_p": 0.95, "top_k": 20,
        "controller_output": 16384, "reader_output": 20000}
    revision = value.get("model_revision", "")
    if (value.get("version") != 1 or value.get("model") != MODEL
            or value.get("endpoint") != config["reader"]["base_url"]
            or value.get("endpoint") != config["controller"]["endpoint"]
            or value.get("reader_thinking_default") is not True
            or type(value.get("context_tokens")) is not int or value["context_tokens"] < 220000
            or value.get("precision") != "BF16" or value.get("mtp") is not True
            or not value.get("hardware") or value["hardware"] == "unverified"
            or not isinstance(revision, str) or len(revision) != 40 or any(c not in "0123456789abcdef" for c in revision)
            or value.get("applied_options") != expected or not value.get("verification_method")
            or not value.get("evidence")):
        raise ValueError("Deployment evidence is missing, unknown, conflicting, clamped or below the required capacity.")
    if "deployment" in config:
        declared = config["deployment"]
        for key in ("precision", "mtp", "hardware", "reader_thinking_default", "model_revision"):
            if value.get(key) != declared.get(key):
                raise ValueError("Live deployment evidence conflicts with the frozen deployment identity.")
    for item in value["evidence"]:
        if set(item) != {"path", "sha256"} or not Path(item["path"]).is_absolute() or digest(Path(item["path"])) != item["sha256"]:
            raise ValueError("Deployment evidence artifact changed.")
    return {"classification": "operator-attested", "details": value}

def verify_response(response, model, expected=None):
    if response.model != model:
        raise ValueError("Live provider model identity differs from the frozen model.")
    if not response.choices or response.choices[0].finish_reason not in ("stop", "tool_calls"):
        raise ValueError("Live response was truncated or unsupported.")
    if expected is not None and response.choices[0].message.content.strip() != expected:
        raise ValueError("Synthetic text/image/context answer failed.")

def verify_wire(request, role):
    expected = 16384 if role == "controller" else 20000
    extra = {"top_k": 20}
    if role == "controller":
        extra["chat_template_kwargs"] = {"enable_thinking": True}
    images = [p for message in request["messages"] for p in
        (message["content"] if isinstance(message.get("content"), list) else [])
        if p.get("type") == "image_url" and p.get("image_url", {}).get("url", "").startswith("data:image/png;base64,")]
    if request.get("max_completion_tokens" if role == "controller" else "max_tokens") != expected or request.get("extra_body") != extra or not images:
        raise ValueError("Thinking, image serialization or independent output-limit wire contract failed.")

def _image_question():
    from PIL import Image, ImageDraw, ImageFont
    answer = "".join(str(secrets.randbelow(10)) for _ in range(6))
    image = Image.new("RGB", (560, 160), "white")
    draw = ImageDraw.Draw(image)
    draw.text((25, 25), answer, fill="black", font=ImageFont.load_default(size=90))
    buffer = BytesIO()
    image.save(buffer, format="PNG")
    return answer, "data:image/png;base64," + base64.b64encode(buffer.getvalue()).decode("ascii")

def _request(config, role, messages):
    if role == "controller":
        settings = config["controller"]
        return {"model": settings["model"], "messages": messages,
            "temperature": settings["temperature"], "top_p": settings["topP"],
            "max_completion_tokens": settings["maxOutputTokens"], "timeout": settings["networkTimeoutSeconds"],
            "extra_body": {"top_k": settings["topK"],
                "chat_template_kwargs": {"enable_thinking": settings["thinking"]}}}
    # Use the official request builder, including its intentional absence of enable_thinking=true.
    from evaluation.harness import build_reader_request
    settings = config["reader"]
    args = SimpleNamespace(**settings, reader_enable_thinking=True,
        repetition_penalty=None, reasoning_effort=None, presence_penalty=None)
    return build_reader_request(args, messages)

async def _tool_probe(config, client, request, record):
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    learning = config["memory"]["learning"]
    with tempfile.TemporaryDirectory(prefix="scrinia-v2-live-probe-") as temporary:
        root = Path(temporary)
        (root / "workspace").mkdir()
        (root / "home").mkdir()
        server = StdioServerParameters(command=learning["mcp_executable"], args=[
            *learning["mcp_prefix_arguments"], "serve", "--no-auto-setup", "--workspace-root", str(root / "workspace")],
            env={**os.environ, "SCRINIA_HOME": str(root / "home")})
        async with stdio_client(server) as (receive, send):
            async with ClientSession(receive, send) as session:
                await session.initialize()
                listing = await session.list_tools()
                tool = next(t for t in listing.tools if t.name == "list")
                options = {**request, "tools": [{"type": "function", "function": {
                    "name": tool.name, "description": tool.description, "parameters": tool.inputSchema}}],
                    "tool_choice": {"type": "function", "function": {"name": tool.name}}}
                response = await record("controller-tool", client, options)
                calls = response.choices[0].message.tool_calls or []
                if len(calls) != 1 or calls[0].function.name != "list":
                    raise ValueError("Controller did not make the required real MCP tool call.")
                arguments = json.loads(calls[0].function.arguments)
                observed = await session.call_tool("list", arguments)
                if observed.isError:
                    raise ValueError("The synthetic MCP tool call failed.")
                followup = {**options, "tool_choice": "none", "messages": [
                    *options["messages"], response.choices[0].message.model_dump(exclude_none=True),
                    {"role": "tool", "tool_call_id": calls[0].id, "content":
                        "\n".join(c.text for c in observed.content if hasattr(c, "text"))}]}
                final = await record("controller-tool-followup", client, followup)
                if not final.choices[0].message.content:
                    raise ValueError("Controller omitted its response after the real tool call.")

def probe_pair(folder, attestation_path):
    from openai import AsyncOpenAI
    from batching import ProcessorCounter
    from preflight import offline_preflight
    offline_preflight(folder)
    pair = read(folder / "resolved_pair.json")
    config = {**pair["experiments"]["web"], "deployment": pair["deployment"]}
    evidence = validate_attestation(read(Path(attestation_path)), config)
    processor = ProcessorCounter(config["processor"]["assets"])
    attempts = []
    async def run():
        clients = {}
        try:
            for role in ("controller", "reader", "judge"):
                settings = config[role]
                key = settings["apiKeyEnvironmentVariable" if role == "controller" else "api_key_env"]
                endpoint = settings["endpoint" if role == "controller" else "base_url"]
                clients[role] = AsyncOpenAI(base_url=endpoint, api_key=os.environ[key], max_retries=0)
            async def record(phase, client, request):
                started = time.monotonic()
                measurement = {"phase": phase, "success": False, "usage": None, "cost_usd": None}
                try:
                    response = await client.chat.completions.create(**request)
                    verify_response(response, request["model"])
                    measurement["success"] = True
                    if response.usage is not None:
                        measurement["usage"] = response.usage.model_dump()
                    return response
                finally:
                    measurement["seconds"] = time.monotonic() - started
                    attempts.append(measurement)
            for role in ("controller", "reader"):
                answer, url = _image_question()
                image_messages = [{"role": "user", "content": [
                    {"type": "text", "text": "Read the six digits in this image. Reply only with those digits."},
                    {"type": "image_url", "image_url": {"url": url}}]}]
                request = _request(config, role, image_messages)
                verify_wire(request, role)
                response = await record(role + "-image", clients[role], request)
                verify_response(response, request["model"], answer)
                nonce = secrets.token_hex(12)
                text = _request(config, role, [{"role": "user", "content": "Reply exactly: " + nonce}])
                response = await record(role + "-text", clients[role], text)
                verify_response(response, text["model"], nonce)
                # Empirically exercise the declared input capacity with markers across the full context.
                target = 24576 if role == "controller" else 200000
                markers = [secrets.token_hex(8) for _ in range(3)]
                tokenizer = processor.processor.tokenizer
                instruction = "Return the three hexadecimal markers in order, separated by spaces.\n"
                lo, hi, prompt = 0, target, ""
                while lo <= hi:
                    count = (lo + hi) // 2
                    pad = " invented filler" * count
                    candidate = instruction + markers[0] + pad + markers[1] + pad + markers[2]
                    tokens = len(tokenizer.encode(candidate, add_special_tokens=False))
                    if tokens <= target - 8:
                        prompt = candidate
                        lo = count + 1
                    else:
                        hi = count - 1
                capacity = _request(config, role, [{"role": "user", "content": prompt}])
                response = await record(role + "-capacity", clients[role], capacity)
                verify_response(response, capacity["model"], " ".join(markers))
                attempts[-1]["measured_input_tokens"] = len(tokenizer.encode(prompt, add_special_tokens=False))
                if role == "controller":
                    await _tool_probe(config, clients[role], _request(config, role, [
                        {"role": "user", "content": "Call list once on this empty synthetic workspace and report the result."}]), record)
            nonce = secrets.token_hex(12)
            judge = config["judge"]
            request = {"model": judge["model"], "messages": [{"role": "user", "content": "Reply exactly: " + nonce}],
                "reasoning_effort": judge["reasoning_effort"], "max_completion_tokens": judge["max_completion_tokens"],
                "timeout": judge["timeout_seconds"]}
            response = await record("judge-text", clients["judge"], request)
            verify_response(response, judge["model"], nonce)
        finally:
            for client in clients.values():
                await client.close()
    try:
        asyncio.run(run())
    except Exception as error:
        if attempts:
            attempts[-1]["success"] = False
        # Never include provider bodies, keys or generated probe answers in the report.
        from freeze_experiment import write
        write(folder.parent / (folder.name + "-failed-live-probe.json"),
            {"status": "failed", "failure": type(error).__name__, "attempts": attempts})
        raise ValueError("Live synthetic probe failed; inspect the redacted failure/attempt report.") from None
    # Detect server evidence or frozen-input changes while probes were in flight.
    validate_attestation(read(Path(attestation_path)), config)
    from freeze_experiment import verify_frozen
    verify_frozen(folder)
    return {"status": "ready", "live": "empirical-probes-passed",
        "frozen_manifest_hash": digest(folder / "manifest.json"),
        "wire": "verified", "deployment": evidence, "attempts": attempts,
        "claims": {"images": "empirically-verified", "tool_calls": "empirically-verified",
            "context": "empirically-verified", "effective_sampling": "operator-attested",
            "reader_thinking_default": "operator-attested"},
        "full_scored_run": "not-started"}
