"""Offline retention tests use real files and synthetic worker receipts."""
import json
from pathlib import Path
import shutil
import unittest
from unittest.mock import patch

import test_snapshots
from snapshots import SnapshotStore, fingerprint
from bridge import BridgeError


class GrowingBridge(test_snapshots.FixtureBridge):
    def run(self, operation, request, cancel):
        if operation != "learn" or self.fail:
            return super().run(operation, request, cancel)
        self.calls.append(operation)
        stage = Path(request["stagingPath"])
        (stage / "workspace" / (request["trajectory"]["id"] + ".txt")).write_bytes(b"x" * 4096)
        return {"receipt": {"sourceHash": request["trajectory"]["sourceHash"],
                "previousSnapshotHash": request.get("previousFingerprint"),
                "newSnapshotHash": fingerprint(stage), "committedOperationId": request["operationId"]}}


class SnapshotRetentionTests(unittest.TestCase):
    setUp = test_snapshots.SnapshotTests.setUp
    trajectory = test_snapshots.SnapshotTests.trajectory

    def insert(self, index, store=None):
        return (store or self.store).insert(self.trajectory(ident=f"trajectory-{index}"),
                                           "b" * 64, self.bridge, self.cancel)

    def generations(self):
        return set((self.root / "generations").iterdir())

    def test_successive_insertions_retain_at_most_two_complete_generations(self):
        self.bridge = GrowingBridge()
        prior = None
        for index in range(25):
            self.insert(index)
            current = self.store.generation_path
            expected = {current} if prior is None else {prior, current}
            self.assertEqual(self.generations(), expected)
            self.assertEqual(len(list((current / "workspace").glob("*.txt"))), index + 1)
            prior = current
        current_bytes = sum(p.stat().st_size for p in current.rglob("*") if p.is_file())
        retained_bytes = sum(p.stat().st_size for p in (self.root / "generations").rglob("*") if p.is_file())
        self.assertLessEqual(retained_bytes, 2 * current_bytes)
        self.assertEqual(len(list((self.root / "retired").glob("*.json"))), 1)
        # Historical idempotency depends on the current ledger, not retired directories.
        calls = len(self.bridge.calls)
        self.insert(0)
        self.assertEqual(len(self.bridge.calls), calls)

    def test_acknowledgement_loss_reconciles_then_retires_on_replay(self):
        self.insert(0)
        oldest = self.store.generation_path
        self.insert(1)
        previous = self.store.generation_path
        def lose_ack(point):
            if point == "after-publication":
                raise OSError("lost acknowledgement")
        self.store.fault = lose_ack
        with self.assertRaisesRegex(OSError, "lost acknowledgement"):
            self.insert(2)
        self.assertTrue(oldest.exists())
        resumed = SnapshotStore(self.root, self.store.request_factory)
        receipt = self.insert(2, resumed)
        self.assertEqual(receipt, resumed.current()["receipt"])
        self.assertEqual(self.generations(), {previous, resumed.generation_path})
        self.assertEqual(list((self.root / "uncertain").glob("*.json")), [])

    def test_uncertain_stage_and_previous_generations_survive_later_publications(self):
        self.insert(0)
        self.insert(1)
        self.bridge.fail = True
        with self.assertRaisesRegex(BridgeError, "TOOL_COMPLETION_UNCERTAIN"):
            self.insert(2)
        retained = self.generations()
        markers = {p: p.read_bytes() for p in (self.root / "uncertain").glob("*.json")}
        self.bridge.fail = False
        self.insert(3)
        self.insert(4)
        self.assertTrue(retained.issubset(self.generations()))
        self.assertEqual({p: p.read_bytes() for p in markers}, markers)

    def test_tampered_retirement_candidate_is_preserved(self):
        self.insert(0)
        oldest = self.store.generation_path
        self.insert(1)
        (oldest / "workspace" / "synthetic.txt").write_text("diagnostic evidence")
        self.insert(2)
        self.assertEqual((oldest / "workspace" / "synthetic.txt").read_text(), "diagnostic evidence")
        self.assertEqual(self.store.current()["history"][-1]["id"], "trajectory-2")

    def test_cleanup_failure_keeps_committed_success_and_retries_later(self):
        self.insert(0)
        oldest = self.store.generation_path
        self.insert(1)
        with patch("snapshots.shutil.rmtree", side_effect=OSError("file in use")):
            receipt = self.insert(2)
        self.assertEqual(receipt, self.store.current()["receipt"])
        self.assertTrue(oldest.exists())
        self.insert(2)
        self.assertFalse(oldest.exists())
        self.assertEqual(len(self.generations()), 2)

    def test_predecessor_pointer_remains_valid_after_new_generation_cleanup(self):
        from snapshots import atomic_json
        self.insert(0)
        self.insert(1)
        predecessor_pointer = json.loads((self.root / "current.json").read_text())
        predecessor = self.store.current()
        self.insert(2)
        atomic_json(self.root / "current.json", predecessor_pointer)
        self.assertEqual(self.store.current(), predecessor)
        self.assertEqual(self.insert(1), predecessor["receipt"])

    def test_failed_durability_barrier_preserves_last_durable_generation_across_publications(self):
        from snapshots import atomic_json
        for point in ("retirement-flush-file", "retirement-flush-directory",
                      "retirement-flush-generations", "retirement-flush-pointer",
                      "retirement-flush-root", "retirement-flush-ancestor"):
            with self.subTest(point=point):
                store = SnapshotStore(self.root / point, self.store.request_factory)
                self.insert(0, store)
                durable_pointer = json.loads((store.root / "current.json").read_text())
                durable_metadata = store.current()
                def fail_flush(actual):
                    if actual == point:
                        raise OSError("durability unavailable")
                store.fault = fail_flush
                self.insert(1, store)
                receipt = self.insert(2, store)
                self.assertEqual(receipt, store.current()["receipt"])
                latest_pointer = json.loads((store.root / "current.json").read_text())
                self.assertEqual(len(list((store.root / "generations").iterdir())), 3)
                # Model loss of unflushed pointer renames, not a physical power cut.
                atomic_json(store.root / "current.json", durable_pointer)
                self.assertEqual(store.current(), durable_metadata)
                atomic_json(store.root / "current.json", latest_pointer)
                store.fault = lambda point: None
                self.assertEqual(self.insert(2, store), receipt)
                self.assertEqual(len(list((store.root / "generations").iterdir())), 2)

    def test_unsupported_directory_sync_preserves_generations_and_committed_success(self):
        self.insert(0)
        original = self.store.generation_path
        with patch("snapshots._sync_directory", side_effect=OSError("unsupported filesystem")):
            self.insert(1)
            receipt = self.insert(2)
        self.assertEqual(receipt, self.store.current()["receipt"])
        self.assertTrue(original.exists())
        self.assertEqual(len(self.generations()), 3)
        self.insert(2)
        self.assertFalse(original.exists())

    def test_durability_flushes_copied_files_and_metadata_before_directory_and_pointer_entries(self):
        import snapshots
        self.bridge = GrowingBridge()
        self.insert(0)
        sync_file, sync_directory = snapshots._sync_file, snapshots._sync_directory
        events = []
        def flush_file(path):
            events.append(("file", path))
            sync_file(path)
        def flush_directory(path):
            events.append(("directory", path))
            sync_directory(path)
        with patch("snapshots._sync_file", side_effect=flush_file), patch("snapshots._sync_directory", side_effect=flush_directory):
            self.insert(1)
        current = self.store.generation_path
        expected_files = {current / ".generation.json", current / "workspace" / "trajectory-0.txt",
                          current / "workspace" / "trajectory-1.txt"}
        generation_files = {path for kind, path in events if kind == "file" and path.is_relative_to(current)}
        self.assertEqual(generation_files, expected_files)
        first_directory = next(index for index, event in enumerate(events) if event[0] == "directory")
        self.assertTrue(all(events.index(("file", path)) < first_directory for path in expected_files))
        self.assertLess(events.index(("directory", current / "workspace")), events.index(("directory", current)))
        self.assertLess(events.index(("directory", current / "home")), events.index(("directory", current)))
        self.assertEqual(events[-4:], [("directory", current.parent), ("file", self.root / "current.json"),
                                      ("directory", self.root), ("directory", self.root.parent)])

    def test_unticketed_generation_and_damaged_ticket_are_retained(self):
        self.insert(0)
        first = self.store.generation_path
        orphan = self.root / "generations" / ("f" * 32)
        shutil.copytree(first, orphan)
        self.insert(1)
        ticket = self.root / "retired" / (first.name + ".json")
        ticket.write_text("{malformed")
        self.insert(2)
        self.assertTrue(orphan.exists())
        self.assertTrue(first.exists())
        self.assertEqual(ticket.read_text(), "{malformed")

    def test_query_source_survives_publications_until_reader_finishes(self):
        import test_memory_contract
        memory = test_memory_contract.MemoryContractTests.memory(self)
        memory.snapshots = self.store
        self.insert(0)
        source = self.store.generation_path
        memory.snapshot_path = str(source)

        def retrieve(operation, request, cancel):
            self.insert(1)
            self.insert(2)
            self.assertTrue(source.exists())
            self.assertEqual((source / "workspace" / "synthetic.txt").read_text(), "a" * 64)
            return {"evidence": [{"type": "text", "value": "reader completed"}]}

        with patch.object(memory.bridge, "run", side_effect=retrieve):
            self.assertEqual(memory.query("q"), [{"type": "text", "value": "reader completed"}])
        self.insert(2)
        self.assertFalse(source.exists())
        self.assertEqual(len(self.generations()), 2)

    def test_overlapping_queries_can_share_the_same_generation(self):
        import test_memory_contract
        memory = test_memory_contract.MemoryContractTests.memory(self)
        memory.snapshots = self.store
        self.insert(0)
        memory.snapshot_path = str(self.store.generation_path)
        seen = []
        def retrieve(operation, request, cancel):
            seen.append(request["question"])
            if request["question"] == "outer":
                self.assertEqual(memory.query("inner"), [{"type": "text", "value": "inner"}])
            return {"evidence": [{"type": "text", "value": request["question"]}]}
        with patch.object(memory.bridge, "run", side_effect=retrieve):
            self.assertEqual(memory.query("outer"), [{"type": "text", "value": "outer"}])
        self.assertEqual(seen, ["outer", "inner"])

    def test_saved_snapshot_survives_source_retirement_and_load_still_validates(self):
        import test_official_harness
        from portability import identity_hash
        from memory_modules.memory import load_memory
        fixture = test_official_harness.OfficialHarnessTests()
        fixture.root = self.root
        memory = fixture.committed_memory()
        memory.snapshots.request_factory = self.store.request_factory
        original = memory.snapshots.generation_path
        metadata = memory.snapshots.current()
        saved = self.root / "saved"
        memory.save_memory(saved)
        for index in range(2):
            memory.snapshots.insert(self.trajectory(ident=f"later-{index}"),
                identity_hash(memory.memory_params["method_identity"]), self.bridge, self.cancel)
        self.assertFalse(original.exists())
        params = fixture.params("relocated")
        params["expected_history"] = metadata["history"]
        with patch("bridge.WorkerBridge.run", return_value={}):
            loaded = load_memory(saved, {"memory_type": "scrinia", "memory_params": params})
        self.assertEqual(loaded.snapshots.current(), metadata)
        (saved / "snapshot" / "workspace" / "tamper.txt").write_text("unexpected")
        with patch("bridge.WorkerBridge.run", return_value={}):
            with self.assertRaisesRegex(BridgeError, "BUNDLE_FINGERPRINT_MISMATCH"):
                load_memory(saved, {"memory_type": "scrinia", "memory_params": params})


if __name__ == "__main__":
    unittest.main()
