"""Exercise package/catalog contracts without publishing a release."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("release", Path(__file__).resolve().parents[1] / "scripts/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_version_uses_run_number_and_rejects_invalid_assembly_versions(self):
        self.assertEqual(release.build_version("42"), "5.0.0.42")
        for run in ("-1", "1.2", "65535", "$(false)"):
            with self.assertRaises(ValueError):
                release.build_version(run)

    def test_archive_manifest_checksums_and_rerun(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in release.ARTIFACTS:
                (root / name).write_bytes(b"test assembly")
            (root / "Jellyfin.Controller.dll").write_bytes(b"must not be packaged")
            notes = 'Notes with "quotes", newlines\nand Unicode: café.'
            archive = release.package(root, root / "dist", "5.0.0.42", "owner/repo", notes)
            with zipfile.ZipFile(archive) as bundle:
                self.assertEqual(set(bundle.namelist()), {*release.ARTIFACTS, *release.LICENSE_FILES, "meta.json"})
                metadata = json.loads(bundle.read("meta.json"))
                self.assertEqual(metadata["targetAbi"], "12.0.0.0")
                self.assertEqual(metadata["changelog"], notes)
            for algorithm in ("md5", "sha256"):
                expected = hashlib.new(algorithm, archive.read_bytes()).hexdigest().upper()
                self.assertEqual(archive.with_suffix(f".zip.{algorithm}").read_text(), f"{expected}  {archive.name}\n")
            catalog = root / "manifest.json"
            catalog.write_text((release.ROOT / "manifest.json").read_text())
            seed = json.loads(catalog.read_text())
            seed[0]["versions"] = [{"version": "5.0.0.9"}, {"version": "5.0.0.100"}]
            catalog.write_text(json.dumps(seed))
            release.update_manifest(catalog, archive, "owner/repo", notes)
            first = catalog.read_text()
            release.update_manifest(catalog, archive, "owner/repo", notes)
            self.assertEqual(first, catalog.read_text())
            versions = json.loads(first)[0]["versions"]
            self.assertEqual([v["version"] for v in versions], ["5.0.0.100", "5.0.0.42", "5.0.0.9"])
            self.assertEqual(versions[1]["sourceUrl"], "https://github.com/owner/repo/releases/download/5.0.0.42/SSO-Auth_5.0.0.42.zip")

    def test_missing_dependencies_fail_before_creating_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(ValueError, "Missing release dependency"):
                release.package(root, root / "dist", "5.0.0.1", "owner/repo", "notes")
            self.assertFalse((root / "dist").exists())


if __name__ == "__main__":
    unittest.main()
