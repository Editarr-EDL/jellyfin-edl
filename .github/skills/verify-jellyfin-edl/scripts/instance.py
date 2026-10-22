"""Docker lifecycle for one run-owned Jellyfin verification instance.

Every container and volume carries the label jfedl.run=<run id>. Cleanup
removes only resources whose label matches the recorded run ID.
"""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
import tempfile
from pathlib import Path

DOCKER = shutil.which("docker") or "/usr/local/bin/docker"
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0"
OLD_GLIBC_IMAGE = "rockylinux/rockylinux:8.10"
JELLYFIN_IMAGE = "jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5"
JELLYFIN_TAG = "jellyfin/jellyfin:12.0.20260908-012347"
FFMPEG = "/usr/lib/jellyfin-ffmpeg/ffmpeg"
PLUGIN_DIR = "Jellyfin.Plugin.Edl"
PLUGIN_FILES = ("Jellyfin.Plugin.Edl.dll", "Jellyfin.Plugin.Edl.deps.json", "Jellyfin.Plugin.Edl.pdb", "Jellyfin.Plugin.Edl.xml")
SCRATCH_PREFIX = "jfedl-verify-"
SCRATCH_MARKER = ".jfedl-run"
LABEL = "jfedl.run"
COPY_SOURCE = ("mkdir -p /tmp/work && tar -C /src --exclude=.git --exclude=bin --exclude=obj "
               "--exclude=artifacts --exclude=dev -cf - . | tar -C /tmp/work -xf - && cd /tmp/work")


class Blocked(Exception):
    """A runtime prerequisite is missing; the run proves nothing."""


def docker(*args: str, check: bool = True, log: Path | None = None) -> subprocess.CompletedProcess:
    """Run docker with an argv list. Callers never pass secrets as arguments."""
    result = subprocess.run([DOCKER, *args], capture_output=True, text=True, check=False)
    if log is not None:
        with log.open("a") as handle:
            handle.write(f"$ docker {' '.join(args)}\n{result.stdout}{result.stderr}exit={result.returncode}\n")
    if check and result.returncode != 0:
        raise RuntimeError(f"docker {args[0]} failed with exit {result.returncode}: {result.stderr.strip()[-400:]}")
    return result


def preflight(images: list) -> dict:
    """Confirm the daemon and images are present, pulling a missing image once."""
    if not os.path.exists(DOCKER):
        raise Blocked("docker CLI not found")
    if docker("version", "--format", "{{.Server.Version}}", check=False).returncode != 0:
        raise Blocked("docker daemon is not reachable")
    facts = {"pulled": []}
    for image in images:
        if docker("image", "inspect", image, check=False).returncode != 0:
            if docker("pull", image, check=False).returncode != 0:
                raise Blocked(f"image {image} is not cached and could not be pulled")
            facts["pulled"].append(image)
        facts[image] = docker("image", "inspect", "--format", "{{.Id}} {{.Architecture}}", image).stdout.strip()
    return facts


def new_scratch(run_id: str) -> Path:
    """Create a private temp directory marked with the run ID that cleanup requires before removing it."""
    scratch = Path(tempfile.mkdtemp(prefix=SCRATCH_PREFIX)).resolve()
    (scratch / SCRATCH_MARKER).write_text(run_id)
    return scratch


def scratch_identity(scratch: Path) -> dict:
    """Return the device and inode that launch records so cleanup can recognize the directory it created."""
    status = os.lstat(scratch)
    return {"dev": status.st_dev, "inode": status.st_ino}


