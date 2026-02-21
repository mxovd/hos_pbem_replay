#!/usr/bin/env python3
"""Build and package the pbem_replay mod for distribution."""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path
from typing import NoReturn
from urllib import error as urlerror
from urllib import request as urlrequest

MOD_NAME = "pbem_replay"
PACKAGE_PREFIX = "pbem-replay"
PROJECT_FILENAME = "PbemReplay.csproj"
OUTPUT_DLL_NAME = "PbemReplay.dll"
MOD_FOLDER_NAME = "pbem_replay"

REQUIRED_DLLS = [
    "Assembly-CSharp.dll",
    "Newtonsoft.Json.dll",
    "PhotonUnityNetworking.dll",
    "LeTai.TranslucentImage.dll",
    "Unity.TextMeshPro.dll",
    "UnityEngine.dll",
    "UnityEngine.AudioModule.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.ImageConversionModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll",
]


HARMONY_PACKAGE_ID = "lib.harmony.thin"
NUGET_FLAT_BASE_URL = "https://api.nuget.org/v3-flatcontainer"


def _apply_env_file(env_path: Path) -> None:
    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue

        if "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip()

        if not key:
            continue

        if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
            value = value[1:-1]

        os.environ.setdefault(key, value)


def load_env_overrides() -> None:
    """Load .env files located next to this script or its parent directory."""

    script_dir = Path(__file__).resolve().parent
    search_dirs = [script_dir]
    parent_dir = script_dir.parent
    if parent_dir != script_dir:
        search_dirs.append(parent_dir)

    seen: set[Path] = set()
    for directory in search_dirs:
        if directory in seen:
            continue
        seen.add(directory)
        env_path = directory / ".env"
        if env_path.exists():
            _apply_env_file(env_path)


load_env_overrides()


def _raise_missing_path(description: str, env_var: str, candidates: list[Path]) -> NoReturn:
    lines = "\n".join(f" - {candidate}" for candidate in candidates) or " - (no predefined locations)"
    raise SystemExit(
        f"Could not locate {description}.\n"
        f"Checked the following paths:\n{lines}\n"
        f"Set {env_var} in your environment or in the closest .env file to override the detection."
    )


def determine_mod_install_path() -> Path:
    """Return the most likely Hex of Steel MODS directory.

    Preference order:
    1. Environment variable ``HOS_MODS_PATH`` if defined.
    2. Flatpak Steam path (default on Linux/Steam Deck).
    3. Native Linux config path (Unity3D under ~/.config).
    4. Windows LocalLow path.
    5. Fallback directory under ~/Hex of Steel/MODS.
    """

    candidates: list[Path] = []
    env_path = os.environ.get("HOS_MODS_PATH")
    if env_path:
        candidates.append(Path(env_path).expanduser())

    flatpak_path = (
        Path.home()
        / ".var"
        / "app"
        / "com.valvesoftware.Steam"
        / "config"
        / "unity3d"
        / "War Frogs Studio"
        / "Hex of Steel"
        / "MODS"
    )
    native_linux_path = (
        Path.home()
        / ".config"
        / "unity3d"
        / "War Frogs Studio"
        / "Hex of Steel"
        / "MODS"
    )
    macos_path = (
        Path.home()
        / "Library"
        / "Application Support"
        / "War Frogs Studio"
        / "Hex of Steel"
        / "MODS"
    )
    windows_locallow_path = (
        Path.home()
        / "AppData"
        / "LocalLow"
        / "War Frogs Studio"
        / "Hex of Steel"
        / "MODS"
    )
    fallback_path = Path.home() / "Hex of Steel" / "MODS"

    candidates.extend(
        [flatpak_path, native_linux_path, macos_path, windows_locallow_path, fallback_path]
    )

    # Remove duplicates while preserving order
    unique_candidates: list[Path] = []
    seen = set()
    for path in candidates:
        resolved = path.expanduser()
        if resolved not in seen:
            unique_candidates.append(resolved)
            seen.add(resolved)

    existing_candidates: list[Path] = []
    for path in unique_candidates:
        if path.exists() and path.is_dir():
            existing_candidates.append(path)

    if existing_candidates:
        def candidate_sort_key(path: Path) -> tuple[float, float]:
            assembly_path = path / "Assembly-CSharp.dll"
            if assembly_path.exists():
                return (assembly_path.stat().st_mtime, path.stat().st_mtime)
            return (0.0, path.stat().st_mtime)

        return max(existing_candidates, key=candidate_sort_key)

    _raise_missing_path(
        "the Hex of Steel MODS directory",
        "HOS_MODS_PATH",
        unique_candidates,
    )


