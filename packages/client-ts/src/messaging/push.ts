export interface WebPushSubscriptionResult {
  readonly endpoint: string;
  readonly p256dh: string;
  readonly auth: string;
  readonly consentGranted: true;
}

export interface WebPushRegistrationOptions {
  readonly vapidPublicKeyBase64Url: string;
  readonly consentGranted: boolean;
  readonly pushManager?: {
    subscribe(options: {
      userVisibleOnly: boolean;
      applicationServerKey: Uint8Array<ArrayBuffer>;
    }): Promise<{
      endpoint: string;
      toJSON(): { keys?: { p256dh?: string; auth?: string } };
    }>;
  };
}

/** Converts a URL-safe base64 VAPID public key to a Uint8Array for PushManager.subscribe. */
export function decodeVapidPublicKey(base64Url: string): Uint8Array<ArrayBuffer> {
  const normalized = base64Url.replace(/-/g, "+").replace(/_/g, "/");
  const padding = "=".repeat((4 - (normalized.length % 4)) % 4);
  const base64 = normalized + padding;
  if (typeof Buffer !== "undefined") {
    const buf = Buffer.from(base64, "base64");
    const out = new Uint8Array(new ArrayBuffer(buf.length));
    out.set(buf);
    return out;
  }
  const binary = atob(base64);
  const out = new Uint8Array(new ArrayBuffer(binary.length));
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}

/**
 * Subscribes the browser to Web Push / VAPID only after explicit user consent.
 * Fails closed if consent was not granted or PushManager is unavailable.
 */
export async function subscribeWebPush(
  options: WebPushRegistrationOptions
): Promise<WebPushSubscriptionResult> {
  if (!options.consentGranted) {
    throw new Error("Explicit user consent is required before registering Web Push.");
  }
  if (!options.pushManager) {
    throw new Error("Browser PushManager is unavailable in this environment.");
  }
  const applicationServerKey = decodeVapidPublicKey(options.vapidPublicKeyBase64Url);
  const subscription = await options.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey,
  });
  const json = subscription.toJSON();
  const p256dh = json.keys?.p256dh;
  const auth = json.keys?.auth;
  if (!subscription.endpoint || !p256dh || !auth) {
    throw new Error("Push subscription returned incomplete key material.");
  }
  return {
    endpoint: subscription.endpoint,
    p256dh,
    auth,
    consentGranted: true,
  };
}
