"""Bearer-token identity helpers. Fail closed: missing token denies access."""

import hmac
import os
from dataclasses import dataclass


def extract_bearer(authorization: str | None) -> str | None:
    """Extract the token from an Authorization header value, or None if malformed."""
    if not authorization or not authorization.startswith("Bearer "):
        return None
    token = authorization.removeprefix("Bearer ")
    return token or None


def verify_bearer(authorization: str | None, expected_token: str | None) -> bool:
    """Constant-time bearer check. Returns False when no token is configured."""
    if not expected_token:
        return False
    supplied = extract_bearer(authorization)
    if supplied is None:
        return False
    return hmac.compare_digest(supplied, expected_token)


@dataclass(frozen=True)
class Identity:
    """Configured worker identity for authenticating private worker calls."""

    token: str | None

    @classmethod
    def from_environment(cls, variable: str = "OPENCONNECT_WORKER_TOKEN") -> "Identity":
        raw = os.environ.get(variable)
        return cls(token=raw if raw else None)

    @property
    def is_configured(self) -> bool:
        return self.token is not None

    def verify(self, authorization: str | None) -> bool:
        return verify_bearer(authorization, self.token)
