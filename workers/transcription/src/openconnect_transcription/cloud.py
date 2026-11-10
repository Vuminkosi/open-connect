"""Outbound cloud leg with local fallback for the transcription worker.

Naming scheme (one scheme, used consistently here and in inference with
its own prefix): per-worker variables ``TRANSCRIPTION_CLOUD_URL``,
``TRANSCRIPTION_CLOUD_API_KEY``, ``TRANSCRIPTION_ROUTE_ORDER``,
``TRANSCRIPTION_CLOUD_TIMEOUT_MS``. A shared URL can be supplied at deploy
time via compose interpolation
(``TRANSCRIPTION_CLOUD_URL: ${WORKER_CLOUD_URL:-}``); the code reads only
the per-worker names.

Semantics:
- Cloud is DISABLED by default: an empty/unset ``INFERENCE_CLOUD_URL`` means
  local-only (current behavior, unchanged).
- ``INFERENCE_ROUTE_ORDER`` accepts ``cloud-first`` (default when cloud is
  configured) or ``local-first``; anything else normalizes to ``local-first``
  with a warning (privacy-safe baseline). When cloud is not configured the
  order is irrelevant: only the local leg runs.
- A cloud leg failure (timeout, unreachable, 5xx/429, bounded to one retry)
  falls back to the other leg. Policy denials (``privacy_blocked``) are
  terminal: no fallback, typed error. All legs exhausted -> typed
  ``model_unavailable`` (never fake success).
- Privacy guardrails (defense-in-depth; the .NET host privacy policy is the
  real gate): cloud calls REQUIRE an explicit allowlisted URL (``https``
  scheme, no credentials, no localhost/loopback/link-local/unspecified
  literal IP; other hostnames/IPs are accepted and the host firewall remains
  authoritative), validated at startup -- invalid URLs fail closed. When
  ``OC_PRIVACY_ZONE=strict-local`` the cloud leg is disabled entirely: any
  cloud attempt returns terminal 503 ``privacy_blocked`` and only the local
  leg may serve.
- Secrets: the cloud API key travels only as an outbound Bearer header. It is
  never logged, never echoed in errors/capabilities/readiness. A configured
  cloud URL without a key fails closed at startup.
"""

from __future__ import annotations

import ipaddress
import json
import logging
import os
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Mapping

logger = logging.getLogger("openconnect_transcription_cloud")

CLOUD_TIMEOUT_DEFAULT_MS = 5000
CLOUD_MAX_ATTEMPTS = 2  # initial attempt + one bounded retry, no sleeps

CLOUD_FIRST = "cloud-first"
LOCAL_FIRST = "local-first"

PRIVACY_ZONE_ENV_VAR = "OC_PRIVACY_ZONE"
PRIVACY_ZONE_STRICT = "strict-local"


class CloudConfigError(ValueError):
    """Startup refusal: cloud misconfigured (bad URL, missing key)."""


class CloudLegFailed(RuntimeError):
    """Transient cloud leg failure: safe to try the next leg."""


