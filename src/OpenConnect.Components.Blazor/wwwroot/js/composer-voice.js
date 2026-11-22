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
  const vadBaseUrl = () => new URL('_content/OpenConnect.Components.Blazor/vendor/vad/', document.baseURI).href;

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
    if (window.__OC_VOICE_DIAGNOSTICS__) window.__ocVoiceTestHooks = { pcm16, resample, createSileroDetector };
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

  if (window.__OC_VOICE_DIAGNOSTICS__) window.__ocVoiceTestHooks = { pcm16, resample, createSileroDetector };
  return { start, stop, abort: () => stop(false, 'availability_lost', true) };
})();
