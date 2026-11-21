"""Explicit bootstrap for a private, versioned MinIO bucket."""

from __future__ import annotations

import os
import sys

from minio import Minio
from minio.error import S3Error
from minio.helpers import check_bucket_name
from minio.versioningconfig import VersioningConfig

from openconnect_knowledge.minio_resolver import storage_pool


def ensure_bucket(client: Minio, bucket: str) -> bool:
    """Create a bucket if absent, without changing an existing bucket's configuration."""
    check_bucket_name(bucket)
    created = not client.bucket_exists(bucket)
    if created:
        # A concurrent creator may win: fail rather than alter a bucket we did not create.
        client.make_bucket(bucket)
        client.set_bucket_versioning(bucket, VersioningConfig("Enabled"))
    if client.get_bucket_versioning(bucket).status != "Enabled":
        raise RuntimeError("bucket_versioning_required")
    try:
        # An existing policy needs independent review; this bootstrap does not change it.
        client.get_bucket_policy(bucket)
    except S3Error as error:
        if error.code != "NoSuchBucketPolicy":
            raise
    else:
        raise RuntimeError("bucket_policy_requires_review")
    return created


def main() -> int:
    try:
        endpoint = os.environ["OC_STORAGE_BOOTSTRAP_ENDPOINT"]
        access_key = os.environ["OC_STORAGE_BOOTSTRAP_ACCESS_KEY"]
        secret_key = os.environ["OC_STORAGE_BOOTSTRAP_SECRET_KEY"]
        bucket = os.environ["OC_STORAGE_BOOTSTRAP_BUCKET"]
        tls = os.environ.get("OC_STORAGE_BOOTSTRAP_TLS", "true").lower()
        if (not endpoint or any(char in endpoint for char in "/@") or "://" in endpoint
                or not access_key or not secret_key or tls not in {"true", "false"}):
            raise ValueError("invalid_storage_configuration")
        pool = storage_pool(tls=tls == "true", ca_file=os.environ.get("OC_STORAGE_BOOTSTRAP_CA_FILE"))
        client = Minio(endpoint, access_key=access_key, secret_key=secret_key,
                       secure=tls == "true", http_client=pool)
        created = ensure_bucket(client, bucket)
    except Exception:
        print("storage_bootstrap_failed", file=sys.stderr)
        return 1
    print("storage_bucket_created" if created else "storage_bucket_verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