class CloudPolicyDenied(RuntimeError):
    """Terminal policy denial: do NOT fall back, return the typed error."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


def _get(env: Mapping[str, str] | None, name: str) -> str | None:
    if env is not None:
        return env.get(name)
    return os.environ.get(name)


def privacy_zone_strict(env: Mapping[str, str] | None = None) -> bool:
    """True only when OC_PRIVACY_ZONE is exactly ``strict-local``."""
    return _get(env, PRIVACY_ZONE_ENV_VAR) == PRIVACY_ZONE_STRICT


def validate_cloud_url(raw: str | None) -> str | None:
    """Allowlist a cloud endpoint URL, or None when unconfigured.

    Raises CloudConfigError for anything that is set but not allowlisted.
    """
    if raw is None or raw.strip() == "":
        return None
    url = raw.strip()
    try:
        parts = urllib.parse.urlsplit(url)
    except ValueError as exc:
        raise CloudConfigError(f"cloud URL is not a valid URL: {exc}") from exc
    if parts.scheme.lower() != "https":
        raise CloudConfigError("cloud URL must use the https scheme")
    if parts.username or parts.password or "@" in parts.netloc:
        raise CloudConfigError("cloud URL must not embed credentials")
    host = parts.hostname
    if not host:
        raise CloudConfigError("cloud URL must include a host")
    if host.lower() in ("localhost",):
        raise CloudConfigError("cloud URL must not target localhost")
    try:
        ip = ipaddress.ip_address(host)
    except ValueError:
        pass  # DNS name: host firewall/allowlist remains authoritative.
    else:
        if ip.is_loopback or ip.is_link_local or ip.is_unspecified:
            raise CloudConfigError("cloud URL must not target loopback/link-local/unspecified IPs")
    return url


def resolve_route_order(raw: str | None, *, cloud_configured: bool) -> str:
    """Normalize the route order. Invalid values fall back to local-first."""
    value = (raw or "").strip().lower()
    if value in (CLOUD_FIRST, LOCAL_FIRST):
        return value
    if value not in ("",):
        logger.warning("Unknown route order %r; using %s", raw, LOCAL_FIRST)
        return LOCAL_FIRST
    return CLOUD_FIRST if cloud_configured else LOCAL_FIRST


def cloud_host(url: str) -> str:
    """Host only, for metadata logging (never log full URLs or payloads)."""
    return urllib.parse.urlsplit(url).hostname or "unknown"


def post_json(
    *,
    url: str,
    payload: dict[str, object],
    api_key: str,
    timeout_s: float,
) -> dict[str, object]:
    """POST JSON to the cloud endpoint. Never raises urllib errors outward.

    Bounded to CLOUD_MAX_ATTEMPTS attempts. Raises CloudLegFailed on timeout,
    unreachable host, 5xx/429, or malformed responses. Logs metadata only:
    host, attempt, timeout, outcome class/status -- never payload content,
    never the API key, never response bodies.
    """
    body = json.dumps(payload).encode("utf-8")
    host = cloud_host(url)
    last_error = "unknown"
    for attempt in range(1, CLOUD_MAX_ATTEMPTS + 1):
        request = urllib.request.Request(
            url,
            data=body,
            # The key travels only in this outbound header; logging below
            # records metadata only and never touches headers or payloads.
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout_s) as response:
                status = response.status
                raw = response.read()
        except urllib.error.HTTPError as exc:
            last_error = f"http-{exc.code}"
            logger.info(
                "Cloud leg attempt %d/%d host=%s timeout_s=%.1f outcome=%s",
                attempt,
                CLOUD_MAX_ATTEMPTS,
                host,
                timeout_s,
                last_error,
            )
            if exc.code == 429 or 500 <= exc.code <= 599:
                continue  # transient: bounded retry, then leg failure
            raise CloudLegFailed(f"cloud leg rejected with status {exc.code}") from exc
        except (urllib.error.URLError, TimeoutError, OSError, ValueError) as exc:
            last_error = type(exc).__name__
            logger.info(
                "Cloud leg attempt %d/%d host=%s timeout_s=%.1f outcome=%s",
                attempt,
                CLOUD_MAX_ATTEMPTS,
                host,
                timeout_s,
                last_error,
            )
            continue  # transient: bounded retry, then leg failure
        logger.info(
            "Cloud leg attempt %d/%d host=%s timeout_s=%.1f outcome=http-%d",
            attempt,
            CLOUD_MAX_ATTEMPTS,
            host,
            timeout_s,
            status,
        )
        try:
            decoded = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise CloudLegFailed("cloud leg returned malformed JSON") from exc
        if not isinstance(decoded, dict):
            raise CloudLegFailed("cloud leg returned malformed JSON")
        return decoded
    raise CloudLegFailed(f"cloud leg unreachable after retries ({last_error})")
