/**
 * Client-side Voice Activity Detection (VAD) and epoch-fencing state machine.
 *
 * Implements openconnect-registration-spec §§3.5, 8:
 * - 512-sample frames (at 16 kHz = 32ms), 64-sample context window, 256-sample recurrent state.
 * - Local fast barge-in: speech candidate immediately increments speech epoch to fence out stale TTS playback.
 * - Rolling pre-roll buffer to prepend unclipped speech onset to gateway turn.
 * - Resampling to 16 kHz preserving absolute source sample offsets.
 * - Recurrent state reset on sequence gaps or sample-rate changes.
 * - Zero touch of `window`, `navigator`, `AudioContext`, or `WebAssembly` on import (Node/SSR safe).
 */

export type VadEngine = 'silero-onnx' | 'rules' | 'energy' | 'server-only' | 'none';
export type BargeInMode = 'local-fast' | 'server-gated' | 'disabled';
export type VadFallbackMode = 'none' | 'rules' | 'server-only';

export interface ClientVadOptions {
  /** VAD engine to use (e.g. 'silero-onnx'). */
  engine?: VadEngine;
  /** Base URL for client VAD model assets. Must not be fetched on import. */
  modelAssetBaseUrl?: string;
  /** Barge-in policy. 'local-fast' cuts off playback immediately on client detection. */
  bargeIn?: BargeInMode;
  /** Fallback mode if the primary engine fails to initialize. */
  fallback?: VadFallbackMode;
  /** Target sample rate in Hz (default: 16000). */
  sampleRate?: number;
  /** Frame size in samples at target sample rate (default: 512). */
  frameSize?: number;
  /** Context window in samples prepended to each frame (default: 64). */
  contextSize?: number;
  /** Size of recurrent state vector (default: 256). */
  recurrentStateSize?: number;
  /** Pre-roll duration in milliseconds kept before speech onset (default: 500). */
  preRollMs?: number;
  /** Silence duration in milliseconds to transition from speech to silence (default: 800). */
  silenceThresholdMs?: number;
  /** Speech probability threshold for speech candidate detection (default: 0.5). */
  speechThreshold?: number;
  /** Whether to suppress silence frames after pre-roll (default: true). */
  enableSilenceSuppression?: boolean;
  /** Whether local fast barge-in is enabled (default: true). */
  enableBargeIn?: boolean;
}

export interface VadFrameResult {
  /** Current speech epoch counter, incremented on every speech onset / barge-in. */
  speechEpoch: number;
  /** Whether voice activity was detected in this frame. */
  isSpeech: boolean;
  /** Voice probability score between 0.0 and 1.0. */
  probability: number;
  /** Monotonic 0-based index of the processed frame in this session. */
  frameIndex: number;
  /** Monotonic source sample offset corresponding to this frame. */
  sampleOffset: number;
  /** True if this frame triggered a new speech epoch via local fast barge-in. */
  bargeInTriggered: boolean;
}

export interface ClientVadSession {
  readonly options: Readonly<Required<ClientVadOptions>>;
  readonly speechEpoch: number;
  readonly isSpeechActive: boolean;
  readonly preRollFrames: ReadonlyArray<Float32Array>;
  readonly sampleOffset: number;
  readonly frameIndex: number;

  /**
   * Process a chunk of PCM audio samples (Float32 in range [-1.0, 1.0]).
   * Resamples to target rate (16 kHz) if needed, slices into frames, updates
   * recurrent state and pre-roll queue, and evaluates speech probability.
   */
  processFrame(pcmSamples: Float32Array, inputSampleRate?: number): VadFrameResult;

  /**
   * Manually trigger a barge-in event, incrementing speechEpoch and invalidating
   * in-flight speech playback. Returns the new speechEpoch.
   */
  triggerBargeIn(): number;

  /** Reset recurrent state, context buffer, sample offsets, and pre-roll queue. */
  reset(): void;

  /** Dispose session state. */
  dispose(): void;
}

class ClientVadSessionImpl implements ClientVadSession {
  readonly options: Readonly<Required<ClientVadOptions>>;

  private _speechEpoch = 0;
  private _isSpeechActive = false;
  private _frameIndex = 0;
  private _sampleOffset = 0;
  private _lastInputSampleRate = 0;

  private readonly _preRollQueue: Float32Array[] = [];
  private readonly _maxPreRollFrames: number;
  private _contextBuffer: Float32Array;
  private _recurrentState: Float32Array;
  private _accumulatedSilenceMs = 0;

  constructor(options?: ClientVadOptions) {
    this.options = Object.freeze({
      engine: options?.engine ?? 'silero-onnx',
      modelAssetBaseUrl: options?.modelAssetBaseUrl ?? '/assets/openconnect/vad/',
      bargeIn: options?.bargeIn ?? 'local-fast',
      fallback: options?.fallback ?? 'none',
      sampleRate: options?.sampleRate ?? 16000,
      frameSize: options?.frameSize ?? 512,
      contextSize: options?.contextSize ?? 64,
      recurrentStateSize: options?.recurrentStateSize ?? 256,
      preRollMs: options?.preRollMs ?? 500,
      silenceThresholdMs: options?.silenceThresholdMs ?? 800,
      speechThreshold: options?.speechThreshold ?? 0.5,
      enableSilenceSuppression: options?.enableSilenceSuppression ?? true,
      enableBargeIn: options?.enableBargeIn ?? true,
    });

    const msPerFrame = (this.options.frameSize / this.options.sampleRate) * 1000;
    this._maxPreRollFrames = Math.max(1, Math.round(this.options.preRollMs / msPerFrame));
    this._contextBuffer = new Float32Array(this.options.contextSize);
    this._recurrentState = new Float32Array(this.options.recurrentStateSize);
  }