def check_owned_scratch(scratch: Path, run_id: str, identity: dict | None) -> None:
    """Raise unless scratch is the real temp directory this run created.

    Uses lstat throughout, so a symlinked root or marker is refused without
    following it. The device and inode must equal the identity recorded at
    launch. The marker must name this run, or be missing from an empty
    directory, which is what an interrupted final rmdir leaves behind.
    """
    if not stat.S_ISDIR(os.lstat(scratch).st_mode):
        raise RuntimeError("recorded scratch path is a symlink or not a directory")
    if not scratch.name.startswith(SCRATCH_PREFIX) or scratch.parent != Path(tempfile.gettempdir()).resolve():
        raise RuntimeError("recorded scratch path is not a run-owned temp directory")
    if identity is None or scratch_identity(scratch) != identity:
        raise RuntimeError("recorded scratch path is not the directory this run created")
    marker = scratch / SCRATCH_MARKER
    if not os.path.lexists(marker):
        if any(scratch.iterdir()):
            raise RuntimeError("recorded scratch directory has lost its marker but is not empty")
        return
    if not stat.S_ISREG(os.lstat(marker).st_mode) or marker.read_text() != run_id:
        raise RuntimeError("recorded scratch directory does not carry this run's marker")


def user_flag() -> str:
    return f"{os.getuid()}:{os.getgid()}"


def sdk_run(run_id: str, repo: Path, mounts: list, script: str, log: Path, old_glibc: bool = False) -> int:
    """Run a bash script in a disposable SDK container over a read-only copy of the checkout.

    With old_glibc, the SDK image's dotnet tree is copied into a run-labelled volume and the script
    runs in OLD_GLIBC_IMAGE (glibc 2.28) instead. The volume is removed afterwards.
    """
    args = ["run", "--rm", "--name", f"{run_id}-sdk", "--label", f"{LABEL}={run_id}", "--user", user_flag(),
            "-e", "HOME=/tmp", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
            "-v", f"{repo}:/src:ro"]
    for host, container in mounts:
        args += ["-v", f"{host}:{container}"]
    if not old_glibc:
        return docker(*args, SDK_IMAGE, "bash", "-c", f"set -eo pipefail; {COPY_SOURCE}; {script}", check=False, log=log).returncode
    volume = f"{run_id}-dotnet"
    try:
        docker("volume", "create", "--label", f"{LABEL}={run_id}", volume, log=log)
        docker("run", "--rm", "--name", f"{run_id}-copy", "--label", f"{LABEL}={run_id}", "-v", f"{volume}:/out",
               SDK_IMAGE, "cp", "-a", "/usr/share/dotnet/.", "/out/", log=log)
        args += ["-v", f"{volume}:/usr/share/dotnet:ro", "-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
                 "-e", "PATH=/usr/share/dotnet:/usr/bin:/bin", OLD_GLIBC_IMAGE, "bash", "-c",
                 f"set -eo pipefail; ldd --version | head -1; {COPY_SOURCE}; {script}"]
        return docker(*args, check=False, log=log).returncode
    finally:
        docker("volume", "rm", volume, check=False, log=log)


def build_plugin(run_id: str, repo: Path, scratch: Path, log: Path) -> Path:
    """Build the plugin in Release and stage the same four files as scripts/stage-plugin.sh."""
    out = scratch / "build"
    out.mkdir()
    code = sdk_run(run_id, repo, [(out, "/out")],
                   "dotnet build src/Jellyfin.Plugin.Edl/Jellyfin.Plugin.Edl.csproj -c Release -o /out", log)
    if code != 0:
        raise RuntimeError(f"plugin build failed with exit {code}")
    staged = scratch / "config-seed" / "plugins" / PLUGIN_DIR
    staged.mkdir(parents=True)
    for name in PLUGIN_FILES:
        shutil.copyfile(out / name, staged / name)
    return staged


def generate_video(run_id: str, scratch: Path, log: Path) -> Path:
    """Encode a two-second synthetic test pattern with the Jellyfin image's own FFmpeg."""
    out = scratch / "video"
    out.mkdir()
    docker("run", "--rm", "--name", f"{run_id}-ffmpeg", "--label", f"{LABEL}={run_id}", "--user", user_flag(),
           "--entrypoint", FFMPEG, "-v", f"{out}:/out", JELLYFIN_IMAGE, "-hide_banner", "-loglevel", "error",
           "-f", "lavfi", "-i", "testsrc=duration=2:size=320x180:rate=24", "-c:v", "libx264",
           "-pix_fmt", "yuv420p", "/out/synthetic.mkv", log=log)
    return out / "synthetic.mkv"


