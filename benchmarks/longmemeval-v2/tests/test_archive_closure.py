"""Static archive closure checks; extracted offline build is recorded separately."""
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freeze_experiment import tree

ROOT = Path(__file__).resolve().parents[3]

class ArchiveClosureTests(unittest.TestCase):
    def test_project_references_and_explicit_build_inputs_are_in_archive(self):
        files = tree(ROOT, source=True)
        self.assertNotIn("Scrinia.sln", list(files), "The partial project archive must not advertise the complete repository solution.")
        self.assertIn("LICENSE", files)
        self.assertIn("release/embeddings-payloads.json", files)
        for name in files:
            if not name.endswith((".csproj", ".props", ".targets")):
                continue
            with self.subTest(project=name):
                project = ROOT / name
                value = ET.fromstring(project.read_text(encoding="utf-8"))
                for element in value.iter():
                    tag = element.tag.rsplit("}", 1)[-1]
                    attribute = "Project" if tag == "Import" else "Include"
                    if tag not in ("ProjectReference", "Import", "EmbeddedResource", "Content", "None", "Compile"):
                        continue
                    include = element.get(attribute)
                    if not include or "$(" in include or "@(" in include:
                        continue
                    for item in include.split(";"):
                        pattern = item.replace("\\", "/")
                        matches = list(project.parent.glob(pattern))
                        if not any(character in pattern for character in "*?["):
                            self.assertTrue(matches, (name, include))
                        for dependency in matches:
                            if dependency.is_file():
                                relative = dependency.resolve().relative_to(ROOT).as_posix()
                                self.assertIn(relative, files, (name, include, relative))
