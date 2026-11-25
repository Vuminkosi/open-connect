import { describe, expect, it, vi } from "vitest";
import {
  BROWSER_CRYPTO_PROTOCOL,
  MessagingQrValidationError,
  MessagingRealtimeClient,
  decryptMessageEnvelope,
  deriveEpochAesGcmKey,
  encryptMessageEnvelope,
  generateBrowserDeviceKeyPair,
  parseAndValidateEnrollmentQr,
  subscribeWebPush,
  type MessagingEventEnvelope,
  type MessagingWebSocketLike,
} from "./index.js";

describe("OpenConnect IMessaging browser SDK", () => {
  it("validates a well-formed HTTPS QR enrollment payload", () => {
    const now = new Date("2026-11-25T12:00:00Z");
    const raw = JSON.stringify({
      version: 1,
      type: "openconnect.device.enrollment",
      baseGatewayUrl: "https://gateway.company.example/app",
      tenantId: "01J00000000000000000000001",
      applicationId: "insurance-crm",
      pairingId: "01J00000000000000000000002",
      enrollmentToken: "high-entropy-single-use-token-0123456789",
      expiresAt: "2026-11-25T12:02:00Z",
    });

    const parsed = parseAndValidateEnrollmentQr(raw, { now });
    expect(parsed.baseGatewayUrl).toBe("https://gateway.company.example/app/");
    expect(parsed.applicationId).toBe("insurance-crm");
  });

  it("rejects insecure HTTP, embedded credentials, fragments, and expired QR payloads", () => {
    const now = new Date("2026-11-25T12:00:00Z");
    const base = {
      version: 1,
      type: "openconnect.device.enrollment",
      baseGatewayUrl: "https://gateway.company.example/",
      tenantId: "tenant-1",
      applicationId: "app-1",
      pairingId: "pair-1",
      enrollmentToken: "high-entropy-single-use-token-0123456789",
      expiresAt: "2026-11-25T12:05:00Z",
    };

    expect(() =>
      parseAndValidateEnrollmentQr(
        JSON.stringify({ ...base, baseGatewayUrl: "http://gateway.company.example/" }),
        { now }
      )
    ).toThrow(MessagingQrValidationError);

    expect(() =>
      parseAndValidateEnrollmentQr(
        JSON.stringify({ ...base, baseGatewayUrl: "https://user:pass@gateway.company.example/" }),
        { now }
      )
    ).toThrow(MessagingQrValidationError);

    expect(() =>
      parseAndValidateEnrollmentQr(
        JSON.stringify({ ...base, baseGatewayUrl: "https://gateway.company.example/#frag" }),
        { now }
      )
    ).toThrow(MessagingQrValidationError);

    expect(() =>
      parseAndValidateEnrollmentQr(
        JSON.stringify({ ...base, expiresAt: "2026-11-25T11:59:59Z" }),
        { now }
      )
    ).toThrow(MessagingQrValidationError);
  });

  it("encrypts and decrypts via WebCrypto ECDH + AES-256-GCM and rejects tampered ciphertext", async () => {
    const alice = await generateBrowserDeviceKeyPair();
    const bob = await generateBrowserDeviceKeyPair();

    const aliceKey = await deriveEpochAesGcmKey(
      alice.keyPair.privateKey,
      bob.publicKeySpkiBase64,
      "conv-1",
      3
    );
    const bobKey = await deriveEpochAesGcmKey(
      bob.keyPair.privateKey,
      alice.publicKeySpkiBase64,
      "conv-1",
      3
    );

    const envelope = await encryptMessageEnvelope(
      aliceKey,
      "Claim #4821 approved for settlement",
      3,
      "tenant-1:conv-1:3"
    );
    expect(envelope.protocol).toBe(BROWSER_CRYPTO_PROTOCOL);

    const decrypted = await decryptMessageEnvelope(bobKey, envelope, "tenant-1:conv-1:3");
    expect(decrypted).toBe("Claim #4821 approved for settlement");

    await expect(
      decryptMessageEnvelope(bobKey, envelope, "tenant-2:conv-1:3")
    ).rejects.toThrow();
  });

  it("fences realtime events by profile epoch, deduplicates event IDs, and replays from cursor", async () => {
    let activeEpoch = 1;
    let fakeSocket: MessagingWebSocketLike | null = null;
    const received: MessagingEventEnvelope[] = [];

    const replayEvent: MessagingEventEnvelope = {
      schemaVersion: 1,
      eventId: "evt-1",
      type: "messaging.message.committed",
      tenantId: "t1",
      conversationId: "c1",
      sequence: 1,
      cursor: "cur-1",
      occurredAt: "2026-11-25T12:00:00Z",
      payloadJson: "{}",
    };

    const client = new MessagingRealtimeClient({
      webSocketUrl: "wss://gateway.company.example/openconnect/v1/messaging/realtime",
      profileId: "prof-a",
      profileEpoch: 1,
      isCurrentEpoch: (epoch, id) => epoch === activeEpoch && id === "prof-a",
      createWebSocket: () => {
        fakeSocket = {
          onopen: null,
          onmessage: null,
          onerror: null,
          onclose: null,
          close: vi.fn(),
        };
        return fakeSocket;
      },
      replaySinceCursor: async () => ({
        events: [replayEvent],
        nextCursor: "cur-1",
        hasMore: false,
      }),
      onEvent: (ev) => received.push(ev),
    });

    await client.connect();
    fakeSocket!.onopen?.({});
    await vi.waitFor(() => expect(received).toHaveLength(1));
    expect(client.currentCursor).toBe("cur-1");

    fakeSocket!.onmessage?.({ data: JSON.stringify(replayEvent) });
    expect(received).toHaveLength(1);

    activeEpoch = 2;
    fakeSocket!.onmessage?.({
      data: JSON.stringify({ ...replayEvent, eventId: "evt-2", sequence: 2, cursor: "cur-2" }),
    });
    expect(received).toHaveLength(1);
    client.dispose();
  });

  it("gates Web Push subscription behind explicit consent", async () => {
    await expect(
      subscribeWebPush({
        vapidPublicKeyBase64Url: "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U",
        consentGranted: false,
      })
    ).rejects.toThrow("Explicit user consent");

    const result = await subscribeWebPush({
      vapidPublicKeyBase64Url: "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U",
      consentGranted: true,
      pushManager: {
        subscribe: async () => ({
          endpoint: "https://push.example/sub/123",
          toJSON: () => ({ keys: { p256dh: "p256dh-key", auth: "auth-key" } }),
        }),
      },
    });
    expect(result.endpoint).toBe("https://push.example/sub/123");
    expect(result.consentGranted).toBe(true);
  });
});