def determine_game_managed_dir() -> Path:
    """Return the most likely Hex of Steel Managed directory.

    Honors ``HOS_MANAGED_DIR`` first, then common Steam install paths across
    Linux (Flatpak and native), macOS, and Windows, finally falling back to a
    directory under the user's home folder.
    """

    candidates: list[Path] = []
    env_path = os.environ.get("HOS_MANAGED_DIR")
    if env_path:
        candidates.append(Path(env_path).expanduser())

    flatpak_managed = Path.home() / (
        ".var/app/com.valvesoftware.Steam/data/Steam/steamapps/common/Hex of Steel/"
        "Hex of Steel_Data/Managed"
    )
    native_linux_managed = Path.home() / (
        ".steam/steam/steamapps/common/Hex of Steel/Hex of Steel_Data/Managed"
    )
    linux_local_share = Path.home() / (
        ".local/share/Steam/steamapps/common/Hex of Steel/Hex of Steel_Data/Managed"
    )
    steam_library_under_home = Path.home() / (
        "SteamLibrary/steamapps/common/Hex of Steel/Hex of Steel_Data/Managed"
    )
    mac_app_bundle = Path("/Applications/Hex of Steel.app/Contents/Resources/Data/Managed")
    mac_steam_bundle = (
        Path.home()
        / "Library"
        / "Application Support"
        / "Steam"
        / "steamapps"
        / "common"
        / "Hex of Steel"
        / "Hex of Steel.app"
        / "Contents"
        / "Resources"
        / "Data"
        / "Managed"
    )
    mac_config_copy = (
        Path.home()
        / "Library"
        / "Application Support"
        / "War Frogs Studio"
        / "Hex of Steel"
        / "Hex of Steel_Data"
        / "Managed"
    )

    windows_roots = [
        os.environ.get("ProgramFiles(x86)"),
        os.environ.get("ProgramFiles"),
        "C:/Program Files (x86)",
    ]
    windows_candidates = []
    for root in windows_roots:
        if not root:
            continue
        windows_candidates.append(
            Path(root)
            / "Steam"
            / "steamapps"
            / "common"
            / "Hex of Steel"
            / "Hex of Steel_Data"
            / "Managed"
        )

    fallback_path = Path.home() / "Hex of Steel_Data" / "Managed"

    candidates.extend(
        [
            flatpak_managed,
            native_linux_managed,
            linux_local_share,
            steam_library_under_home,
            mac_app_bundle,
            mac_steam_bundle,
            mac_config_copy,
            *windows_candidates,
            fallback_path,
        ]
    )

    unique_candidates: list[Path] = []
    seen = set()
    for path in candidates:
        resolved = Path(path).expanduser()
        if resolved not in seen:
            unique_candidates.append(resolved)
            seen.add(resolved)

    for path in unique_candidates:
        if path.exists() and path.is_dir():
            return path

    _raise_missing_path(
        "the Hex of Steel Managed directory",
        "HOS_MANAGED_DIR",
        unique_candidates,
    )


