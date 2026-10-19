"""Freeze source bytes, transitive runtime files and paired V2 settings."""
import argparse
import copy
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import sysconfig
import zipfile
from filesystem import io_path, canonical_path, canonical_paths
from experiment_config import UPSTREAM_REVISION, validate_experiment, method_fingerprint
from run_scrinia import upstream_hashes

MODEL = "unsloth/Qwen3.5-9B-MTP-GGUF:BF16"
HERE = Path(__file__).resolve().parent
EXCLUDED = {"bin", "obj", "__pycache__", ".git", ".scrinia", "artifacts", "node_modules"}
SOURCE_RULES = json.loads((HERE / "source-archive-manifest.json").read_text(encoding="utf-8"))

def digest(path):
    result = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()

def read(path):
    return json.loads(io_path(path).read_text(encoding="utf-8"))

def write(path, value):
    io_path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")

def tree(root, source=False):
    root = canonical_path(io_path(root).resolve())
    if not io_path(root).is_dir() or io_path(root).is_symlink():
        raise ValueError("A frozen directory is missing or linked.")
    result = {}
    included = None
    if source and io_path(root / ".git").exists():
        tracked = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"])
        included = set(tracked.decode("utf-8").split("\0"))
    for directory, dirs, files in os.walk(io_path(root), followlinks=False):
        current = canonical_path(Path(directory))
        excluded = EXCLUDED if source else {".git", "__pycache__"}
        dirs[:] = sorted(d for d in dirs if d not in excluded)
        if source:
            dirs[:] = [d for d in dirs if any(
                (current / d).relative_to(root).as_posix().casefold() == p.casefold()
                or (current / d).relative_to(root).as_posix().casefold().startswith(p.casefold() + "/")
                or p.casefold().startswith((current / d).relative_to(root).as_posix().casefold() + "/")
                for p in SOURCE_RULES["include"])]
        for name in dirs + files:
            if io_path(current / name).is_symlink():
                raise ValueError("Linked frozen inputs are unsupported.")
        for name in sorted(files):
            path = current / name
            relative = path.relative_to(root).as_posix()
            if name.endswith((".pyc", ".pyo")):
                continue
            if source:
                if included is not None and relative not in included:
                    continue
                if not any(relative.casefold() == p.casefold() or relative.casefold().startswith(p.casefold() + "/") for p in SOURCE_RULES["include"]):
                    continue
                if path.suffix.lower() not in SOURCE_RULES["extensions"]:
                    continue
                if re.search(r"(^|[./_-])(secret|credentials|token|password|env)([./_-]|$)", relative, re.I):
                    continue
            result[relative] = digest(path)
    return result

def dependency_inventory():
    roots = {str(Path(sys.executable).resolve().parent), str(Path(sys.base_prefix).resolve()),
             str(Path(sysconfig.get_path("purelib")).resolve())}
    # The venv interpreter, Python stdlib/native runtime and every installed package byte.
    dotnet = subprocess.check_output(["dotnet", "--list-runtimes"], text=True).splitlines()
    for runtime in dotnet:
        match = re.fullmatch(r"\S+ (\S+) \[(.+)\]", runtime)
        if match:
            roots.add(str(Path(match.group(2)) / match.group(1)))
    files = []
    for root in sorted(roots):
        for name, sha in tree(Path(root)).items():
            files.append({"path": str(Path(root) / name), "sha256": sha})
    return {"python": sys.version, "executable": str(Path(sys.executable).resolve()),
        "packages": sorted({d.metadata["Name"] + "==" + d.version for d in importlib.metadata.distributions()}),
        "files": files, "dotnet_runtimes": dotnet}

