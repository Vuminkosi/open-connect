import { bindWorkerPayload } from './worker-payload.mjs';

// The caller owns and clears key and payload after sending them to the chosen worker.
export function bindClassificationPayload(payload) {
    return bindWorkerPayload(payload);
}