def start(run: dict, log: Path) -> int:
    """Create labelled volumes and the container, copy the plugin in, start it, and return the host port."""
    run_id = run["runId"]
    for volume in run["volumes"]:
        docker("volume", "create", "--label", f"{LABEL}={run_id}", volume, log=log)
    docker("create", "--name", run["container"], "--label", f"{LABEL}={run_id}", "-p", "127.0.0.1::8096",
           "-v", f"{run['volumes'][0]}:/config", "-v", f"{run['volumes'][1]}:/cache",
           "-v", f"{run['scratch']}/media:/media:ro", JELLYFIN_IMAGE, log=log)
    docker("cp", f"{run['scratch']}/config-seed/plugins", f"{run['container']}:/config/", log=log)
    docker("start", run["container"], log=log)
    binding = docker("port", run["container"], "8096/tcp", log=log).stdout.split()[0]
    host, port = binding.rsplit(":", 1)
    if host != "127.0.0.1":
        raise RuntimeError(f"container published on {host}, not loopback")
    return int(port)


def owned_volume(name: str, run_id: str) -> bool:
    result = docker("volume", "inspect", "--format", f'{{{{index .Labels "{LABEL}"}}}}', name, check=False)
    return result.returncode == 0 and result.stdout.strip() == run_id


def cleanup(run: dict, log: Path) -> dict:
    """Remove only this run's containers, volumes, and scratch directory. Safe to repeat.

    Scratch holds the run's only copy of its secrets, so it stays until the
    containers and volumes are gone and a retry can still capture and redact logs.
    """
    run_id = run["runId"]
    steps, errors = [], []

    def step(name: str, action) -> None:
        try:
            steps.append({"step": name, "result": action()})
        except Exception as error:  # every step runs even when an earlier one fails
            errors.append(f"{name}: {error}")
            steps.append({"step": name, "result": "error"})

    def remove_containers() -> str:
        ids = docker("ps", "-aq", "--filter", f"label={LABEL}={run_id}", log=log).stdout.split()
        for container_id in ids:
            docker("rm", "-f", container_id, log=log)
        return f"removed {len(ids)}"

    def remove_volumes() -> str:
        removed = [v for v in run["volumes"] if owned_volume(v, run_id)]
        for volume in removed:
            docker("volume", "rm", volume, log=log)
        return f"removed {len(removed)}"

    def remove_scratch() -> str:
        scratch = Path(run["scratch"])
        if not os.path.lexists(scratch):
            return "absent"
        if errors:
            return "kept until containers and volumes are removed"
        check_owned_scratch(scratch, run_id, run.get("scratchIdentity"))
        for child in scratch.iterdir():
            if child.name == SCRATCH_MARKER:
                continue
            if stat.S_ISDIR(os.lstat(child).st_mode):
                shutil.rmtree(child)
            else:
                child.unlink()
        # The marker goes last so a retry after a partial removal can still prove ownership.
        (scratch / SCRATCH_MARKER).unlink(missing_ok=True)
        scratch.rmdir()
        return "removed"

    step("containers", remove_containers)
    step("volumes", remove_volumes)
    step("scratch", remove_scratch)
    leftovers = {
        "containers": docker("ps", "-aq", "--filter", f"label={LABEL}={run_id}", check=False).stdout.split(),
        "volumes": docker("volume", "ls", "-q", "--filter", f"label={LABEL}={run_id}", check=False).stdout.split(),
        "scratchExists": os.path.lexists(run["scratch"]),
    }
    clean = not errors and not leftovers["containers"] and not leftovers["volumes"] and not leftovers["scratchExists"]
    return {"steps": steps, "errors": errors, "leftovers": leftovers, "clean": clean}