def resolve_pair(value):
    required = {"version", "source_root", "upstream_root", "guide_path", "experiments",
                "policies", "deployment", "exposure_log", "measurements"}
    if not isinstance(value, dict) or set(value) != required or value["version"] != 1:
        raise ValueError("Pair configuration has missing or unsupported fields.")
    if set(value["experiments"]) != {"web", "enterprise"}:
        raise ValueError("Both web and enterprise domains are required.")
    result = copy.deepcopy(value)
    for key in ("source_root", "upstream_root", "guide_path"):
        if not isinstance(result[key], str) or not Path(result[key]).is_absolute():
            raise ValueError("Freeze input paths must be absolute.")
    if result["policies"] != {"image": "original-bytes-no-resize", "query_learning": False}:
        raise ValueError("The source image and query isolation policies must be explicit.")
    deployment = result["deployment"]
    if (not isinstance(deployment, dict) or set(deployment) != {"precision", "mtp", "hardware",
            "reader_thinking_default", "model_revision"} or deployment["precision"] != "BF16" or deployment["mtp"] is not True):
        raise ValueError("Declare the BF16 MTP deployment and unresolved deployment evidence.")
    if not isinstance(result["exposure_log"], list) or not isinstance(result["measurements"], list):
        raise ValueError("Exposure and measurement logs must be lists.")
    for domain, config in result["experiments"].items():
        for role in ("controller", "reader", "judge"):
            model = config.get(role, {}).get("model")
            if not isinstance(model, str):
                raise ValueError("Each model identity is required.")
            config[role]["model"] = model.strip()
        config = validate_experiment(config)
        controller = config["controller"]
        expected = {"temperature": 0.6, "topP": 0.95, "topK": 20, "thinking": True,
                    "maxOutputTokens": 16384, "networkTimeoutSeconds": 180, "episodeSeconds": 300,
                    "maxModelRequests": 40, "maxAttempts": 5}
        if (config["domain"] != domain or config["tier"] != "small"
                or any(type(controller[k]) is not type(v) or controller[k] != v for k, v in expected.items())
                or controller["model"] != MODEL or config["reader"]["model"] != MODEL
                or config["judge"]["model"] != "gpt-5.2"):
            raise ValueError("The initial small-tier model identities and operating point are binding.")
        if config["judge"]["api_key_env"] == controller["apiKeyEnvironmentVariable"]:
            raise ValueError("GPT-5.2 needs its own explicit credential reference.")
        if config["mode"] == "submission" and (
                not re.fullmatch(r"[0-9a-f]{40}", str(deployment["model_revision"]))
                or deployment["reader_thinking_default"] is not True
                or deployment["hardware"] == "unverified"):
            raise ValueError("Declare the immutable deployment identity and effective thinking default before freezing real data.")
        if config["mode"] == "submission" and not re.fullmatch(r"[0-9a-f]{40,64}", config["data"]["revision"]):
            raise ValueError("Submission datasets require an immutable revision hash.")
        if config["memory"]["learning"]["served_context_tokens"] < 220000:
            raise ValueError("Declare capacity for the official reader context plus output budget.")
        if config.get("load_memory_dir") is not None:
            raise ValueError("The initial frozen pair must start fresh.")
        config["method_fingerprint"] = method_fingerprint(config)
        result["experiments"][domain] = config
    web, enterprise = result["experiments"].values()
    for field in ("mode", "controller", "reader", "judge", "memory", "processor", "limits", "method_fingerprint"):
        if web[field] != enterprise[field]:
            raise ValueError("Paired domains must use one identical operating point.")
    return result

def verify_upstream(root, expected=None):
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if revision != UPSTREAM_REVISION:
        raise ValueError("The official upstream revision changed.")
    subprocess.run(["git", "-C", str(root), "diff", "--exit-code", UPSTREAM_REVISION, "--",
                    "evaluation", "memory_modules", "data", "leaderboard"], check=True, capture_output=True)
    changed = subprocess.check_output(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=all",
        "--", "evaluation", "memory_modules", "data", "leaderboard"], text=True)
    if changed.strip():
        raise ValueError("Official upstream source tree changed or contains untracked inputs.")
    actual = upstream_hashes(root)
    if not actual or (expected is not None and actual != expected):
        raise ValueError("Official upstream source files changed.")
    return actual

def estimate_measurements(measurements):
    result = {}
    for phase in ("build", "query"):
        selected = [r for r in measurements if r["phase"] == phase]
        for record in selected:
            if record["seconds"] < 0 or type(record["success"]) is not bool or record["provider"] not in ("real", "fake"):
                raise ValueError("Invalid synthetic measurement.")
        costs = [r.get("cost_usd") for r in selected]
        result[phase] = {"attempts": len(selected), "failed_attempts": sum(not r["success"] for r in selected),
            "seconds_total": sum(r["seconds"] for r in selected) if selected else None,
            "seconds_per_attempt": sum(r["seconds"] for r in selected) / len(selected) if selected else None,
            "cost_usd_total": sum(costs) if costs and all(c is not None for c in costs) else None,
            "performance_evidence": "measured" if selected and all(r["provider"] == "real" for r in selected) else "synthetic-only" if selected else "unknown"}
    return result

