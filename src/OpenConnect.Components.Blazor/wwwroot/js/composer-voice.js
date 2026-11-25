window.ocComposerVoice = (() => {
  let state = null;
  let sileroRuntimeLoading = null;
  let sileroModelBytesPromise = null;
  const targetSampleRate = 24000;
  const sileroSampleRate = 16000;
  const sileroFrameSamples = 512;
  const maxPendingFrames = 128;
  const maxPreRollFrames = 16;
  const speechThreshold = Number(window.__OC_VOICE_SPEECH_THRESHOLD || 0.008);
  const silenceStopMs = Number(window.__OC_VOICE_SILENCE_MS || 1500);
  const sileroPositiveThreshold = Number(window.__OC_VOICE_SILERO_POSITIVE_THRESHOLD || 0.5);
  const sileroContinueThreshold = Number(window.__OC_VOICE_SILERO_CONTINUE_THRESHOLD || 0.25);
  const toWs = (url) => url.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:');
  const workletUrl = () => new URL('_content/OpenConnect.Components.Blazor/js/voice-capture-worklet.js', document.baseURI).href;
  const playbackWorkletUrl = () => new URL('_content/OpenConnect.Components.Blazor/js/voice-playback-worklet.js', document.baseURI).href;
  const vadBaseUrl = () => new URL('_content/OpenConnect.Components.Blazor/vendor/vad/', document.baseURI).href;

  // --- v2 Binary Frame Codec (32-byte little-endian header matching VoiceBinaryFrameCodec) ---
  const V2_MAGIC_0 = 0x4f; // 'O'
  const V2_MAGIC_1 = 0x43; // 'C'
  const V2_VERSION = 2;
  const V2_HEADER_SIZE = 32;
  const V2_MAX_FRAME_BYTES = 96 * 1024;
  const V2_MAX_PAYLOAD_BYTES = V2_MAX_FRAME_BYTES - V2_HEADER_SIZE;
  const V2_STREAM_KIND_MIC = 1;
  const V2_STREAM_KIND_TTS = 2;

  const encodeV2Frame = (header, pcmPayload) => {
    if (!header || typeof header !== 'object') return null;
    const streamKind = Number(header.streamKind);
    const streamId = Number(header.streamId ?? 1) >>> 0;
    const speechEpoch = Number(header.speechEpoch ?? 0);
    const sequence = Number(header.sequence ?? 0);
    const sampleOffset = Number(header.sampleOffset ?? 0);

    if ((streamKind !== V2_STREAM_KIND_MIC && streamKind !== V2_STREAM_KIND_TTS) ||
        !Number.isSafeInteger(speechEpoch) || speechEpoch < 0 ||
        !Number.isSafeInteger(sequence) || sequence < 0 ||
        !Number.isSafeInteger(sampleOffset) || sampleOffset < 0) {
      return null;
    }

    const payloadBytes = pcmPayload instanceof ArrayBuffer
      ? new Uint8Array(pcmPayload)
      : ArrayBuffer.isView(pcmPayload)
        ? new Uint8Array(pcmPayload.buffer, pcmPayload.byteOffset, pcmPayload.byteLength)
        : new Uint8Array(0);

    if ((payloadBytes.byteLength & 1) !== 0 || payloadBytes.byteLength > V2_MAX_PAYLOAD_BYTES) {
      return null;
    }

    const frame = new ArrayBuffer(V2_HEADER_SIZE + payloadBytes.byteLength);
    const view = new DataView(frame);
    view.setUint8(0, V2_MAGIC_0);
    view.setUint8(1, V2_MAGIC_1);
    view.setUint8(2, V2_VERSION);
    view.setUint8(3, streamKind);
    view.setUint32(4, streamId, true);
    view.setBigInt64(8, BigInt(speechEpoch), true);
    view.setBigInt64(16, BigInt(sequence), true);
    view.setBigInt64(24, BigInt(sampleOffset), true);
    if (payloadBytes.byteLength > 0) {
      new Uint8Array(frame, V2_HEADER_SIZE).set(payloadBytes);
    }
    return frame;
  };

  const decodeV2Frame = (frameBuffer) => {
    const bytes = frameBuffer instanceof ArrayBuffer
      ? new Uint8Array(frameBuffer)
      : ArrayBuffer.isView(frameBuffer)
        ? new Uint8Array(frameBuffer.buffer, frameBuffer.byteOffset, frameBuffer.byteLength)
        : null;
    if (!bytes || bytes.byteLength < V2_HEADER_SIZE || bytes.byteLength > V2_MAX_FRAME_BYTES) {
      return null;
    }
    if (bytes[0] !== V2_MAGIC_0 || bytes[1] !== V2_MAGIC_1 || bytes[2] !== V2_VERSION) {
      return null;
    }
    const streamKind = bytes[3];
    if (streamKind !== V2_STREAM_KIND_MIC && streamKind !== V2_STREAM_KIND_TTS) {
      return null;
    }
    const payloadLength = bytes.byteLength - V2_HEADER_SIZE;
    if ((payloadLength & 1) !== 0) {
      return null;
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const streamId = view.getUint32(4, true);
    const speechEpochBig = view.getBigInt64(8, true);
    const sequenceBig = view.getBigInt64(16, true);
    const sampleOffsetBig = view.getBigInt64(24, true);
    if (speechEpochBig < 0n || sequenceBig < 0n || sampleOffsetBig < 0n) {
      return null;
    }
    const speechEpoch = Number(speechEpochBig);
    const sequence = Number(sequenceBig);
    const sampleOffset = Number(sampleOffsetBig);
    if (!Number.isSafeInteger(speechEpoch) || !Number.isSafeInteger(sequence) || !Number.isSafeInteger(sampleOffset)) {
      return null;
    }
    const payload = bytes.slice(V2_HEADER_SIZE).buffer;
    return {
      header: { streamKind, streamId, speechEpoch, sequence, sampleOffset },
      payload
    };
  };

  const createEpochFence = (initialEpoch = 0) => {
    let activeEpoch = Math.max(0, Number(initialEpoch) || 0);
    let suppressedEpoch = -1;
    return {
      get activeEpoch() { return activeEpoch; },
      get suppressedEpoch() { return suppressedEpoch; },
      advanceTo(epoch) {
        const next = Number(epoch);
        if (Number.isSafeInteger(next) && next > activeEpoch) activeEpoch = next;
        return activeEpoch;
      },
      suppress(epoch) {
        const target = Number(epoch ?? activeEpoch);
        if (Number.isSafeInteger(target) && target > suppressedEpoch) suppressedEpoch = target;
        return suppressedEpoch;
      },
      shouldAccept(epoch) {
        const candidate = Number(epoch);
        if (!Number.isSafeInteger(candidate) || candidate < 0) return false;
        if (candidate <= suppressedEpoch) return false;
        if (candidate < activeEpoch) return false;
        return true;
      }
    };
  };

  const setVoiceLevel = (level) => {
    const clamped = Math.max(0, Math.min(1, Number.isFinite(level) ? level : 0));
    const radius = clamped > 0.01 ? `${(6 + clamped * 18).toFixed(1)}px` : '0px';
    const opacity = clamped > 0.01 ? `${Math.round(35 + clamped * 60)}%` : '0%';
    const root = document?.documentElement;
    if (!root?.style) return;
    if (clamped > 0.01) {
      root.style.setProperty('--oc-voice-glow-radius', radius);
      root.style.setProperty('--oc-voice-glow-opacity', opacity);
    } else {
      root.style.setProperty('--oc-voice-glow-radius', '0px');
      root.style.setProperty('--oc-voice-glow-opacity', '0%');
    }
  };

  const loadScript = (src) => new Promise((resolve, reject) => {
    if (!document?.createElement || !document?.head?.appendChild) {
      reject(new Error('document_script_unavailable'));
      return;
    }
    const existing = Array.from(document.querySelectorAll?.('script') ?? []).find(s => s.src === src);
    if (existing) {
      resolve();
      return;
    }
    const element = document.createElement('script');
    element.src = src;
    element.onload = () => resolve();
    element.onerror = () => reject(new Error(`failed_to_load_${src}`));
    document.head.appendChild(element);
  });

  const createSileroDetector = async () => {
    if (window.__OC_VOICE_DISABLE_SILERO__ || typeof fetch !== 'function' || !document?.head?.appendChild) {
      return null;
    }
    const base = vadBaseUrl();
    sileroRuntimeLoading ??= (async () => {
      if (!globalThis.ort) await loadScript(`${base}ort.wasm.min.js`);
      if (!globalThis.ellaiVad?.Silero) await loadScript(`${base}vad.bundle.min.js`);
    })().catch(error => {
      sileroRuntimeLoading = null;
      throw error;
    });
    await sileroRuntimeLoading;
    if (!globalThis.ort || !globalThis.ellaiVad?.Silero) return null;

    globalThis.ort.env.wasm.wasmPaths = base;
    globalThis.ort.env.wasm.numThreads = 1;
    globalThis.ort.env.logLevel = 'error';

    sileroModelBytesPromise ??= (async () => {
      const response = await fetch(`${base}silero_vad_v5.onnx`);
      if (!response.ok) throw new Error('silero_vad_model_unavailable');
      return response.arrayBuffer();
    })().catch(error => {
      sileroModelBytesPromise = null;
      throw error;
    });

    const detector = await globalThis.ellaiVad.Silero.new(globalThis.ort, async () => {
      const bytes = await sileroModelBytesPromise;
      return bytes.slice(0);
    });
    return {
      async analyze16k(frame512) {
        const result = await detector.process(frame512);
        return typeof result?.isSpeech === 'number' ? result.isSpeech : 0;
      },
      async destroy() {
        try { await detector.release(); } catch (_) { }
      }
    };
  };

  const pcm16 = (samples) => {
    const bytes = new ArrayBuffer(samples.length * 2);
    const view = new DataView(bytes);
    for (let i = 0; i < samples.length; i++) {
      const value = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(i * 2, value < 0 ? value * 0x8000 : value * 0x7fff, true);
    }
    return bytes;
  };

  const resample = (input, from, to) => {
    if (!input?.length || from === to) return input;
    const length = Math.max(1, Math.round(input.length * to / from));
    const output = new Float32Array(length);
    const ratio = from / to;
    for (let i = 0; i < length; i++) {
      const position = i * ratio;
      const index = Math.floor(position);
      const fraction = position - index;
      const a = input[Math.min(index, input.length - 1)];
      const b = input[Math.min(index + 1, input.length - 1)];
      output[i] = a + (b - a) * fraction;
    }
    return output;
  };

  const stop = async (notify = true, reason = 'stopped', abort = false) => {
    const current = state;
    state = null;
    setVoiceLevel(0);
    if (!current) return;
    if (current.diagnostics) {
      current.diagnostics.stopReason = reason;
      current.diagnostics.stoppedAt = Date.now();
    }
    clearTimeout(current.silenceTimer);
    clearTimeout(current.maxTimer);
    current.endRequested = true;
    try {
      current.processor.port.postMessage({ type: 'stop' });
      current.processor.disconnect();
      current.source.disconnect();
    } catch (_) { }
    try {
      if (abort) {
        current.pendingPcm.length = 0;
        current.preRollPcm.length = 0;
        current.socket.close();
      } else if (current.socket.readyState === WebSocket.OPEN) {
        for (const bytes of current.pendingPcm.splice(0)) current.socket.send(bytes);
        current.socket.send(JSON.stringify({ type: 'endStream' }));
      }
    } catch (_) { }
    try { await current.sileroQueue; } catch (_) { }
    try { await current.silero?.destroy?.(); } catch (_) { }
    try { await current.context.close(); } catch (_) { }
    try { current.stream.getTracks().forEach(track => track.stop()); } catch (_) { }
    if (notify) try { await current.dotnet.invokeMethodAsync('OnVoiceStopped'); } catch (_) { }
  };

  const waitForSocket = (socket) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('voice_socket_timeout')), 15000);
    socket.addEventListener('open', () => { clearTimeout(timer); resolve(); }, { once: true });
    socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('voice_socket_error')); }, { once: true });
    socket.addEventListener('close', () => { clearTimeout(timer); reject(new Error('voice_socket_closed')); }, { once: true });
  });

  const start = async (dotnet, sessionId, speechEpoch, endpoint, ticket, connectionId) => {
    await stop(false, 'restarted');
    const captureId = crypto.randomUUID();
    const url = toWs(endpoint || `${window.location.origin}/openconnect/v1/voice/realtime/${encodeURIComponent(sessionId)}`);
    const protocols = ticket ? ['openconnect.voice.v1', `oc-ticket.${ticket}`] : 'openconnect.voice.v1';
    const socket = new WebSocket(url, protocols);
    socket.binaryType = 'arraybuffer';

    let stream, context, source, processor;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      context = new AudioContext();
      await context.resume();
      if (context.state !== 'running') throw new Error('voice_audio_context_not_running');
      await context.audioWorklet.addModule(workletUrl());
      source = context.createMediaStreamSource(stream);
      processor = new AudioWorkletNode(context, 'openconnect-pcm-capture', { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1 });
    } catch (error) {
      try { processor?.disconnect(); source?.disconnect(); } catch (_) { }
      try { await context?.close(); } catch (_) { }
      try { stream?.getTracks().forEach(track => track.stop()); } catch (_) { }
      try { socket.close(); } catch (_) { }
      throw error;
    }
    const track = stream.getAudioTracks()[0];
    let processorReadyResolve;
    const processorReady = new Promise(resolve => { processorReadyResolve = resolve; });
    const diagnostics = window.__OC_VOICE_DIAGNOSTICS__ ? {
      captureId,
      streamId: stream.id,
      inputSampleRate: context.sampleRate,
      trackSettings: track?.getSettings?.() ?? {},
      trackReadyState: track?.readyState,
      processorType: 'AudioWorkletNode',
      processorReady: false,
      processorMessages: 0,
      sourceFrames: 0,
      sentFrames: 0,
      sentBytes: 0,
      rmsMax: 0,
      peakMax: 0,
      sileroReady: false,
      sileroFrames: 0,
      sileroProbabilityMax: 0,
      speechSeen: false,
      stopReason: null,
      startedAt: Date.now(),
      stoppedAt: null,
    } : null;
    window.__ocVoiceDiagnostics = diagnostics;
    if (window.__OC_VOICE_DIAGNOSTICS__) {
      window.__ocVoiceTestHooks = Object.assign(window.__ocVoiceTestHooks || {}, { pcm16, resample, createSileroDetector });
    }
    const current = {
      dotnet,
      captureId,
      speechEpoch,
      stream,
      context,
      source,
      processor,
      socket,
      diagnostics,
      maxTimer: null,
      silenceTimer: null,
      pendingPcm: [],
      pendingFrameCount: 0,
      preRollPcm: [],
      silero: null,
      sileroBuffer16k: [],
      sileroQueue: Promise.resolve(),
      sileroSpeaking: false,
      endRequested: false,
      speechSeen: false,
      finalTranscript: '',
      processorReady: false,
      transportOpened: false,
      smoothedLevel: 0,
    };
    state = current;

    createSileroDetector().then(detector => {
      if (state !== current || current.endRequested) {
        detector?.destroy?.();
        return;
      }
      if (detector) {
        current.silero = detector;
        if (current.diagnostics) current.diagnostics.sileroReady = true;
      }
    }).catch(() => { });

    const maxDurationMs = Number(window.__OC_VOICE_MAX_DURATION_MS || 0);
    if (maxDurationMs > 0) current.maxTimer = setTimeout(() => stop(false, 'max-duration'), maxDurationMs);

    const enqueueOutboundPcm = (bytes) => {
      if (socket.readyState === WebSocket.OPEN && current.transportOpened) {
        if (socket.bufferedAmount < 512 * 1024) socket.send(bytes);
      } else if (current.pendingFrameCount < maxPendingFrames) {
        current.pendingPcm.push(bytes);
        current.pendingFrameCount++;
      }
      if (current.diagnostics) {
        current.diagnostics.sentFrames++;
        current.diagnostics.sentBytes += bytes.byteLength;
      }
    };

    const markSpeechActivity = (isSpeech) => {
      if (state !== current || current.endRequested) return;
      if (isSpeech) {
        const firstSpeech = !current.speechSeen;
        current.speechSeen = true;
        current.sileroSpeaking = true;
        if (current.diagnostics) current.diagnostics.speechSeen = true;
        clearTimeout(current.silenceTimer);
        current.silenceTimer = null;
        if (firstSpeech && current.preRollPcm.length > 0) {
          for (const preRollBytes of current.preRollPcm.splice(0)) enqueueOutboundPcm(preRollBytes);
        }
      } else if (current.speechSeen && !current.silenceTimer) {
        current.sileroSpeaking = false;
        current.silenceTimer = setTimeout(() => stop(false, 'silence'), silenceStopMs);
      }
    };

    const openTransport = () => {
      if (state !== current || current.transportOpened || socket.readyState !== WebSocket.OPEN) return;
      current.transportOpened = true;
      socket.send(JSON.stringify({ type: 'open', sessionId, captureId, speechEpoch, clientConnectionId: connectionId || null, sampleRate: targetSampleRate, channels: 1, bitsPerSample: 16 }));
      for (const bytes of current.pendingPcm.splice(0)) socket.send(bytes);
      current.pendingFrameCount = 0;
      if (current.endRequested) socket.send(JSON.stringify({ type: 'endStream' }));
    };
    socket.onopen = openTransport;

    socket.onmessage = async event => {
      try {
        const message = JSON.parse(event.data);
        if (message.type === 'error') {
          await stop(false, 'transcription_error');
          await dotnet.invokeMethodAsync('OnVoiceError', 'Voice transcription or task submission is unavailable. Text input remains available.');
          return;
        }
        if (message.type === 'transcript-partial') {
          if (current.diagnostics) current.diagnostics.lastPartial = message.text || '';
          await dotnet.invokeMethodAsync('OnVoicePartial', message.text || '', speechEpoch);
        }
        if (message.type === 'transcript-final' && message.authoritative) {
          const candidate = (message.text || '').trim();
          const previous = current.finalTranscript || '';
          if (!candidate || candidate.length <= previous.length) return;
          current.finalTranscript = candidate;
          if (current.diagnostics) {
            current.diagnostics.finalTranscript = candidate;
            current.diagnostics.finalReceivedAt = Date.now();
          }
          await dotnet.invokeMethodAsync('OnVoiceFinal', candidate, speechEpoch);
        }
      } catch (_) { }
    };
    socket.onerror = async () => {
      processorReadyResolve();
      await stop(false, 'socket_error');
      try { await dotnet.invokeMethodAsync('OnVoiceError', 'Voice transcription is unavailable. Text input remains available.'); } catch (_) { }
    };
    socket.onclose = async event => {
      if (state !== current) return;
      processorReadyResolve();
      await stop(false, 'socket_closed');
      try {
        if (event?.code === 1000 && current.finalTranscript) await dotnet.invokeMethodAsync('OnVoiceStopped');
        else await dotnet.invokeMethodAsync('OnVoiceError', 'Voice transcription is unavailable. Text input remains available.');
      } catch (_) { }
    };

    processor.port.onmessage = event => {
      if (event.data && event.data.type === 'ready') {
        current.processorReady = true;
        if (current.diagnostics) current.diagnostics.processorReady = true;
        processorReadyResolve();
        return;
      }
      if (state !== current || current.endRequested) return;
      const sourceSamples = event.data instanceof Float32Array ? event.data : new Float32Array(event.data);
      if (!sourceSamples.length) return;
      let energy = 0;
      let peak = 0;
      for (let i = 0; i < sourceSamples.length; i++) {
        energy += sourceSamples[i] * sourceSamples[i];
        peak = Math.max(peak, Math.abs(sourceSamples[i]));
      }
      const rms = Math.sqrt(energy / sourceSamples.length);
      const normalized = Math.max(0, Math.min(1, (rms - 0.008) / 0.16));
      current.smoothedLevel = current.smoothedLevel * 0.58 + normalized * 0.42;
      setVoiceLevel(current.smoothedLevel < 0.025 ? 0 : current.smoothedLevel);
      if (current.diagnostics) {
        current.diagnostics.processorMessages++;
        current.diagnostics.sourceFrames += sourceSamples.length;
        current.diagnostics.rmsMax = Math.max(current.diagnostics.rmsMax, rms);
        current.diagnostics.peakMax = Math.max(current.diagnostics.peakMax, peak);
      }

      const samples24k = resample(sourceSamples, context.sampleRate, targetSampleRate);
      if (!samples24k.length) return;
      const bytes24k = pcm16(samples24k);

      if (current.silero) {
        if (!current.speechSeen) {
          current.preRollPcm.push(bytes24k);
          while (current.preRollPcm.length > maxPreRollFrames) current.preRollPcm.shift();
        } else {
          enqueueOutboundPcm(bytes24k);
        }

        const samples16k = resample(sourceSamples, context.sampleRate, sileroSampleRate);
        for (let i = 0; i < samples16k.length; i++) current.sileroBuffer16k.push(samples16k[i]);
        while (current.sileroBuffer16k.length >= sileroFrameSamples) {
          const frame512 = Float32Array.from(current.sileroBuffer16k.splice(0, sileroFrameSamples));
          current.sileroQueue = current.sileroQueue.then(async () => {
            if (state !== current || current.endRequested || !current.silero) return;
            const probability = await current.silero.analyze16k(frame512);
            if (current.diagnostics) {
              current.diagnostics.sileroFrames++;
              current.diagnostics.sileroProbabilityMax = Math.max(current.diagnostics.sileroProbabilityMax, probability);
            }
            const threshold = current.sileroSpeaking ? sileroContinueThreshold : sileroPositiveThreshold;
            const isSpeech = probability >= threshold || (window.__OC_VOICE_SPEECH_THRESHOLD && rms >= speechThreshold);
            markSpeechActivity(isSpeech);
          }).catch(() => {
            markSpeechActivity(rms >= speechThreshold);
          });
        }
      } else {
        markSpeechActivity(rms >= speechThreshold);
        enqueueOutboundPcm(bytes24k);
      }
    };

    source.connect(processor);
    processor.connect(context.destination);
    await processorReady;
    if (state !== current) throw new Error('voice_socket_closed');
    if (window.__ocTestMicrophone?.start) window.__ocTestMicrophone.start();
    if (socket.readyState === WebSocket.CLOSED || socket.readyState === WebSocket.CLOSING) {
      await stop(false, 'socket_closed');
      throw new Error('voice_socket_closed');
    }
    if (socket.readyState !== WebSocket.OPEN) {
      try { await waitForSocket(socket); }
      catch (error) { await stop(false, 'socket_unavailable'); throw error; }
    }
    openTransport();
  };

  // --- v2 Persistent Full-Duplex Session ---
  let duplexState = null;

  const createFallbackPlayback = (context, onReceipt, onDrained) => {
    const activeSources = new Set();
    let nextStartTime = 0;
    let playedSamples = 0;
    let speechEpoch = 0;
    let responseId = null;
    let drainTimer = null;

    const clearTimer = () => {
      if (drainTimer) {
        clearTimeout(drainTimer);
        drainTimer = null;
      }
    };

    return {
      mode: 'AudioBufferSourceNode',
      get playedSamples() { return playedSamples; },
      reset(epoch, respId) {
        this.clear(false);
        speechEpoch = epoch || 0;
        responseId = respId ?? null;
        playedSamples = 0;
      },
      push(pcmBuffer, epoch) {
        if (epoch !== undefined && speechEpoch !== 0 && epoch !== speechEpoch) return;
        const bytes = pcmBuffer instanceof ArrayBuffer ? new Uint8Array(pcmBuffer) : new Uint8Array(pcmBuffer.buffer, pcmBuffer.byteOffset, pcmBuffer.byteLength);
        const count = bytes.byteLength >> 1;
        if (count <= 0) return;
        const view = new DataView(bytes.buffer, bytes.byteOffset, count * 2);
        const audioBuffer = context.createBuffer(1, count, targetSampleRate);
        const channel = audioBuffer.getChannelData(0);
        for (let i = 0; i < count; i++) {
          channel[i] = view.getInt16(i * 2, true) / 32768;
        }
        const source = context.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(context.destination);
        const now = context.currentTime || 0;
        const startAt = Math.max(now, nextStartTime);
        nextStartTime = startAt + audioBuffer.duration;
        activeSources.add(source);
        source.onended = () => {
          activeSources.delete(source);
          playedSamples += count;
          onReceipt({ playedSamples, underruns: 0, speechEpoch, responseId });
        };
        source.start(startAt);
      },
      drain() {
        clearTimer();
        const remainingMs = Math.max(0, ((nextStartTime - (context.currentTime || 0)) * 1000) + 25);
        drainTimer = setTimeout(() => {
          drainTimer = null;
          onDrained({ playedSamples, underruns: 0, speechEpoch, responseId });
        }, remainingMs);
      },
      clear() {
        clearTimer();
        for (const source of activeSources) {
          try { source.stop(); } catch (_) { }
          try { source.disconnect(); } catch (_) { }
        }
        activeSources.clear();
        nextStartTime = 0;
      },
      dispose() {
        this.clear();
      }
    };
  };

  const createPlaybackEngine = async (context, onReceipt, onDrained, onCleared) => {
    try {
      if (context.audioWorklet?.addModule && typeof AudioWorkletNode === 'function') {
        await context.audioWorklet.addModule(playbackWorkletUrl());
        const node = new AudioWorkletNode(context, 'openconnect-pcm-playback', {
          numberOfInputs: 0,
          numberOfOutputs: 1,
          outputChannelCount: [1],
          processorOptions: {
            sourceSampleRate: targetSampleRate,
            maxBufferMs: 500,
            initialBufferMs: 60,
            receiptIntervalMs: 120,
            fadeOutMs: 5
          }
        });
        let playedSamples = 0;
        let underruns = 0;
        node.port.onmessage = (event) => {
          const msg = event.data;
          if (!msg) return;
          if (typeof msg.playedSamples === 'number') playedSamples = msg.playedSamples;
          if (typeof msg.underruns === 'number') underruns = msg.underruns;
          if (msg.type === 'receipt') onReceipt(msg);
          else if (msg.type === 'drained') onDrained(msg);
          else if (msg.type === 'cleared') onCleared?.(msg);
        };
        node.connect(context.destination);
        return {
          mode: 'AudioWorkletNode',
          node,
          get playedSamples() { return playedSamples; },
          get underruns() { return underruns; },
          reset(epoch, responseId) {
            playedSamples = 0;
            underruns = 0;
            node.port.postMessage({ type: 'reset', speechEpoch: epoch, responseId });
          },
          push(pcmBuffer, epoch) {
            const copy = pcmBuffer instanceof ArrayBuffer ? pcmBuffer.slice(0) : pcmBuffer.buffer.slice(pcmBuffer.byteOffset, pcmBuffer.byteOffset + pcmBuffer.byteLength);
            node.port.postMessage({ type: 'push', pcm16: copy, sampleRate: targetSampleRate, speechEpoch: epoch }, [copy]);
          },
          drain() {
            node.port.postMessage({ type: 'drain' });
          },
          clear(fadeOut = true) {
            node.port.postMessage({ type: 'clear', fadeOut });
          },
          dispose() {
            try { node.port.postMessage({ type: 'clear', fadeOut: false }); } catch (_) { }
            try { node.disconnect(); } catch (_) { }
          }
        };
      }
    } catch (_) {
      // Fallback cleanly to AudioBufferSourceNode when AudioWorklet playback is unavailable.
    }
    return createFallbackPlayback(context, onReceipt, onDrained);
  };

  const stopSession = async (notify = true, reason = 'stopped') => {
    const current = duplexState;
    duplexState = null;
    setVoiceLevel(0);
    if (!current) return;
    current.closing = true;
    if (current.diagnostics) {
      current.diagnostics.stopReason = reason;
      current.diagnostics.stoppedAt = Date.now();
    }
    try {
      if (current.socket?.readyState === WebSocket.OPEN) {
        current.sendControl({ type: 'session.close', reason });
        current.socket.close(1000, reason);
      } else {
        current.socket?.close?.();
      }
    } catch (_) { }
    try {
      current.captureNode?.port?.postMessage?.({ type: 'stop' });
      current.captureNode?.disconnect?.();
      current.source?.disconnect?.();
    } catch (_) { }
    try { current.playback?.dispose?.(); } catch (_) { }
    try { await current.sileroQueue; } catch (_) { }
    try { await current.silero?.destroy?.(); } catch (_) { }
    try { await current.context?.close?.(); } catch (_) { }
    try { current.stream?.getTracks?.().forEach(track => track.stop()); } catch (_) { }
    if (notify) {
      try { await current.dotnet?.invokeMethodAsync?.('OnDuplexVoiceStopped'); } catch (_) { }
    }
  };

  const endUtterance = async () => {
    const current = duplexState;
    if (!current || current.closing) return false;
    // Flush pending outbound audio frames, signal speech.ended, and prepare next captureId
    // WITHOUT releasing microphone tracks, AudioContext, or WebSocket.
    if (current.socket.readyState === WebSocket.OPEN && current.transportOpened) {
      for (const frame of current.pendingFrames.splice(0)) {
        if (current.socket.bufferedAmount < 512 * 1024) current.socket.send(frame);
      }
      current.sendControl({
        type: 'speech.ended',
        captureId: current.captureId,
        speechEpoch: current.epochFence.activeEpoch,
        authoritative: false
      });
    }
    current.utteranceCount++;
    current.captureId = crypto.randomUUID();
    current.micSampleOffset = 0;
    current.speechActive = false;
    current.bargeInOnsetStartedAt = 0;
    if (current.diagnostics) {
      current.diagnostics.utteranceCount = current.utteranceCount;
      current.diagnostics.captureId = current.captureId;
    }
    return true;
  };

  const setMuted = async (muted) => {
    const current = duplexState;
    if (!current || current.closing) return false;
    current.muted = !!muted;
    try {
      for (const track of current.stream?.getAudioTracks?.() ?? []) {
        track.enabled = !current.muted;
      }
    } catch (_) { }
    if (current.muted) {
      setVoiceLevel(0);
      current.bargeInOnsetStartedAt = 0;
    }
    current.sendControl({
      type: current.muted ? 'session.mute' : 'session.unmute',
      speechEpoch: current.epochFence.activeEpoch
    });
    current.notifyUiState(current.muted ? 'muted' : (current.isPlaying ? 'speaking' : 'listening'));
    return current.muted;
  };

  const interruptPlayback = async (reason = 'manual_interrupt') => {
    const current = duplexState;
    if (!current || current.closing) return null;
    const targetEpoch = current.activeResponseEpoch || current.epochFence.activeEpoch;
    const targetResponseId = current.activeResponseId;
    const playedSamples = current.playback?.playedSamples ?? 0;

    // 1. Locally suppress current response & clear audio immediately (zero round-trip wait).
    current.epochFence.suppress(targetEpoch);
    current.isPlaying = false;
    current.bargeInOnsetStartedAt = 0;
    current.playback?.clear?.(true);

    // 2. Send authoritative interrupt request to gateway.
    const requestId = `int-${crypto.randomUUID()}`;
    current.sendControl({
      type: 'response.interrupt',
      requestId,
      responseId: targetResponseId,
      speechEpoch: targetEpoch,
      playedSamples,
      reason
    });
    if (current.diagnostics) {
      current.diagnostics.interruptsSent++;
      current.diagnostics.lastInterrupt = { requestId, responseId: targetResponseId, speechEpoch: targetEpoch, playedSamples, reason };
    }
    current.notifyUiState(current.muted ? 'muted' : 'listening');
    return { requestId, responseId: targetResponseId, speechEpoch: targetEpoch, playedSamples };
  };

  const startDuplex = async (dotnet, sessionId, speechEpoch, endpoint, ticket, connectionId, options = null) => {
    await stop(false, 'duplex_started');
    await stopSession(false, 'restarted');

    const voiceConnectionId = connectionId || `vc-${crypto.randomUUID()}`;
    const captureId = crypto.randomUUID();
    const url = toWs(endpoint || `${window.location.origin}/openconnect/v1/voice/realtime/${encodeURIComponent(sessionId)}`);
    const protocols = ticket ? ['openconnect.voice.v2', `oc-ticket.${ticket}`] : 'openconnect.voice.v2';
    const socket = new WebSocket(url, protocols);
    socket.binaryType = 'arraybuffer';

    let stream, context, source, captureNode, playback;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }
      });
      context = new AudioContext();
      await context.resume();
      if (context.state !== 'running') throw new Error('voice_audio_context_not_running');
      await context.audioWorklet.addModule(workletUrl());
      source = context.createMediaStreamSource(stream);
      captureNode = new AudioWorkletNode(context, 'openconnect-pcm-capture', {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        channelCount: 1
      });
    } catch (error) {
      try { captureNode?.disconnect?.(); source?.disconnect?.(); } catch (_) { }
      try { await context?.close?.(); } catch (_) { }
      try { stream?.getTracks?.().forEach(track => track.stop()); } catch (_) { }
      try { socket.close(); } catch (_) { }
      throw error;
    }

    let captureReadyResolve;
    const captureReady = new Promise(resolve => { captureReadyResolve = resolve; });
    const epochFence = createEpochFence(speechEpoch || 0);
    const bargeInDebounceMs = Number(window.__OC_VOICE_BARGE_IN_DEBOUNCE_MS ?? options?.bargeInDebounceMs ?? 120);

    const diagnostics = {
      mode: 'v2-duplex',
      voiceConnectionId,
      captureId,
      utteranceCount: 0,
      inputSampleRate: context.sampleRate,
      playbackMode: null,
      sentFrames: 0,
      sentBytes: 0,
      receivedAudioFrames: 0,
      rejectedFrames: 0,
      staleFramesRejected: 0,
      interruptsSent: 0,
      lastInterrupt: null,
      receiptsSent: 0,
      underruns: 0,
      sileroReady: false,
      stopReason: null,
      startedAt: Date.now(),
      stoppedAt: null
    };
    window.__ocVoiceDiagnostics = diagnostics;

    const current = {
      dotnet,
      sessionId,
      voiceConnectionId,
      captureId,
      stream,
      context,
      source,
      captureNode,
      playback: null,
      socket,
      diagnostics,
      epochFence,
      controlSequence: 0,
      micSequence: 0,
      micSampleOffset: 0,
      micStreamId: 1,
      utteranceCount: 0,
      activeResponseId: null,
      activeResponseEpoch: 0,
      isPlaying: false,
      muted: false,
      closing: false,
      transportOpened: false,
      speechActive: false,
      bargeInOnsetStartedAt: 0,
      lastUiState: null,
      lastPartialText: '',
      pendingFrames: [],
      preRollPcm: [],
      silero: null,
      sileroBuffer16k: [],
      sileroQueue: Promise.resolve(),
      sileroSpeaking: false,
      smoothedLevel: 0,
      sendControl(payload) {
        if (socket.readyState !== WebSocket.OPEN) return false;
        const envelope = {
          version: V2_VERSION,
          voiceConnectionId: current.voiceConnectionId,
          sequence: ++current.controlSequence,
          ...payload
        };
        socket.send(JSON.stringify(envelope));
        return true;
      },
      notifyUiState(nextState, detail = null) {
        if (current.closing || (nextState === current.lastUiState && detail === null)) return;
        current.lastUiState = nextState;
        try {
          dotnet?.invokeMethodAsync?.('OnDuplexVoiceStateChanged', nextState, current.epochFence.activeEpoch, detail);
        } catch (_) { }
      }
    };
    duplexState = current;

    playback = await createPlaybackEngine(
      context,
      (receipt) => {
        if (duplexState !== current || current.closing) return;
        if (!current.activeResponseId || !current.epochFence.shouldAccept(receipt.speechEpoch)) return;
        current.diagnostics.underruns = receipt.underruns || 0;
        current.diagnostics.receiptsSent++;
        current.sendControl({
          type: 'response.audio.played',
          responseId: receipt.responseId || current.activeResponseId,
          speechEpoch: receipt.speechEpoch || current.activeResponseEpoch,
          playedSamples: receipt.playedSamples || 0
        });
      },
      (drained) => {
        if (duplexState !== current || current.closing) return;
        current.isPlaying = false;
        current.sendControl({
          type: 'response.playback.completed',
          responseId: drained.responseId || current.activeResponseId,
          speechEpoch: drained.speechEpoch || current.activeResponseEpoch,
          playedSamples: drained.playedSamples || 0,
          completed: true
        });
        current.notifyUiState(current.muted ? 'muted' : 'listening');
      },
      () => {
        if (duplexState !== current || current.closing) return;
        current.isPlaying = false;
      }
    );
    current.playback = playback;
    diagnostics.playbackMode = playback.mode;

    createSileroDetector().then(detector => {
      if (duplexState !== current || current.closing) {
        detector?.destroy?.();
        return;
      }
      if (detector) {
        current.silero = detector;
        current.diagnostics.sileroReady = true;
      }
    }).catch(() => { });

    const enqueueOutboundPcm = (pcmBytes) => {
      const sampleCount = pcmBytes.byteLength >> 1;
      const frame = encodeV2Frame({
        streamKind: V2_STREAM_KIND_MIC,
        streamId: current.micStreamId,
        speechEpoch: current.epochFence.activeEpoch,
        sequence: ++current.micSequence,
        sampleOffset: current.micSampleOffset
      }, pcmBytes);
      current.micSampleOffset += sampleCount;
      if (!frame) return;

      if (socket.readyState === WebSocket.OPEN && current.transportOpened) {
        if (socket.bufferedAmount < 512 * 1024) socket.send(frame);
      } else if (current.pendingFrames.length < maxPendingFrames) {
        current.pendingFrames.push(frame);
      }
      current.diagnostics.sentFrames++;
      current.diagnostics.sentBytes += frame.byteLength;
    };

    const handleSpeechActivity = (isSpeech) => {
      if (duplexState !== current || current.closing || current.muted) return;
      const now = Date.now();
      if (isSpeech) {
        current.sileroSpeaking = true;
        if (current.isPlaying) {
          if (current.bargeInOnsetStartedAt === 0) {
            current.bargeInOnsetStartedAt = now;
            return;
          }
          if (now - current.bargeInOnsetStartedAt < bargeInDebounceMs) {
            return;
          }
          interruptPlayback('barge_in');
          // Flush retained pre-roll immediately so first words of the interruption are not dropped.
          for (const preRollBytes of current.preRollPcm.splice(0)) {
            enqueueOutboundPcm(preRollBytes);
          }
        }
        if (!current.speechActive) {
          current.speechActive = true;
          current.sendControl({
            type: 'speech.started',
            captureId: current.captureId,
            speechEpoch: current.epochFence.activeEpoch,
            authoritative: false
          });
          if (current.preRollPcm.length > 0) {
            for (const preRollBytes of current.preRollPcm.splice(0)) {
              enqueueOutboundPcm(preRollBytes);
            }
          }
        }
      } else {
        current.sileroSpeaking = false;
        current.bargeInOnsetStartedAt = 0;
      }
    };

    const openTransport = () => {
      if (duplexState !== current || current.transportOpened || socket.readyState !== WebSocket.OPEN) return;
      current.transportOpened = true;
      current.sendControl({
        type: 'session.open',
        sessionId,
        mode: 'duplex',
        codec: 'pcm16le',
        inputSampleRateHz: targetSampleRate,
        outputSampleRateHz: targetSampleRate,
        channels: 1,
        capabilities: ['streaming_playback', 'barge_in', 'vad_onset']
      });
      for (const frame of current.pendingFrames.splice(0)) {
        if (socket.bufferedAmount < 512 * 1024) socket.send(frame);
      }
    };
    socket.onopen = openTransport;

    socket.onmessage = async (event) => {
      if (duplexState !== current || current.closing) return;
      if (event.data instanceof ArrayBuffer || ArrayBuffer.isView(event.data)) {
        const decoded = decodeV2Frame(event.data);
        if (!decoded || decoded.header.streamKind !== V2_STREAM_KIND_TTS) {
          current.diagnostics.rejectedFrames++;
          return;
        }
        if (!current.epochFence.shouldAccept(decoded.header.speechEpoch)) {
          current.diagnostics.staleFramesRejected++;
          return;
        }
        current.diagnostics.receivedAudioFrames++;
        if (!current.isPlaying) {
          current.isPlaying = true;
          current.notifyUiState('speaking');
        }
        current.playback.push(decoded.payload, decoded.header.speechEpoch);
        return;
      }

      try {
        const message = JSON.parse(event.data);
        switch (message.type) {
          case 'session.ready':
            if (message.voiceConnectionId) current.voiceConnectionId = message.voiceConnectionId;
            if (typeof message.speechEpoch === 'number') current.epochFence.advanceTo(message.speechEpoch);
            current.notifyUiState(current.muted ? 'muted' : 'listening');
            break;

          case 'transcript.partial': {
            const partialText = message.text || '';
            if (partialText !== current.lastPartialText) {
              current.lastPartialText = partialText;
              await dotnet?.invokeMethodAsync?.('OnVoicePartial', partialText, message.speechEpoch ?? current.epochFence.activeEpoch);
            }
            break;
          }

          case 'transcript.final': {
            const finalText = (message.text || '').trim();
            current.lastPartialText = '';
            if (message.authoritative !== false && finalText) {
              current.notifyUiState('thinking');
              await dotnet?.invokeMethodAsync?.('OnDuplexVoiceFinal', finalText, message.speechEpoch ?? current.epochFence.activeEpoch);
            }
            break;
          }

          case 'response.started': {
            const respEpoch = typeof message.speechEpoch === 'number' ? message.speechEpoch : current.epochFence.activeEpoch;
            if (!current.epochFence.shouldAccept(respEpoch)) return;
            current.epochFence.advanceTo(respEpoch);
            current.activeResponseId = message.responseId || null;
            current.activeResponseEpoch = respEpoch;
            current.playback.reset(respEpoch, current.activeResponseId);
            current.notifyUiState('thinking');
            break;
          }

          case 'response.completed':
            if (typeof message.speechEpoch === 'number' && !current.epochFence.shouldAccept(message.speechEpoch)) return;
            current.playback.drain();
            break;

          case 'response.cancelled':
            if (typeof message.speechEpoch === 'number') {
              current.epochFence.advanceTo(message.speechEpoch);
            }
            current.isPlaying = false;
            current.activeResponseId = null;
            current.playback.clear(true);
            current.notifyUiState(current.muted ? 'muted' : 'listening');
            break;

          case 'confirmation.required':
            current.notifyUiState('awaiting_confirmation', message.text || null);
            break;

          case 'session.ping':
            current.sendControl({ type: 'session.pong' });
            break;

          case 'error':
            await stopSession(false, 'duplex_error');
            await dotnet?.invokeMethodAsync?.('OnDuplexVoiceError', message.reason || 'Live voice session encountered an error. Text input remains available.');
            break;
        }
      } catch (_) { }
    };

    socket.onerror = async () => {
      captureReadyResolve();
      if (duplexState !== current || current.closing) return;
      await stopSession(false, 'socket_error');
      try { await dotnet?.invokeMethodAsync?.('OnDuplexVoiceError', 'Live voice connection is unavailable. Text input remains available.'); } catch (_) { }
    };

    socket.onclose = async (event) => {
      captureReadyResolve();
      if (duplexState !== current || current.closing) return;
      await stopSession(false, 'socket_closed');
      try {
        if (event?.code === 1000) await dotnet?.invokeMethodAsync?.('OnDuplexVoiceStopped');
        else await dotnet?.invokeMethodAsync?.('OnDuplexVoiceError', 'Live voice connection closed. Text input remains available.');
      } catch (_) { }
    };

    captureNode.port.onmessage = (event) => {
      if (event.data && event.data.type === 'ready') {
        captureReadyResolve();
        return;
      }
      if (duplexState !== current || current.closing || current.muted) return;
      const sourceSamples = event.data instanceof Float32Array ? event.data : new Float32Array(event.data);
      if (!sourceSamples.length) return;

      let energy = 0;
      for (let i = 0; i < sourceSamples.length; i++) {
        energy += sourceSamples[i] * sourceSamples[i];
      }
      const rms = Math.sqrt(energy / sourceSamples.length);
      const normalized = Math.max(0, Math.min(1, (rms - 0.008) / 0.16));
      current.smoothedLevel = current.smoothedLevel * 0.58 + normalized * 0.42;
      setVoiceLevel(current.smoothedLevel < 0.025 ? 0 : current.smoothedLevel);

      const samples24k = resample(sourceSamples, context.sampleRate, targetSampleRate);
      if (!samples24k.length) return;
      const bytes24k = pcm16(samples24k);

      // Keep bounded pre-roll ring when idle or during playback so barge-in onset isn't clipped.
      if (!current.speechActive || current.isPlaying) {
        current.preRollPcm.push(bytes24k);
        while (current.preRollPcm.length > maxPreRollFrames) current.preRollPcm.shift();
      }

      if (current.silero) {
        if (current.speechActive && !current.isPlaying) {
          enqueueOutboundPcm(bytes24k);
        }
        const samples16k = resample(sourceSamples, context.sampleRate, sileroSampleRate);
        for (let i = 0; i < samples16k.length; i++) current.sileroBuffer16k.push(samples16k[i]);
        while (current.sileroBuffer16k.length >= sileroFrameSamples) {
          const frame512 = Float32Array.from(current.sileroBuffer16k.splice(0, sileroFrameSamples));
          current.sileroQueue = current.sileroQueue.then(async () => {
            if (duplexState !== current || current.closing || !current.silero) return;
            const probability = await current.silero.analyze16k(frame512);
            const threshold = current.sileroSpeaking ? sileroContinueThreshold : sileroPositiveThreshold;
            const isSpeech = probability >= threshold || (window.__OC_VOICE_SPEECH_THRESHOLD && rms >= speechThreshold);
            handleSpeechActivity(isSpeech);
          }).catch(() => {
            handleSpeechActivity(rms >= speechThreshold);
          });
        }
      } else {
        handleSpeechActivity(rms >= speechThreshold);
        if (!current.isPlaying) {
          enqueueOutboundPcm(bytes24k);
        }
      }
    };

    source.connect(captureNode);
    captureNode.connect(context.destination);
    await captureReady;
    if (duplexState !== current) throw new Error('voice_socket_closed');
    if (window.__ocTestMicrophone?.start) window.__ocTestMicrophone.start();
    if (socket.readyState === WebSocket.CLOSED || socket.readyState === WebSocket.CLOSING) {
      await stopSession(false, 'socket_closed');
      throw new Error('voice_socket_closed');
    }
    if (socket.readyState !== WebSocket.OPEN) {
      try { await waitForSocket(socket); }
      catch (error) { await stopSession(false, 'socket_unavailable'); throw error; }
    }
    openTransport();
    return { voiceConnectionId: current.voiceConnectionId, playbackMode: playback.mode };
  };

  window.__ocVoiceTestHooks = {
    pcm16,
    resample,
    createSileroDetector,
    encodeV2Frame,
    decodeV2Frame,
    createEpochFence,
    getDuplexState: () => duplexState
  };

  return {
    start,
    stop,
    abort: async () => {
      await stop(false, 'availability_lost', true);
      await stopSession(false, 'availability_lost');
    },
    startDuplex,
    endUtterance,
    setMuted,
    interruptPlayback,
    stopSession
  };
})();
