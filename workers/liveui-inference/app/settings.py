import os
from dataclasses import dataclass
from pathlib import Path


def _worker_token() -> str:
    value = os.getenv("OPENCONNECT_WORKER_TOKEN", "")
    file_name = os.getenv("OPENCONNECT_WORKER_TOKEN_FILE", "")
    if not file_name:
        return value
    if value or not Path(file_name).is_absolute():
        return ""
    try:
        with Path(file_name).open("rb") as source:
            raw = source.read(515)
        if raw.endswith(b"\r\n"):
            raw = raw[:-2]
        elif raw.endswith(b"\n"):
            raw = raw[:-1]
        return raw.decode("ascii") if 1 <= len(raw) <= 512 and all(33 <= item <= 126 for item in raw) else ""
    except (OSError, ValueError):
        return ""


@dataclass(frozen=True)
class Settings:
    enabled: bool
    model_id: str
    model_revision: str
    model_filename: str
    model_sha256: str
    model_path: Path
    model_context_size: int
    model_threads: int
    max_concurrent: int
    queue_capacity: int
    request_timeout_seconds: int
    bearer_token: str

    @classmethod
    def from_environment(cls) -> "Settings":
        return cls(
            enabled=os.getenv("MODEL_ENABLED", "false").lower() == "true",
            model_id=os.getenv("MODEL_ID", "bartowski/Qwen_Qwen3-0.6B-GGUF"),
            model_revision=os.getenv("MODEL_REVISION", "60b85c0e3d8fe0f6474f406922a26d12aca4550d"),
            model_filename=os.getenv("MODEL_FILENAME", "Qwen_Qwen3-0.6B-Q4_K_M.gguf"),
            model_sha256=os.getenv(
                "MODEL_SHA256", "9acfc1e001311f34b4252001b626f2e466d592a42065f66571bff3790d4e1b14"
            ).lower(),
            model_path=Path(os.getenv("MODEL_PATH", "/models/liveui.gguf")),
            model_context_size=int(os.getenv("MODEL_CONTEXT_SIZE", "2048")),
            model_threads=int(os.getenv("MODEL_THREADS", "4")),
            max_concurrent=int(os.getenv("MAX_CONCURRENT_INFERENCES", "1")),
            queue_capacity=int(os.getenv("QUEUE_CAPACITY", "4")),
            request_timeout_seconds=int(os.getenv("REQUEST_TIMEOUT_SECONDS", "30")),
            bearer_token=_worker_token(),
        )

    def validate(self) -> None:
        cloud_mode = os.getenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "false").lower()
        if cloud_mode not in {"true", "false"}:
            raise ValueError("Invalid Cloud LiveUI mode")
        approved = os.getenv("OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED", "false").lower() == "true"
        reviewed = os.getenv("OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED", "false").lower() == "true"
        if cloud_mode == "true" and (not self.enabled or not approved or not reviewed):
            raise ValueError("Cloud LiveUI requires an enabled and approved model")
        if not self.enabled:
            return
        if not self.bearer_token:
            raise ValueError("OPENCONNECT_WORKER_TOKEN is required when the model worker is enabled")
        if len(self.model_sha256) != 64 or any(c not in "0123456789abcdef" for c in self.model_sha256):
            raise ValueError("MODEL_SHA256 must be a 64 character SHA-256 digest")
        if len(self.model_revision) != 40 or any(c not in "0123456789abcdef" for c in self.model_revision):
            raise ValueError("MODEL_REVISION must be a pinned 40 character repository revision")
        if not self.model_id or "/" not in self.model_id or Path(self.model_filename).name != self.model_filename:
            raise ValueError("Model repository and filename are invalid")
        if not 256 <= self.model_context_size <= 32768 or not 1 <= self.model_threads <= 64:
            raise ValueError("Model context size or CPU thread count is out of bounds")
        if not 1 <= self.max_concurrent <= 4 or not 0 <= self.queue_capacity <= 32:
            raise ValueError("Inference concurrency or queue capacity is out of bounds")
        if not 1 <= self.request_timeout_seconds <= 120:
            raise ValueError("Request timeout is out of bounds")