def _bindings(pair):
    paths = {pair["guide_path"]}
    trees = {pair["source_root"]: tree(Path(pair["source_root"]), source=True)}
    for config in pair["experiments"].values():
        trees[config["data"]["root"]] = tree(Path(config["data"]["root"]))
        trees[config["processor"]["assets"]] = tree(Path(config["processor"]["assets"]))
        learning = config["memory"]["learning"]
        paths.add(learning["tool_catalog_path"])
        for argument in [*config["memory"]["worker_command"], learning["mcp_executable"], *learning["mcp_prefix_arguments"]]:
            path = Path(argument)
            if not path.is_absolute():
                located = shutil.which(argument)
                if located:
                    path = Path(located)
                else:
                    continue
            if io_path(path).is_file():
                # Capture all sibling DLLs, .deps/runtimeconfig, resources and script modules.
                trees[str(path.parent)] = tree(path.parent)
    return {"files": {p: digest(Path(p)) for p in sorted(paths)}, "trees": trees}

def freeze_experiment(value, output_dir):
    pair = resolve_pair(value)
    upstream = verify_upstream(Path(pair["upstream_root"]))
    output = Path(output_dir).absolute()
    if io_path(output).exists():
        raise ValueError("Freeze output directory must be new.")
    bindings = _bindings(pair)
    dependencies = dependency_inventory()
    source = tree(Path(pair["source_root"]), source=True)
    if not source:
        raise ValueError("Source archive cannot be empty.")
    io_path(output).mkdir(parents=True)
    write(output / "resolved_pair.json", pair)
    for domain, config in pair["experiments"].items():
        write(output / (domain + ".json"), config)
    write(output / "dependencies.json", dependencies)
    io_path(output / "requirements.lock").write_text("\n".join(dependencies["packages"]) + "\n", encoding="utf-8")
    with zipfile.ZipFile(io_path(output / "source.zip"), "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(source):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, io_path(Path(pair["source_root"]) / name).read_bytes())
    # Re-read all inputs after archive/dependency work, refusing a moving snapshot.
    if _bindings(pair) != bindings or tree(Path(pair["source_root"]), source=True) != source:
        raise ValueError("Freeze inputs changed during capture.")
    write(output / "estimates.json", estimate_measurements(pair["measurements"]))
    description = (HERE / "SYSTEM_DESCRIPTION.template.md").read_text(encoding="utf-8")
    description += "\n\nFrozen configuration and exposure log:\n\n```json\n" + json.dumps(pair, indent=2, sort_keys=True) + "\n```\n"
    io_path(output / "SYSTEM_DESCRIPTION.md").write_text(description, encoding="utf-8")
    outputs = {p.name: digest(p) for p in canonical_paths(io_path(output).iterdir())}
    manifest = {"version": 1, "upstream_revision": UPSTREAM_REVISION, "upstream_files": upstream,
        "source_files": source, "bindings": bindings, "outputs": outputs,
        "capabilities": {"live": "unverified", "server_thinking_default": "unverified"},
        "source_archive_acceptance": "organizer-confirmation-required"}
    write(output / "manifest.json", manifest)
    io_path(output / "manifest.sha256").write_text(digest(output / "manifest.json") + "\n", encoding="utf-8")
    return output

def verify_frozen(folder):
    folder = Path(folder).absolute()
    if digest(folder / "manifest.json") != io_path(folder / "manifest.sha256").read_text(encoding="utf-8").strip():
        raise ValueError("Frozen manifest changed.")
    manifest = read(folder / "manifest.json")
    for name, expected in manifest["outputs"].items():
        if digest(folder / name) != expected:
            raise ValueError("Frozen output changed: " + name)
    pair = read(folder / "resolved_pair.json")
    if _bindings(pair) != manifest["bindings"]:
        raise ValueError("Frozen source, payload, catalog, data or processor changed.")
    if resolve_pair(pair) != pair:
        raise ValueError("Resolved configuration changed.")
    dependencies = read(folder / "dependencies.json")
    if dependency_inventory() != dependencies:
        raise ValueError("Python dependency payload changed.")
    verify_upstream(Path(pair["upstream_root"]), manifest["upstream_files"])
    return manifest

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()
    print(freeze_experiment(read(Path(args.config)), args.output_dir))

if __name__ == "__main__":
    main()
