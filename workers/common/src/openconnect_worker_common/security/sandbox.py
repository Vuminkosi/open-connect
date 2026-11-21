"""Minimum Linux media-sandbox checks, not a complete sandbox attestation."""

import os
from pathlib import Path


def restricted_media_sandbox_ready() -> bool:
    """Fail closed unless minimum runtime restrictions are observable."""
    try:
        if os.name != "posix" or not hasattr(os, "geteuid") or os.geteuid() == 0:
            return False
        status = Path("/proc/self/status").read_text(encoding="utf-8")
        fields = dict(line.split(":", 1) for line in status.splitlines() if ":" in line)
        if fields.get("NoNewPrivs", "").strip() != "1" or fields.get("Seccomp", "").strip() != "2":
            return False
        if set(os.listdir("/sys/class/net")) != {"lo"}:
            return False
        if not (os.statvfs("/").f_flag & os.ST_RDONLY):
            return False
        memory = Path("/sys/fs/cgroup/memory.max").read_text().strip()
        processes = Path("/sys/fs/cgroup/pids.max").read_text().strip()
        return 0 < int(memory) <= 2 * 1024**3 and 0 < int(processes) <= 128
    except (OSError, ValueError, AttributeError):
        return False
