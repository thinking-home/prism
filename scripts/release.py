#!/usr/bin/env python3
"""Build the complete Prism release on Windows. Python 3.12+, standard library only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"


def run(*args, cwd=None, capture=False):
    # Resolve .cmd/.bat explicitly on Windows; never put passwords on the command line.
    command = [str(a) for a in args]
    command[0] = shutil.which(command[0]) or command[0]
    print("+ " + subprocess.list2cmdline(command), flush=True)
    result = subprocess.run(command, cwd=ROOT if cwd is None else cwd, check=True,
                            stdout=subprocess.PIPE if capture else None,
                            encoding="utf-8", errors="replace")
    return result.stdout or ""


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def version_value(value):
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", value):
        raise argparse.ArgumentTypeError("Version must be MAJOR.MINOR.PATCH, e.g. 0.4.0")
    major, minor, patch = map(int, value.split("."))
    if major > 255 or minor > 255 or patch > 65535:
        raise argparse.ArgumentTypeError("MSI limits: major/minor <= 255, patch <= 65535")
    return value


def version_code(value):
    number = int(value)
    if not 1 <= number <= 2100000000:
        raise argparse.ArgumentTypeError("Android version code must be 1..2100000000")
    return number


def check_release_git(version, expected_commit=None):
    commit = run("git", "rev-parse", "--verify", "HEAD^{commit}", capture=True).strip()
    if run("git", "status", "--porcelain", "--untracked-files=all", capture=True).strip():
        raise RuntimeError("Commit or stash all changes before building a tagged release")
    if expected_commit is not None and commit != expected_commit:
        raise RuntimeError("Git HEAD changed during the build; no release tag was created")
    tag = f"v{version}"
    if run("git", "tag", "--list", tag, capture=True).strip():
        raise RuntimeError(f"Git tag {tag} already exists; it will not be overwritten")
    return commit


def tag_release(version, commit):
    # Check again after the build and tag the exact commit captured before it.
    check_release_git(version, expected_commit=commit)
    run("git", "tag", "--no-sign", f"v{version}", commit)


def preflight(args):
    if os.name != "nt":
        raise RuntimeError("Run on Windows: WiX MSI compilation/validation requires Windows.")
    if args.java_home:
        os.environ["JAVA_HOME"] = str(args.java_home.resolve())
    if os.environ.get("JAVA_HOME"):
        os.environ["PATH"] = str(Path(os.environ["JAVA_HOME"]) / "bin") + os.pathsep + os.environ["PATH"]
    sdk = args.android_sdk or Path(os.environ.get("ANDROID_HOME") or
                                   os.environ.get("ANDROID_SDK_ROOT") or
                                   Path.home() / "AppData/Local/Android/Sdk")
    sdk = sdk.resolve()
    os.environ["ANDROID_HOME"] = str(sdk)
    os.environ["ANDROID_SDK_ROOT"] = str(sdk)
    for command in ("dotnet", "npm.cmd", "java"):
        if not shutil.which(command):
            raise RuntimeError(f"Missing build tool: {command}. See scripts/README.md")
    if not any(line.startswith("10.") for line in run("dotnet", "--list-sdks", capture=True).splitlines()):
        raise RuntimeError(".NET SDK 10 is required")
    run("java", "-version")
    for relative in ("platforms/android-36/android.jar", "build-tools/36.0.0/apksigner.bat",
                     "build-tools/36.0.0/aapt.exe"):
        if not (sdk / relative).is_file():
            raise RuntimeError(f"Missing Android SDK component: {relative}")
    for role in ("Player", "Library"):
        if not (ROOT / f"Prism.{role}.Android/keystore.properties").is_file():
            raise RuntimeError(f"Missing Prism.{role}.Android/keystore.properties; signed releases only")
    if (ARTIFACTS / args.version).exists():
        raise RuntimeError(f"Release {args.version} already exists; choose another version or move it aside")
    return sdk


def download(spec):
    cache = ARTIFACTS / "cache" / spec["url"].rsplit("/", 1)[1]
    cache.parent.mkdir(parents=True, exist_ok=True)
    if not cache.exists():
        print(f"Downloading {spec['url']}", flush=True)
        partial = cache.with_suffix(cache.suffix + ".part")
        request = urllib.request.Request(spec["url"], headers={"User-Agent": "Prism-release"})
        with urllib.request.urlopen(request, timeout=120) as response, partial.open("wb") as target:
            shutil.copyfileobj(response, target)
        if sha256(partial) != spec["sha256"]:
            partial.unlink()
            raise RuntimeError("FFmpeg download checksum mismatch")
        partial.rename(cache)
    if sha256(cache) != spec["sha256"]:
        raise RuntimeError(f"FFmpeg cache checksum mismatch: {cache}; remove the corrupt file and retry")
    return cache


def ffmpeg(spec, target):
    archive = download(spec)
    target.mkdir()
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            source.extractall(target)
    else:
        with tarfile.open(archive) as source:
            source.extractall(target, filter="data")
    roots = [p for p in target.iterdir() if p.is_dir()]
    if len(roots) != 1 or not (roots[0] / "bin").is_dir():
        raise RuntimeError("Unexpected FFmpeg archive layout")
    return roots[0]


def publish(project, rid, target, version):
    run("dotnet", "publish", ROOT / project / f"{project}.csproj", "-c", "Release",
        "-r", rid, "--self-contained", "false", "-o", target,
        f"-p:Version={version}", "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None",
        "-p:DebugSymbols=false")
    # Development-only configuration must never override release defaults.
    for file in target.glob("appsettings.*.json"):
        file.unlink()


def add_ffmpeg(source, target, windows):
    target.mkdir()
    extension = ".exe" if windows else ""
    for tool in ("ffmpeg", "ffprobe"):
        binary = source / "bin" / (tool + extension)
        if not binary.is_file():
            raise RuntimeError(f"Missing FFmpeg binary: {binary}")
        shutil.copy2(binary, target)
    # Retain supplier license, notices and presets alongside binaries.
    for file in source.iterdir():
        if file.name == "bin":
            continue
        if file.is_dir():
            shutil.copytree(file, target / file.name)
        else:
            shutil.copy2(file, target)


def pack(source, destination, linux):
    if linux:
        def permissions(info):
            executable = info.name.endswith(("/Prism.Host.Console", "/Prism.Library.Console",
                                              "/ffmpeg/ffmpeg", "/ffmpeg/ffprobe", "/start.sh"))
            info.mode = 0o755 if info.isdir() or executable else 0o644
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            return info
        with tarfile.open(destination, "w:gz") as archive:
            archive.add(source, arcname=source.name, filter=permissions)
    else:
        with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(source.rglob("*")):
                if path.is_file():
                    archive.write(path, path.relative_to(source))


def instructions(target, role, linux):
    executable = f"Prism.{role.title()}.Console" + ("" if linux else ".exe")
    shutil.copy2(ROOT / "LICENSE", target)
    text = (f"Prism {role.title()}\n\n"
            "Requires ASP.NET Core Runtime 10 x64 (installed separately).\n"
            "https://dotnet.microsoft.com/download/dotnet/10.0\n"
            "Edit appsettings.json before starting. Keep your settings, data and logs when upgrading.\n")
    if role == "host":
        text += "Set Player.MediaDirectories to your media folders. FFmpeg is bundled in ffmpeg/.\n"
        if not linux:
            text += "Launcher also requires .NET Desktop Runtime 10 x64. Start launcher/Prism.Launcher.exe.\n"
    else:
        text += "Set Hosts and Mqtt for your network. Open http://localhost:8081 after starting.\n"
    if linux:
        (target / "start.sh").write_text(
            f'#!/bin/sh\nset -eu\ncd -- "$(dirname -- "$0")"\nexec ./{executable} "$@"\n', encoding="utf-8", newline="\n")
        (target / f"prism-{role}.service").write_text(
            f"[Unit]\nDescription=Prism {role.title()}\nAfter=network-online.target\nWants=network-online.target\n\n"
            f"[Service]\nType=simple\nUser=prism\nGroup=prism\nWorkingDirectory=/opt/prism-{role}\n"
            f"ExecStart=/opt/prism-{role}/start.sh\nRestart=on-failure\nRestartSec=5\n"
            "Environment=ASPNETCORE_ENVIRONMENT=Production\n\n[Install]\nWantedBy=multi-user.target\n",
            encoding="utf-8", newline="\n")
        text += ("Run ./start.sh, or use the included systemd example:\n"
                 "1. Create a system user prism; extract to /opt/prism-" + role + ".\n"
                 "2. Give prism write access to that directory (data/logs), and read access to media.\n"
                 f"3. Copy prism-{role}.service to /etc/systemd/system/.\n"
                 f"4. Run sudo systemctl daemon-reload; sudo systemctl enable --now prism-{role}.\n"
                 "Configure your firewall separately if remote access is needed.\n")
    else:
        (target / "start.cmd").write_text(
            f'@echo off\ncd /d "%~dp0"\n"%~dp0{executable}" %*\n', encoding="utf-8")
        text += "Run start.cmd. The ZIP does not register a Windows service or firewall rules.\n"
    (target / "README.txt").write_text(text, encoding="utf-8")


def build(args, sdk, work):
    output = work / "release"
    output.mkdir()
    manifest = json.loads((ROOT / "scripts/ffmpeg.json").read_text(encoding="utf-8"))
    ffwin = ffmpeg(manifest["win-x64"], work / "ffmpeg-win")
    fflinux = ffmpeg(manifest["linux-x64"], work / "ffmpeg-linux")
    run("npm.cmd", "ci", "--no-audit", "--no-fund", cwd=ROOT / "Prism.Client")
    run("npm.cmd", "run", "build", cwd=ROOT / "Prism.Client")
    web = ROOT / "Prism.Client/dist"
    if not (web / "index.html").is_file():
        raise RuntimeError("Web client did not produce index.html")

    for rid in ("win-x64", "linux-x64"):
        for role in ("host", "library"):
            name = f"prism-{role}-{args.version}-{rid}"
            target = work / name
            publish(f"Prism.{role.title()}.Console", rid, target, args.version)
            if role == "host":
                # The development console configuration contains a developer's local path.
                shutil.copy2(ROOT / "Prism.Host.Service/appsettings.json", target / "appsettings.json")
                add_ffmpeg(ffwin if rid == "win-x64" else fflinux, target / "ffmpeg", rid == "win-x64")
                if rid == "win-x64":
                    publish("Prism.Launcher", rid, target / "launcher", args.version)
            else:
                shutil.copytree(web, target / "wwwroot")
            instructions(target, role, rid == "linux-x64")
            pack(target, output / (name + (".zip" if rid == "win-x64" else ".tar.gz")), rid == "linux-x64")

    for role in ("host", "library"):
        setup = f"Prism.{role.title()}.Setup"
        msi_output = work / (setup + "-output")
        run("dotnet", "build", ROOT / setup / (setup + ".wixproj"), "-c", "Release",
            "-t:Rebuild", f"-p:ReleaseVersion={args.version}", f"-p:Version={args.version}",
            f"-p:OutputPath={msi_output}{os.sep}", f"-p:FFmpegBinDir={ffwin / 'bin'}",
            "-p:SkipWebClientBuild=true", "-p:SelfContained=false", "-p:DebugType=None")
        packages = list(msi_output.rglob("*.msi"))
        if len(packages) != 1:
            raise RuntimeError(f"Expected one MSI for {role}, got {len(packages)}")
        shutil.copy2(packages[0], output / f"prism-{role}-{args.version}-win-x64.msi")

    for role in ("player", "library"):
        project = ROOT / f"Prism.{role.title()}.Android"
        run(project / "gradlew.bat", "--no-daemon", "clean", "assembleRelease",
            f"-PreleaseVersion={args.version}", f"-PreleaseVersionCode={args.android_version_code}", cwd=project)
        apk = project / "app/build/outputs/apk/release/app-release.apk"
        if not apk.is_file():
            raise RuntimeError(f"No signed APK produced for {role}")
        run(sdk / "build-tools/36.0.0/apksigner.bat", "verify", apk)
        metadata = run(sdk / "build-tools/36.0.0/aapt.exe", "dump", "badging", apk, capture=True)
        if (f"versionCode='{args.android_version_code}'" not in metadata or
                f"versionName='{args.version}'" not in metadata or f"name='org.prism.{role}'" not in metadata):
            raise RuntimeError(f"Incorrect APK metadata for {role}")
        shutil.copy2(apk, output / f"prism-{role}-{args.version}-android.apk")

    files = sorted(output.iterdir())
    if len(files) != 8 or any(p.stat().st_size == 0 for p in files):
        raise RuntimeError("Incomplete release: expected eight non-empty packages")
    (output / "SHA256SUMS.txt").write_text(
        "".join(f"{sha256(p)}  {p.name}\n" for p in files), encoding="ascii")
    # Publish only the complete set; an interrupted build never looks like a release.
    output.rename(ARTIFACTS / args.version)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, type=version_value)
    parser.add_argument("--android-version-code", required=True, type=version_code,
                        help="Must exceed the versionCode of all previously distributed APKs")
    parser.add_argument("--java-home", type=Path, help="JDK 25 directory; otherwise JAVA_HOME/PATH")
    parser.add_argument("--android-sdk", type=Path, help="Android SDK directory")
    parser.add_argument("--check", action="store_true", help="Check local prerequisites without building")
    args = parser.parse_args()
    commit = check_release_git(args.version)
    sdk = preflight(args)
    if args.check:
        print("Local prerequisites found; no build performed.")
        return
    ARTIFACTS.mkdir(exist_ok=True)
    # Unique staging avoids stale files and never deletes user-selected directories.
    work = Path(tempfile.mkdtemp(prefix="build-", dir=ARTIFACTS))
    print(f"Staging directory: {work}", flush=True)
    build(args, sdk, work)
    try:
        tag_release(args.version, commit)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        raise RuntimeError(
            f"Packages are ready at {ARTIFACTS / args.version}, but tagging failed: {error}. "
            "Packages have been retained; verify the source commit before tagging manually."
        ) from error
    print(f"Release complete: {ARTIFACTS / args.version}")
    print(f"Local Git tag created: v{args.version} ({commit}); not pushed")
    print(f"Build intermediates retained for diagnostics: {work}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError, tarfile.TarError, zipfile.BadZipFile) as error:
        print(f"Release failed: {error}", file=sys.stderr)
        sys.exit(1)
