"""Docker lifecycle for one run-owned Jellyfin verification instance.

Every container and volume carries the label jfedl.run=<run id>. Cleanup
removes only resources whose label matches the recorded run ID.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path

DOCKER = shutil.which("docker") or "/usr/local/bin/docker"
SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0"
JELLYFIN_IMAGE = "jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5"
JELLYFIN_TAG = "jellyfin/jellyfin:12.0.20260908-012347"
FFMPEG = "/usr/lib/jellyfin-ffmpeg/ffmpeg"
PLUGIN_DIR = "Jellyfin.Plugin.Edl"
PLUGIN_FILES = ("Jellyfin.Plugin.Edl.dll", "Jellyfin.Plugin.Edl.deps.json", "Jellyfin.Plugin.Edl.pdb", "Jellyfin.Plugin.Edl.xml")
SCRATCH_PREFIX = "jfedl-verify-"
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


def new_scratch() -> Path:
    return Path(tempfile.mkdtemp(prefix=SCRATCH_PREFIX)).resolve()


def user_flag() -> str:
    return f"{os.getuid()}:{os.getgid()}"


def sdk_run(run_id: str, repo: Path, mounts: list, script: str, log: Path) -> int:
    """Run a bash script in a disposable SDK container over a read-only copy of the checkout."""
    args = ["run", "--rm", "--name", f"{run_id}-sdk", "--label", f"{LABEL}={run_id}", "--user", user_flag(),
            "-e", "HOME=/tmp", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
            "-v", f"{repo}:/src:ro"]
    for host, container in mounts:
        args += ["-v", f"{host}:{container}"]
    args += [SDK_IMAGE, "bash", "-c", f"set -eo pipefail; {COPY_SOURCE}; {script}"]
    return docker(*args, check=False, log=log).returncode


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
    """Remove only this run's containers, volumes, and scratch directory. Safe to repeat."""
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
        if not scratch.exists():
            return "absent"
        if not scratch.name.startswith(SCRATCH_PREFIX) or scratch.parent != Path(tempfile.gettempdir()).resolve():
            raise RuntimeError("recorded scratch path is not a run-owned temp directory")
        for path in scratch.rglob("*"):
            if not path.is_symlink():
                path.chmod(0o700 if path.is_dir() else 0o600)
        shutil.rmtree(scratch)
        return "removed"

    step("containers", remove_containers)
    step("volumes", remove_volumes)
    step("scratch", remove_scratch)
    leftovers = {
        "containers": docker("ps", "-aq", "--filter", f"label={LABEL}={run_id}", check=False).stdout.split(),
        "volumes": docker("volume", "ls", "-q", "--filter", f"label={LABEL}={run_id}", check=False).stdout.split(),
        "scratchExists": Path(run["scratch"]).exists(),
    }
    clean = not errors and not leftovers["containers"] and not leftovers["volumes"] and not leftovers["scratchExists"]
    return {"steps": steps, "errors": errors, "leftovers": leftovers, "clean": clean}
