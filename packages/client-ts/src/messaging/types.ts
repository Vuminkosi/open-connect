export type ConversationSecurityProfile =
  | "private-e2ee"
  | "agent-e2ee"
  | "governed";

export type ConversationState = "active" | "closed" | "archived";

export type MessageReceiptKind =
  | "queued"
  | "committed"
  | "delivered"
  | "read"
  | "failed";

export type MessagingConnectionState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "offline"
  | "revoked"
  | "error";

export type CallMediaSecurityMode = "transport-only" | "endpoint-e2ee";

export interface MessagingProfileIdentity {
  readonly profileId: string;
  readonly baseGatewayUrl: string;
  readonly tenantId: string;
  readonly applicationId: string;
  readonly issuer: string;
  readonly subject: string;
  readonly deviceId: string;
}

export interface DeviceEnrollmentQrPayload {
  readonly version: number;
  readonly type: string;
  readonly baseGatewayUrl: string;
  readonly tenantId: string;
  readonly applicationId: string;
  readonly pairingId: string;
  readonly enrollmentToken: string;
  readonly expiresAt: string;
}

export interface EncryptedMessageEnvelope {
  readonly protocol: string;
  readonly cryptoEpoch: number;
  readonly ciphertextBase64: string;
  readonly nonceBase64: string;
  readonly authTagBase64: string;
  readonly associatedDataBase64?: string;
  readonly senderKeyId?: string;
}

export interface MessagingEventEnvelope {
  readonly schemaVersion: number;
  readonly eventId: string;
  readonly type: string;
  readonly tenantId: string;
  readonly conversationId?: string;
  readonly sequence: number;
  readonly cursor: string;
  readonly occurredAt: string;
  readonly payloadJson: string;
  readonly correlationId?: string;
}

export interface MessagingEventReplayResponse {
  readonly events: readonly MessagingEventEnvelope[];
  readonly nextCursor?: string;
  readonly hasMore: boolean;
}
