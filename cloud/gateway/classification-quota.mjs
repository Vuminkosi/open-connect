// Called only after authentication, authorization, deployment matching and privacy admission.
// A successful reservation is not authority to execute: a worker-enforced claim is still required.
export async function reserveClassificationRequest({ credential, identity, value, body, signal, traceparent }, { controlPlane, keyring }) {
    const unavailable = () => ({ status: 'quota_unavailable' });
    if (signal?.aborted || value?.schemaVersion !== 'openconnect.cloud.classification/v2' ||
        !(body instanceof Uint8Array) || !identity || !controlPlane || !keyring) return unavailable();
    let fingerprint;
    try {
        const lookup = await controlPlane.findClassificationFingerprintKey(credential, value.requestId, signal, traceparent);
        if (signal?.aborted) return unavailable();
        const fingerprintKeyId = keyring.selectKeyId(lookup);
        const expiresAt = lookup.status === 'found' ? lookup.expiresAt : value.expiresAt;
        if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt)) || Date.parse(expiresAt) <= Date.now()) return unavailable();
        fingerprint = keyring.compute(fingerprintKeyId, {
            tenantId: identity.tenantId, applicationId: identity.applicationId, capability: 'classification.classify',
            method: 'POST', route: '/cloud/v1/classification', mediaType: 'application/json', body,
        });
        const result = await controlPlane.reserveClassification(credential, {
            requestId: value.requestId, fingerprintKeyId, fingerprint: fingerprint.toString('base64'), expiresAt,
        }, signal, traceparent);
        if (signal?.aborted) return unavailable();
        return result;
    } catch { return unavailable(); }
    finally { fingerprint?.fill(0); }
}