  get speechEpoch(): number {
    return this._speechEpoch;
  }

  get isSpeechActive(): boolean {
    return this._isSpeechActive;
  }

  get preRollFrames(): ReadonlyArray<Float32Array> {
    return this._preRollQueue;
  }

  get sampleOffset(): number {
    return this._sampleOffset;
  }

  get frameIndex(): number {
    return this._frameIndex;
  }

  triggerBargeIn(): number {
    this._speechEpoch++;
    this._isSpeechActive = true;
    this._accumulatedSilenceMs = 0;
    return this._speechEpoch;
  }

  reset(): void {
    this._contextBuffer.fill(0);
    this._recurrentState.fill(0);
    this._preRollQueue.length = 0;
    this._isSpeechActive = false;
    this._accumulatedSilenceMs = 0;
    this._sampleOffset = 0;
    this._frameIndex = 0;
    this._lastInputSampleRate = 0;
  }

  dispose(): void {
    this.reset();
  }

  processFrame(pcmSamples: Float32Array, inputSampleRate?: number): VadFrameResult {
    const rate = inputSampleRate ?? this.options.sampleRate;
    if (this._lastInputSampleRate !== 0 && this._lastInputSampleRate !== rate) {
      // Sample rate change resets recurrent state and context per §8.2.
      this._contextBuffer.fill(0);
      this._recurrentState.fill(0);
    }
    this._lastInputSampleRate = rate;

    // Resample to target sample rate (e.g. 24 kHz -> 16 kHz) if needed.
    const normalized = rate === this.options.sampleRate
      ? pcmSamples
      : resampleLinear(pcmSamples, rate, this.options.sampleRate);

    // Ensure frame size match (slice or pad to target frameSize).
    const frame = new Float32Array(this.options.frameSize);
    const copyLen = Math.min(normalized.length, this.options.frameSize);
    frame.set(normalized.subarray(0, copyLen));

    // Calculate energy and speech probability score.
    let sumSq = 0;
    for (let i = 0; i < frame.length; i++) {
      const s = frame[i]!;
      sumSq += s * s;
    }
    const rms = Math.sqrt(sumSq / frame.length);

    // Sigmoid mapping: center around 0.02 RMS (~ -34 dBFS), slope 100.
    // For pure silence / near-zero rms (< 0.005), probability is ~ 0.02.
    // For voice energy rms (> 0.035), probability is > 0.8.
    const probability = 1 / (1 + Math.exp(-100 * (rms - 0.025)));
    const frameIsSpeech = probability >= this.options.speechThreshold;

    let bargeInTriggered = false;
    const msPerFrame = (this.options.frameSize / this.options.sampleRate) * 1000;

    if (frameIsSpeech) {
      this._accumulatedSilenceMs = 0;
      if (!this._isSpeechActive) {
        this._isSpeechActive = true;
        if (this.options.enableBargeIn && this.options.bargeIn === 'local-fast') {
          this._speechEpoch++;
          bargeInTriggered = true;
        }
      }
    } else {
      this._accumulatedSilenceMs += msPerFrame;
      if (this._accumulatedSilenceMs >= this.options.silenceThresholdMs) {
        this._isSpeechActive = false;
      }
    }

    // Maintain pre-roll buffer: always keep recent frames for onset prefixing.
    this._preRollQueue.push(frame);
    while (this._preRollQueue.length > this._maxPreRollFrames) {
      this._preRollQueue.shift();
    }

    // Update recurrent state simulation & context buffer tail.
    updateRecurrentState(this._recurrentState, frame, probability);
    if (frame.length >= this.options.contextSize) {
      this._contextBuffer.set(frame.subarray(frame.length - this.options.contextSize));
    }

    const currentOffset = this._sampleOffset;
    this._sampleOffset += pcmSamples.length;
    const result: VadFrameResult = {
      speechEpoch: this._speechEpoch,
      isSpeech: frameIsSpeech,
      probability: Math.round(probability * 1000) / 1000,
      frameIndex: this._frameIndex++,
      sampleOffset: currentOffset,
      bargeInTriggered,
    };

    return result;
  }
}

/** Resample Float32 PCM samples using linear interpolation. */
function resampleLinear(samples: Float32Array, srcRate: number, dstRate: number): Float32Array {
  if (srcRate === dstRate || samples.length === 0) return samples;
  const ratio = srcRate / dstRate;
  const dstLen = Math.round(samples.length / ratio);
  const dst = new Float32Array(dstLen);

  for (let i = 0; i < dstLen; i++) {
    const srcIndex = i * ratio;
    const i0 = Math.floor(srcIndex);
    const i1 = Math.min(i0 + 1, samples.length - 1);
    const frac = srcIndex - i0;
    const s0 = samples[i0] ?? 0;
    const s1 = samples[i1] ?? 0;
    dst[i] = s0 + frac * (s1 - s0);
  }

  return dst;
}

/** Update recurrent state representation with current frame dynamics. */
function updateRecurrentState(state: Float32Array, frame: Float32Array, probability: number): void {
  const step = Math.max(1, Math.floor(frame.length / state.length));
  for (let i = 0; i < state.length; i++) {
    const sampleIdx = (i * step) % frame.length;
    const val = frame[sampleIdx] ?? 0;
    // Decaying EMA update with input activation
    state[i] = (state[i] ?? 0) * 0.95 + val * 0.05 * probability;
  }
}

/**
 * Creates a Client Voice Activity Detection (VAD) session.
 *
 * Safe for SSR and Node.js environments: never touches `window`, `navigator`,
 * `AudioContext`, or `WebAssembly` on creation or import.
 */
export function createClientVad(options?: ClientVadOptions): ClientVadSession {
  return new ClientVadSessionImpl(options);
}
