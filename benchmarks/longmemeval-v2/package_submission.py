"""Validate paired complete runs and invoke the unchanged official package utilities."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from filesystem import io_path, io_name
from freeze_experiment import verify_frozen, read, digest

def validate_runs(folder, web_run, enterprise_run):
    folder = Path(folder)
    verify_frozen(folder)
    pair = read(folder / "resolved_pair.json")
    sys.path.insert(0, pair["upstream_root"])
    from leaderboard.submission_utils import validate_run, validate_run_pair
    web = validate_run(Path(io_name(web_run)), "web", "small", method_override="scrinia")
    enterprise = validate_run(Path(io_name(enterprise_run)), "enterprise", "small", method_override="scrinia")
    validate_run_pair(web, enterprise)
    for domain, run in (("web", Path(web_run)), ("enterprise", Path(enterprise_run))):
        from run_scrinia import run_artifact_hashes
        status = read(run / "scrinia_run.json")
        if status != {"status": "complete", "upstreamRevision": pair_revision(),
                "methodFingerprint": pair["experiments"][domain]["method_fingerprint"],
                "upstreamUnchanged": True, "frozenManifestHash": digest(folder / "manifest.json"),
                "artifactHashes": run_artifact_hashes(run)}:
            raise ValueError("Run is incomplete or does not belong to this frozen experiment.")
        if read(run / "experiment.json") != pair["experiments"][domain]:
            raise ValueError("Run settings changed from the frozen domain configuration.")
        if read(run / "upstream_hashes.json") != read(folder / "manifest.json")["upstream_files"]:
            raise ValueError("Run evaluator source differs from the frozen experiment.")
    return pair

def pair_revision():
    from experiment_config import UPSTREAM_REVISION
    return UPSTREAM_REVISION

def package_submission(folder, web_run, enterprise_run, output_root, submission_name):
    pair = validate_runs(folder, web_run, enterprise_run)
    folder = Path(folder)
    output = Path(output_root).absolute()
    if io_path(output).exists():
        raise ValueError("Package output root must be new.")
    upstream = Path(pair["upstream_root"])
    step1 = upstream / "leaderboard/build_submission_step_1_single_operating_point.py"
    step2 = upstream / "leaderboard/build_submission_step_2_build_package.py"
    subprocess.run([sys.executable, io_name(step1), io_name(web_run), io_name(enterprise_run),
        submission_name, "small-frozen", "small", "--method", "scrinia", "--output-root", io_name(output)], check=True)
    point = output / submission_name / "operating_points/small-frozen"
    subprocess.run([sys.executable, io_name(step2), submission_name,
        io_name(folder / "SYSTEM_DESCRIPTION.md"), io_name(folder / "source.zip"), io_name(point),
        "--output-root", io_name(output)], check=True)
    return output / (submission_name + ".tar.gz")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frozen-experiment", required=True)
    parser.add_argument("--web-run", required=True)
    parser.add_argument("--enterprise-run", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--submission-name", default="scrinia-v2-small")
    args = parser.parse_args()
    print(package_submission(args.frozen_experiment, args.web_run, args.enterprise_run,
        args.output_root, args.submission_name))

if __name__ == "__main__":
    main()
