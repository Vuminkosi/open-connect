"""Firebase Cloud Messaging (FCM) HTTP v1 opaque wake-up provider.

Uses only FCM HTTP v1 (never legacy server keys) and strictly allowlisted
``PushWakeupPayload`` fields. No plaintext content, identities, URLs, or tokens
may appear in the outbound notification payload.
"""

from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path

import httpx

from openconnect_imessaging.contracts.models import PushWakeupPayload
from openconnect_imessaging.notifications.providers.base import (
    PushDeliveryResult,
    PushProvider,
    PushProviderStatus,
)

FCM_V1_ENDPOINT_TEMPLATE = "https://fcm.googleapis.com/v1/projects/{project_id}/messages:send"


def build_fcm_v1_body(target_token: str, payload: PushWakeupPayload) -> dict[str, object]:
    """Serialize a strict opaque wake-up payload into an FCM HTTP v1 message body."""
    data: dict[str, str] = {
        "schemaVersion": str(payload.schemaVersion),
        "notificationId": payload.notificationId,
        "profileHandle": payload.profileHandle,
        "eventClass": payload.eventClass,
    }
    android: dict[str, object] = {
        "ttl": f"{payload.ttlSeconds}s",
        "priority": "HIGH" if payload.eventClass == "call" else "NORMAL",
    }
    if payload.collapseKey:
        android["collapse_key"] = payload.collapseKey

    return {
        "message": {
            "token": target_token,
            "notification": {
                "title": payload.displayTitle,
                "body": payload.displayBody,
            },
            "data": data,
            "android": android,
        }
    }


def _validate_service_account_file(path: Path | None) -> bool:
    if path is None or not path.is_file():
        return False
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    return (
        isinstance(raw, dict)
        and raw.get("type") == "service_account"
        and bool(raw.get("project_id"))
        and bool(raw.get("client_email"))
        and bool(raw.get("private_key"))
    )


class FcmPushProvider(PushProvider):
    """FCM HTTP v1 push provider with injectable HTTP transport and token supplier."""

    def __init__(
        self,
        *,
        enabled: bool,
        project_id: str | None = None,
        credentials_path: Path | None = None,
        access_token_provider: Callable[[], str | None] | None = None,
        http_transport: httpx.BaseTransport | None = None,
        timeout_seconds: float = 5.0,
    ) -> None:
        self._enabled = enabled
        self._project_id = project_id.strip() if project_id else None
        self._credentials_path = credentials_path
        self._access_token_provider = access_token_provider
        self._http_transport = http_transport
        self._timeout_seconds = timeout_seconds

    @property
    def provider_id(self) -> str:
        return "fcm"

    @property
    def capability_id(self) -> str:
        return "messaging.push.fcm"

    def is_ready(self) -> bool:
        if not self._enabled or not self._project_id:
            return False
        if self._access_token_provider is not None:
            return bool(self._access_token_provider())
        return _validate_service_account_file(self._credentials_path)

    def send_wakeup(
        self,
        *,
        target_token: str,
        payload: PushWakeupPayload,
    ) -> PushDeliveryResult:
        if not self.is_ready():
            return PushDeliveryResult(
                status=PushProviderStatus.DISABLED,
                reason_code="fcm_not_configured",
            )
        if not target_token or len(target_token) < 16:
            return PushDeliveryResult(
                status=PushProviderStatus.INVALID_TOKEN,
                reason_code="invalid_fcm_token",
            )

        bearer = self._access_token_provider() if self._access_token_provider else None
        if not bearer:
            return PushDeliveryResult(
                status=PushProviderStatus.PROVIDER_UNAVAILABLE,
                reason_code="fcm_oauth_token_unavailable",
            )

        url = FCM_V1_ENDPOINT_TEMPLATE.format(project_id=self._project_id)
        body = build_fcm_v1_body(target_token, payload)
        try:
            with httpx.Client(
                transport=self._http_transport,
                timeout=self._timeout_seconds,
                follow_redirects=False,
            ) as client:
                response = client.post(
                    url,
                    json=body,
                    headers={
                        "Authorization": f"Bearer {bearer}",
                        "Content-Type": "application/json",
                    },
                )
        except httpx.HTTPError:
            return PushDeliveryResult(
                status=PushProviderStatus.PROVIDER_UNAVAILABLE,
                reason_code="fcm_network_error",
            )

        if response.status_code == 200:
            msg_id: str | None = None
            try:
                msg_id = str(response.json().get("name"))
            except ValueError:
                msg_id = None
            return PushDeliveryResult(
                status=PushProviderStatus.SENT,
                provider_message_id=msg_id,
            )
        if response.status_code in {400, 404}:
            text = response.text.upper()
            if "UNREGISTERED" in text or "INVALID_ARGUMENT" in text or response.status_code == 404:
                return PushDeliveryResult(
                    status=PushProviderStatus.INVALID_TOKEN,
                    reason_code="fcm_unregistered_token",
                )
        if response.status_code == 429:
            return PushDeliveryResult(
                status=PushProviderStatus.QUOTA_EXCEEDED,
                reason_code="fcm_quota_exceeded",
            )
        return PushDeliveryResult(
            status=PushProviderStatus.PROVIDER_UNAVAILABLE,
            reason_code=f"fcm_http_{response.status_code}",
        )
