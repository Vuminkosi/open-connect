"""In-memory worker task registry with lease fencing."""

from __future__ import annotations

import threading
import time
import uuid


class TaskNotFound(Exception):
    pass


class TaskConflict(Exception):
    pass


class LeaseConflict(Exception):
    def __init__(self, expected_fencing_token: int) -> None:
        super().__init__("stale lease fencing token")
        self.expected_fencing_token = expected_fencing_token


class TaskRegistry:
    """Tracks claimed worker tasks; leases guard against stale workers."""

    def __init__(self, allowed_kinds: frozenset[str]) -> None:
        self._allowed = allowed_kinds
        self._lock = threading.Lock()
        self._tasks: dict[str, dict[str, object]] = {}
        self._counter = 0

    def is_supported(self, kind: str) -> bool:
        return kind in self._allowed

    def claim(self, task_id: str, kind: str) -> dict[str, object]:
        if kind not in self._allowed:
            raise TaskConflict(f"unsupported task kind {kind!r}")
        with self._lock:
            existing = self._tasks.get(task_id)
            if existing is not None and existing["state"] in {"claimed", "heartbeat"}:
                raise TaskConflict(f"task {task_id!r} already claimed")
            self._counter += 1
            record = {
                "taskId": task_id,
                "kind": kind,
                "leaseId": str(uuid.uuid4()),
                "fencingToken": self._counter,
                "state": "claimed",
                "result": None,
                "error": None,
                "updatedAt": time.time(),
            }
            self._tasks[task_id] = record
            return dict(record)

    def _checked(self, task_id: str, lease_id: str, fencing_token: int) -> dict[str, object]:
        record = self._tasks.get(task_id)
        if record is None:
            raise TaskNotFound(task_id)
        if record["leaseId"] != lease_id or record["fencingToken"] != fencing_token:
            raise LeaseConflict(int(record["fencingToken"]))
        if record["state"] not in {"claimed", "heartbeat"}:
            raise TaskConflict(f"task {task_id!r} is {record['state']}")
        return record

    def heartbeat(self, task_id: str, lease_id: str, fencing_token: int) -> dict[str, object]:
        with self._lock:
            record = self._checked(task_id, lease_id, fencing_token)
            record["state"] = "heartbeat"
            record["updatedAt"] = time.time()
            return dict(record)

    def complete(
        self, task_id: str, lease_id: str, fencing_token: int, result: object = None
    ) -> dict[str, object]:
        with self._lock:
            record = self._checked(task_id, lease_id, fencing_token)
            record["state"] = "completed"
            record["result"] = result
            record["updatedAt"] = time.time()
            return dict(record)

    def fail(
        self, task_id: str, lease_id: str, fencing_token: int, error: object = None
    ) -> dict[str, object]:
        with self._lock:
            record = self._checked(task_id, lease_id, fencing_token)
            record["state"] = "failed"
            record["error"] = error
            record["updatedAt"] = time.time()
            return dict(record)

    def get(self, task_id: str) -> dict[str, object]:
        with self._lock:
            record = self._tasks.get(task_id)
            if record is None:
                raise TaskNotFound(task_id)
            return dict(record)
