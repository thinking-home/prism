import importlib.util
from pathlib import Path
import tempfile
import tarfile
import unittest
import zipfile
import argparse
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("release", Path(__file__).parents[1] / "release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_git_tag_points_to_build_commit_and_cannot_be_overwritten(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(release, "ROOT", Path(temp)):
            release.run("git", "init")
            release.run("git", "-c", "user.name=Release Test", "-c", "user.email=test@example.invalid",
                        "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Initial")
            commit = release.check_release_git("1.2.3")
            release.tag_release("1.2.3", commit)
            self.assertEqual(release.run("git", "rev-parse", "refs/tags/v1.2.3", capture=True).strip(), commit)
            with self.assertRaisesRegex(RuntimeError, "already exists"):
                release.check_release_git("1.2.3")

    def test_git_rejects_untracked_changes_and_head_change(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(release, "ROOT", Path(temp)):
            release.run("git", "init")
            def commit():
                release.run("git", "-c", "user.name=Release Test", "-c", "user.email=test@example.invalid",
                            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Commit")
            commit()
            original = release.check_release_git("1.2.3")
            (Path(temp) / "new-file.txt").write_text("change")
            with self.assertRaisesRegex(RuntimeError, "Commit or stash"):
                release.tag_release("1.2.3", original)
            (Path(temp) / "new-file.txt").unlink()
            commit()
            with self.assertRaisesRegex(RuntimeError, "HEAD changed"):
                release.tag_release("1.2.3", original)
            self.assertEqual(release.run("git", "tag", "--list", capture=True).strip(), "")

    def test_failed_build_and_check_mode_do_not_tag(self):
        with tempfile.TemporaryDirectory() as temp, \
                patch.object(release, "ARTIFACTS", Path(temp)), \
                patch.object(release, "check_release_git", return_value="commit"), \
                patch.object(release, "preflight"), \
                patch.object(release, "tag_release") as tag, \
                patch.object(release, "build", side_effect=RuntimeError("Build failed")) as build:
            args = ["release.py", "--version", "1.2.3", "--android-version-code", "2"]
            with patch("sys.argv", args + ["--check"]):
                release.main()
            build.assert_not_called()
            tag.assert_not_called()
            with patch("sys.argv", args), self.assertRaisesRegex(RuntimeError, "Build failed"):
                release.main()
            tag.assert_not_called()

    def test_msi_version_limits_and_path_injection(self):
        for value in ("../output", "1.2", "1.2.3-beta", "256.1.0", "1.1.65536", "01.2.3"):
            with self.subTest(value=value), self.assertRaises(argparse.ArgumentTypeError):
                release.version_value(value)
        self.assertEqual(release.version_value("255.255.65535"), "255.255.65535")

    def test_android_version_limits(self):
        for value in ("0", "-1", "2100000001"):
            with self.assertRaises(argparse.ArgumentTypeError):
                release.version_code(value)

    def test_linux_archive_permissions_on_windows(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "prism-host"
            (source / "ffmpeg").mkdir(parents=True)
            for name in ("Prism.Host.Console", "start.sh", "appsettings.json", "ffmpeg/ffmpeg", "ffmpeg/ffprobe"):
                (source / name).write_text("test")
            archive = root / "host.tar.gz"
            release.pack(source, archive, True)
            with tarfile.open(archive) as package:
                self.assertEqual(package.getmember("prism-host/start.sh").mode, 0o755)
                self.assertEqual(package.getmember("prism-host/Prism.Host.Console").mode, 0o755)
                self.assertEqual(package.getmember("prism-host/ffmpeg/ffprobe").mode, 0o755)
                self.assertEqual(package.getmember("prism-host/appsettings.json").mode, 0o644)

    def test_zip_contains_only_staged_payload(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "payload"
            source.mkdir()
            (root / "keystore.properties").write_text("secret")
            (source / "app.exe").write_bytes(b"binary")
            release.pack(source, root / "app.zip", False)
            with zipfile.ZipFile(root / "app.zip") as package:
                self.assertEqual(package.namelist(), ["app.exe"])

    def test_corrupt_download_cache_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            previous = release.ARTIFACTS
            try:
                release.ARTIFACTS = Path(temp)
                cache = Path(temp) / "cache"
                cache.mkdir()
                (cache / "ffmpeg.zip").write_bytes(b"corrupt")
                with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
                    release.download({"url": "https://example.invalid/ffmpeg.zip", "sha256": "0" * 64})
            finally:
                release.ARTIFACTS = previous


if __name__ == "__main__":
    unittest.main()
