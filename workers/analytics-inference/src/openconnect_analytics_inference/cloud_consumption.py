"""Analytics specialization of the shared Cloud claim consumer."""

from pathlib import Path

from openconnect_worker_common.security.cloud_claims import CloudClaimConsumer


class AnalyticsClaimConsumer(CloudClaimConsumer):
    """Consume analytics claims once over deployment-authenticated TLS."""

    def __init__(self, origin: str, ca_file: Path, cert_file: Path, key_file: Path, timeout: float = 5):
        super().__init__(origin, ca_file, cert_file, key_file, timeout, capability="analytics")
