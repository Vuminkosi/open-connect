from types import SimpleNamespace

import pytest
from minio.error import S3Error

from openconnect_knowledge.storage_init import ensure_bucket


class BucketClient:
    def __init__(self, *, exists=False, status=None, policy=False):
        self.exists = exists
        self.status = status
        self.policy = policy
        self.calls = []

    def bucket_exists(self, bucket):
        self.calls.append(("exists", bucket))
        return self.exists

    def make_bucket(self, bucket):
        self.calls.append(("create", bucket))
        self.exists = True

    def set_bucket_versioning(self, bucket, config):
        self.calls.append(("versioning", bucket))
        self.status = config.status

    def get_bucket_versioning(self, bucket):
        return SimpleNamespace(status=self.status)

    def get_bucket_policy(self, bucket):
        if self.policy:
            return "{}"
        raise S3Error("NoSuchBucketPolicy", None, None, None, None, None)


def test_creates_and_verifies_new_versioned_bucket():
    client = BucketClient()
    assert ensure_bucket(client, "private-documents") is True
    assert client.calls == [("exists", "private-documents"), ("create", "private-documents"),
                            ("versioning", "private-documents")]


def test_existing_versioned_bucket_is_not_modified():
    client = BucketClient(exists=True, status="Enabled")
    assert ensure_bucket(client, "private-documents") is False
    assert client.calls == [("exists", "private-documents")]


@pytest.mark.parametrize("status,policy,error", [("Suspended", False, "bucket_versioning_required"),
                                                ("Enabled", True, "bucket_policy_requires_review")])
def test_existing_bucket_with_unsafe_or_unreviewed_state_is_not_changed(status, policy, error):
    client = BucketClient(exists=True, status=status, policy=policy)
    with pytest.raises(RuntimeError, match=error):
        ensure_bucket(client, "private-documents")
    assert client.calls == [("exists", "private-documents")]


def test_invalid_bucket_fails_before_any_storage_call():
    client = BucketClient()
    with pytest.raises(ValueError):
        ensure_bucket(client, "../invalid")
    assert not client.calls
