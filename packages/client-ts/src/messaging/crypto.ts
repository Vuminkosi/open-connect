import type { EncryptedMessageEnvelope } from "./types.js";

/**
 * Browser WebCrypto endpoint helper (ECDH P-256 + HKDF-SHA256 + AES-256-GCM).
 *
 * SECURITY BOUNDARY & XSS/CSP NOTICE:
 * - Browser E2EE protects message payloads in transit and at rest from delivery workers,
 *   brokers, and server storage, NOT from a compromised browser origin or XSS script
 *   executing in the same document.
 * - Host applications MUST deploy strict Content-Security-Policy (CSP), Subresource Integrity
 *   (SRI) where applicable, and never log key material, tokens, or decrypted plaintext.
 * - Private ECDH keys are generated non-extractable (`extractable: false`) so raw private
 *   key bytes cannot be read out by script or persisted in plaintext localStorage.
 * - Do NOT claim protection from a malicious gateway that serves modified browser bundles
 *   unless an independent code-verification mechanism is deployed.
 */

export const BROWSER_CRYPTO_PROTOCOL = "openconnect.webcrypto.ecdh-p256-aes256gcm.v1";

function toBase64(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") {
    return Buffer.from(bytes).toString("base64");
  }
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

function fromBase64(base64: string): Uint8Array<ArrayBuffer> {
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

export interface BrowserDeviceKeyPair {
  readonly keyPair: CryptoKeyPair;
  readonly publicKeySpkiBase64: string;
}

/** Generates a non-extractable ECDH P-256 device key pair in WebCrypto. */
export async function generateBrowserDeviceKeyPair(
  subtle: SubtleCrypto = globalThis.crypto.subtle
): Promise<BrowserDeviceKeyPair> {
  const keyPair = await subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    false,
    ["deriveBits"]
  );
  const spki = await subtle.exportKey("spki", keyPair.publicKey);
  return {
    keyPair,
    publicKeySpkiBase64: toBase64(new Uint8Array(spki)),
  };
}

/** Derives a non-extractable AES-256-GCM conversation epoch key via ECDH + HKDF-SHA256. */
export async function deriveEpochAesGcmKey(
  localPrivateKey: CryptoKey,
  peerPublicKeySpkiBase64: string,
  conversationId: string,
  cryptoEpoch: number,
  subtle: SubtleCrypto = globalThis.crypto.subtle
): Promise<CryptoKey> {
  const peerPublicKey = await subtle.importKey(
    "spki",
    fromBase64(peerPublicKeySpkiBase64),
    { name: "ECDH", namedCurve: "P-256" },
    false,
    []
  );

  const sharedSecret = await subtle.deriveBits(
    { name: "ECDH", public: peerPublicKey },
    localPrivateKey,
    256
  );

  const hkdfKey = await subtle.importKey(
    "raw",
    sharedSecret,
    { name: "HKDF" },
    false,
    ["deriveKey"]
  );

  const encoder = new TextEncoder();
  const salt = encoder.encode(`openconnect:imsg:v1:${conversationId}`);
  const info = encoder.encode(`epoch:${cryptoEpoch}`);

  return subtle.deriveKey(
    { name: "HKDF", hash: "SHA-256", salt, info },
    hkdfKey,
    { name: "AES-GCM", length: 256 },
    false,
    ["encrypt", "decrypt"]
  );
}

/** Encrypts plaintext into an authenticated AES-256-GCM envelope with 96-bit CSPRNG nonce. */
export async function encryptMessageEnvelope(
  aesKey: CryptoKey,
  plaintext: string,
  cryptoEpoch: number,
  associatedData?: string,
  senderKeyId?: string,
  cryptoImpl: Crypto = globalThis.crypto
): Promise<EncryptedMessageEnvelope> {
  const nonce = new Uint8Array(new ArrayBuffer(12));
  cryptoImpl.getRandomValues(nonce);

  const encoder = new TextEncoder();
  const plaintextBytes = encoder.encode(plaintext);
  const aadBytes = associatedData ? encoder.encode(associatedData) : undefined;

  const encryptedBuffer = await cryptoImpl.subtle.encrypt(
    {
      name: "AES-GCM",
      iv: nonce,
      additionalData: aadBytes,
      tagLength: 128,
    },
    aesKey,
    plaintextBytes
  );

  const encryptedBytes = new Uint8Array(encryptedBuffer);
  const tagStart = encryptedBytes.length - 16;
  const ciphertext = encryptedBytes.slice(0, tagStart);
  const authTag = encryptedBytes.slice(tagStart);

  const envelope: EncryptedMessageEnvelope = {
    protocol: BROWSER_CRYPTO_PROTOCOL,
    cryptoEpoch,
    ciphertextBase64: toBase64(ciphertext),
    nonceBase64: toBase64(nonce),
    authTagBase64: toBase64(authTag),
    ...(aadBytes ? { associatedDataBase64: toBase64(aadBytes) } : {}),
    ...(senderKeyId !== undefined ? { senderKeyId } : {}),
  };
  return envelope;
}

/** Decrypts and authenticates an AES-256-GCM envelope; throws on any tampering or AAD mismatch. */
export async function decryptMessageEnvelope(
  aesKey: CryptoKey,
  envelope: EncryptedMessageEnvelope,
  expectedAssociatedData?: string,
  subtle: SubtleCrypto = globalThis.crypto.subtle
): Promise<string> {
  const ciphertext = fromBase64(envelope.ciphertextBase64);
  const nonce = fromBase64(envelope.nonceBase64);
  const authTag = fromBase64(envelope.authTagBase64);

  const combined = new Uint8Array(new ArrayBuffer(ciphertext.length + authTag.length));
  combined.set(ciphertext, 0);
  combined.set(authTag, ciphertext.length);

  const aadBytes =
    expectedAssociatedData !== undefined
      ? new TextEncoder().encode(expectedAssociatedData)
      : envelope.associatedDataBase64
        ? fromBase64(envelope.associatedDataBase64)
        : undefined;

  const decryptedBuffer = await subtle.decrypt(
    {
      name: "AES-GCM",
      iv: nonce,
      additionalData: aadBytes,
      tagLength: 128,
    },
    aesKey,
    combined
  );

  return new TextDecoder().decode(decryptedBuffer);
}