def download_latest_harmony() -> tuple[str, bytes]:
    metadata_url = f"{NUGET_FLAT_BASE_URL}/{HARMONY_PACKAGE_ID}/index.json"
    try:
        with urlrequest.urlopen(metadata_url, timeout=30) as response:
            metadata = json.load(response)
    except urlerror.URLError as error:
        raise SystemExit(
            f"Failed to query Harmony versions from {metadata_url}: {error}"
        ) from error

    versions = metadata.get("versions")
    if not versions:
        raise SystemExit("No versions returned for lib.harmony.thin from NuGet")

    version = versions[-1]
    package_url = (
        f"{NUGET_FLAT_BASE_URL}/{HARMONY_PACKAGE_ID}/{version}/"
        f"{HARMONY_PACKAGE_ID}.{version}.nupkg"
    )

    try:
        with urlrequest.urlopen(package_url, timeout=60) as response:
            package_bytes = response.read()
    except urlerror.URLError as error:
        raise SystemExit(
            f"Failed to download Harmony package {package_url}: {error}"
        ) from error

    with zipfile.ZipFile(io.BytesIO(package_bytes)) as archive:
        candidates = [
            name
            for name in archive.namelist()
            if name.lower().endswith("0harmony.dll") and name.startswith("lib/")
        ]
        if not candidates:
            raise SystemExit("Harmony package did not contain 0Harmony.dll")

        preferred_targets = [
            "lib/net48/",
            "lib/net472/",
            "lib/net452/"
        ]

        def select_key(path: str) -> tuple[int, int]:
            lower = path.lower()
            for index, marker in enumerate(preferred_targets):
                if marker in lower:
                    return (index, len(path))
            return (len(preferred_targets), len(path))

        selected = min(candidates, key=select_key)
        dll_bytes = archive.read(selected)

    return version, dll_bytes


def ensure_harmony_library(libraries_dir: Path) -> None:
    harmony_path = libraries_dir / "0Harmony.dll"
    if harmony_path.exists():
        return

    libraries_dir.mkdir(parents=True, exist_ok=True)
    version, dll_bytes = download_latest_harmony()
    harmony_path.write_bytes(dll_bytes)
    print(f"Downloaded Harmony {version} to {harmony_path}")


def run(command: list[str], *, cwd: Path) -> None:
    """Execute an external command and raise on failure."""
    subprocess.run(command, cwd=cwd, check=True)


def refresh_libraries(root: Path) -> None:
    managed_dir = determine_game_managed_dir()
    if not managed_dir.exists() or not managed_dir.is_dir():
        raise SystemExit(f"Managed directory not found at {managed_dir}")

    libraries_dir = root / "Libraries"
    libraries_dir.mkdir(parents=True, exist_ok=True)

    ensure_harmony_library(libraries_dir)

    for dll_path in libraries_dir.glob("*.dll"):
        if dll_path.name.lower() != "0harmony.dll":
            dll_path.unlink(missing_ok=True)

    for dll_name in REQUIRED_DLLS:
        source = managed_dir / dll_name
        if not source.exists():
            raise SystemExit(f"Required library {dll_name} missing at {source}")
        shutil.copy2(source, libraries_dir / dll_name)


def compute_package_dir(package_root: Path, mod_version: str) -> Path:
    prefix = f"{PACKAGE_PREFIX}-v{mod_version}-"
    highest_index = 0

    if package_root.exists():
        for entry in package_root.iterdir():
            name = entry.name
            if not name.startswith(prefix):
                continue

            suffix = name[len(prefix):]
            if entry.is_file():
                suffix = suffix.split(".", 1)[0]

            if suffix.isdigit():
                highest_index = max(highest_index, int(suffix))

    next_index = highest_index + 1
    return package_root / f"{prefix}{next_index}"


def parse_args() -> tuple[argparse.Namespace, argparse.ArgumentParser]:
    parser = argparse.ArgumentParser(description=f"Build and package the {MOD_NAME} mod.")
    parser.add_argument(
        "--deploy",
        "-d",
        action="store_true",
        help="Build the project and stage the packaged mod in the package directory.",
    )
    parser.add_argument(
        "--install",
        "-i",
        action="store_true",
        help="After a successful deploy, copy the build into the local Hex of Steel mods directory.",
    )
    parser.add_argument(
        "--get-dlls",
        "-g",
        action="store_true",
        help="Decompile Assembly-CSharp.dll and refresh Libraries from the Hex of Steel installation.",
    )
    parser.add_argument(
        "--refresh-lib",
        "--refresh-libs",
        "-r",
        dest="refresh_libs",
        action="store_true",
        help="Refresh the Libraries directory before building (default is to skip).",
    )
    return parser.parse_args(), parser


