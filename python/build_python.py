"""将 Python 版打包为带版本号的 Windows EXE。"""
import hashlib
import json
import msvcrt
import os
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent
STATE = ROOT / "python-version.json"
DIST = ROOT / "dist"
ICON = ROOT.parent / "csharp" / "app.ico"
INPUTS = ("codex_tray.py", "windows_ui.py", "build_python.py", "requirements.txt", "../csharp/app.ico")


def input_hash():
    digest = hashlib.sha256()
    for relative in INPUTS:
        content = (ROOT / relative).read_bytes()
        if not relative.endswith(".ico"):
            content = content.replace(b"\r\n", b"\n")
        digest.update(relative.encode("ascii") + b":" + hashlib.sha256(content).digest())
    return digest.hexdigest()


def write_version_resource(path, version, filename):
    numbers = tuple(map(int, version.split("."))) + (0,)
    path.write_text(f"""VSVersionInfo(
  ffi=FixedFileInfo(filevers={numbers}, prodvers={numbers},
                    mask=0x3f, flags=0, OS=0x40004, fileType=1, subtype=0, date=(0, 0)),
  kids=[StringFileInfo([StringTable('040904b0', [
    StringStruct('ProductName', 'Codex Usage Monitor (Python)'),
    StringStruct('FileDescription', 'Codex Usage Monitor (Python)'),
    StringStruct('FileVersion', '{version}'),
    StringStruct('ProductVersion', '{version}'),
    StringStruct('OriginalFilename', '{filename}'),
  ])]), VarFileInfo([VarStruct('Translation', [1033, 1200])])])
""", encoding="utf-8")


def build():
    state = json.loads(STATE.read_text(encoding="utf-8"))
    digest = input_hash()
    version = state["version"]
    if state["input_hash"] and state["input_hash"] != digest:
        major, minor, patch = map(int, version.split("."))
        version = f"{major}.{minor}.{patch + 1}"
    name = f"CodexUsageMonitor-Python-{version}"
    package = DIST / f"{name}.exe"
    if state["input_hash"] == digest and package.is_file():
        return package

    with tempfile.TemporaryDirectory(prefix=".build-", dir=DIST) as directory:
        staging = Path(directory)
        version_data = staging / STATE.name
        version_data.write_text(json.dumps({"version": version, "input_hash": digest}, indent=2) + "\n",
                                encoding="utf-8")
        resource = staging / "version.txt"
        write_version_resource(resource, version, package.name)
        subprocess.run([
            sys.executable, "-m", "PyInstaller", "--noconfirm", "--onefile", "--windowed",
            "--name", name, "--icon", str(ICON), "--version-file", str(resource),
            "--add-data", f"{version_data};.", "--distpath", str(staging),
            "--specpath", str(staging), "--workpath", str(staging / "work"),
            str(ROOT / "codex_tray.py"),
        ], cwd=ROOT, check=True)
        if input_hash() != digest:
            raise RuntimeError("打包期间输入文件已变化，请重新打包")
        if package.exists():
            raise FileExistsError(package)
        if state["input_hash"] != digest:
            os.replace(version_data, STATE)
        os.rename(staging / package.name, package)
    return package


def main():
    DIST.mkdir(exist_ok=True)
    with (DIST / ".build.lock").open("a+b") as lock:
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        try:
            package = build()
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
    print(f"已打包：{package}")


if __name__ == "__main__":
    main()
