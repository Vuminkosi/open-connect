"""Optional shared push-relay registry binding company credentials and opaque handles.

In relay mode (ADR-IMSG-003), company gateways NEVER submit raw FCM destination
tokens. They authenticate with out-of-band provisioned tenant credentials and
reference an opaque ``registrationHandle`` bound to ``(tenantId, applicationId)``.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass


@dataclass(frozen=True)
class RelayTenantCredential:
    """Out-of-band provisioned credential for a company gateway using the shared push relay."""

    tenant_id: str
    application_id: str
    bearer_token: str
    signing_keys: dict[str, bytes]


@dataclass(frozen=True)
class RelayRegistration:
    """Opaque handle binding an installation's push token to (tenant_id, application_id)."""

    registration_handle: str
    tenant_id: str
    application_id: str
    provider: str
    target_token: str


class PushRelayRegistry:
    """Thread-safe registry for authorized relay tenants and opaque push registrations."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._tenants: dict[tuple[str, str], RelayTenantCredential] = {}
        self._registrations: dict[str, RelayRegistration] = {}

    def register_tenant(self, credential: RelayTenantCredential) -> None:
        with self._lock:
            self._tenants[(credential.tenant_id, credential.application_id)] = credential

    def get_tenant(self, tenant_id: str, application_id: str) -> RelayTenantCredential | None:
        with self._lock:
            return self._tenants.get((tenant_id, application_id))

    def bind_registration(self, registration: RelayRegistration) -> None:
        with self._lock:
            self._registrations[registration.registration_handle] = registration

    def revoke_registration(self, registration_handle: str) -> None:
        with self._lock:
            self._registrations.pop(registration_handle, None)

    def resolve_for_tenant(
        self,
        *,
        tenant_id: str,
        application_id: str,
        registration_handle: str,
    ) -> RelayRegistration | None:
        """Resolve an opaque registration handle ONLY if bound to the exact (tenant, app)."""
        with self._lock:
            reg = self._registrations.get(registration_handle)
            if reg is None:
                return None
            if reg.tenant_id != tenant_id or reg.application_id != application_id:
                return None
            return reg