def install_package(package_root: Path) -> Path:
    install_root = determine_mod_install_path()
    target_path = install_root / package_root.name

    install_root.mkdir(parents=True, exist_ok=True)
    if target_path.exists():
        shutil.rmtree(target_path)
    shutil.copytree(package_root, target_path)
    return target_path


def build_and_package(root: Path, install: bool, refresh_libs: bool = False) -> None:
    manifest_path = root / "Manifest.json"
    project_path = root / PROJECT_FILENAME
    output_dll = root / "output" / "net48" / OUTPUT_DLL_NAME
    package_root = root / "package"
    assets_dir = root / "assets"

    if refresh_libs:
        refresh_libraries(root)
    else:
        print("Skipping Libraries refresh (use --refresh-libs to enable)")

    if not manifest_path.exists():
        raise SystemExit(f"manifest.json not found at {manifest_path}")

    if not project_path.exists():
        raise SystemExit(f"Project file not found at {project_path}")

    with manifest_path.open(encoding="utf-8") as handle:
        manifest = json.load(handle)

    mod_version = manifest.get("modVersion", "0.0.0")

    package_root.mkdir(parents=True, exist_ok=True)

    package_dir = compute_package_dir(package_root, mod_version)
    target_root = package_dir / MOD_FOLDER_NAME
    libraries_dir = target_root / "Libraries"

    package_dir.mkdir(parents=True, exist_ok=True)
    target_root.mkdir(parents=True, exist_ok=True)
    libraries_dir.mkdir(parents=True, exist_ok=True)
    # Additional directories get created as needed when copying assets.

    run(["dotnet", "build", str(project_path), "--configuration", "Release"], cwd=root)

    if not output_dll.exists():
        raise SystemExit(f"Build completed but DLL missing at {output_dll}")

    shutil.copy2(manifest_path, target_root / "Manifest.json")
    shutil.copy2(output_dll, libraries_dir / output_dll.name)

    if assets_dir.exists():
        shutil.copytree(assets_dir, target_root, dirs_exist_ok=True)

    print(f"Package created at {package_dir}")

    if install:
        installed_path = install_package(target_root)
        print(f"Mod installed to {installed_path}")


def run_decompilation(root: Path) -> Path:
    refresh_libraries(root)

    assembly_path = determine_game_managed_dir() / "Assembly-CSharp.dll"

    print(f"Decompiling from: {assembly_path}")

    if not assembly_path.exists():
        raise SystemExit(f"Assembly-CSharp.dll not found at {assembly_path}")

    tmp_dir = Path(tempfile.mkdtemp(prefix="hos_decompile_", dir="/tmp"))

    try:
        command = [
            "ilspycmd",
            str(assembly_path),
            "-p",
            "-o",
            str(tmp_dir),
        ]
        run(command, cwd=root)

        manager_path = tmp_dir / "MultiplayerManager.cs"
        if not manager_path.exists():
            candidates = list(tmp_dir.rglob("MultiplayerManager.cs"))
            if not candidates:
                raise SystemExit("MultiplayerManager.cs not found in decompilation output")
            manager_path = candidates[0]

        content = manager_path.read_text(encoding="utf-8", errors="ignore")
        match = re.search(r"VERSION\s*=\s*\"([^\"]+)\"", content)
        if not match:
            raise SystemExit("VERSION attribute not found in MultiplayerManager.cs")

        version = match.group(1)
        dest_dir = root / "decompiled" / version

        if dest_dir.exists():
            shutil.rmtree(dest_dir)
        dest_dir.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(tmp_dir, dest_dir)

        return dest_dir
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    try:
        args, parser = parse_args()
        root = Path(__file__).resolve().parent

        if args.install and not args.deploy:
            print("--install is ignored unless --deploy is also specified.")

        performed_action = False

        if args.get_dlls:
            decompiled_path = run_decompilation(root)
            print(f"Assembly decompiled to {decompiled_path}")
            performed_action = True

        if args.deploy:
            build_and_package(root, args.install, args.refresh_libs)
            performed_action = True

        if not performed_action:
            parser.print_help()
            sys.exit(0)
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.returncode) from error
