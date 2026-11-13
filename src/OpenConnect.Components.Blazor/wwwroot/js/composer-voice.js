window.ocComposerVoice = (() => {
  let state = null;
  const targetSampleRate = 24000;
  const maxPendingFrames = 256;
  const toWs = (url) => url.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:');
  const workletUrl = () => new URL('_content/OpenConnect.Components.Blazor/js/voice-capture-worklet.js', document.baseURI).href;

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

  const stop = async (notify = true, reason = 'stopped') => {
    const current = state;
    state = null;
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
      if (current.socket.readyState === WebSocket.OPEN) {
        for (const bytes of current.pendingPcm.splice(0)) current.socket.send(bytes);
        current.socket.send(JSON.stringify({ type: 'endStream' }));
      }
    } catch (_) { }
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

  const start = async (dotnet, sessionId, speechEpoch, endpoint) => {
    await stop(false, 'restarted');
    const captureId = crypto.randomUUID();
    const url = toWs(endpoint || `${window.location.origin}/openconnect/v1/voice/realtime/${encodeURIComponent(sessionId)}`);
    const socket = new WebSocket(url, 'openconnect.voice.v1');
    socket.binaryType = 'arraybuffer';

    const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    const track = stream.getAudioTracks()[0];
    const context = new AudioContext();
    await context.resume();
    if (context.state !== 'running') throw new Error('voice_audio_context_not_running');
    await context.audioWorklet.addModule(workletUrl());
    const source = context.createMediaStreamSource(stream);
    const processor = new AudioWorkletNode(context, 'openconnect-pcm-capture', { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1 });
    const diagnostics = window.__OC_VOICE_DIAGNOSTICS__ ? {
      captureId,
      streamId: stream.id,
      inputSampleRate: context.sampleRate,
      trackSettings: track?.getSettings?.() ?? {},
      trackReadyState: track?.readyState,
      processorType: 'AudioWorkletNode',
      processorReady: true,
      processorMessages: 0,
      sourceFrames: 0,
      sentFrames: 0,
      sentBytes: 0,
      rmsMax: 0,
      peakMax: 0,
      speechSeen: false,
      stopReason: null,
      startedAt: Date.now(),
      stoppedAt: null,
    } : null;
    window.__ocVoiceDiagnostics = diagnostics;
    if (window.__OC_VOICE_DIAGNOSTICS__) window.__ocVoiceTestHooks = { pcm16, resample };
    const current = { dotnet, captureId, speechEpoch, stream, context, source, processor, socket, diagnostics, maxTimer: null, silenceTimer: null, pendingPcm: [], pendingFrameCount: 0, endRequested: false, speechSeen: false, finalTranscript: '', transportOpened: false };
    state = current;

    const maxDurationMs = Number(window.__OC_VOICE_MAX_DURATION_MS || 0);
    if (maxDurationMs > 0) current.maxTimer = setTimeout(() => stop(false, 'max-duration'), maxDurationMs);

    const openTransport = () => {
      if (state !== current || current.transportOpened || socket.readyState !== WebSocket.OPEN) return;
      current.transportOpened = true;
      socket.send(JSON.stringify({ type: 'open', sessionId, captureId, speechEpoch, sampleRate: targetSampleRate, channels: 1, bitsPerSample: 16 }));
      for (const bytes of current.pendingPcm.splice(0)) socket.send(bytes);
      current.pendingFrameCount = 0;
      if (current.endRequested) socket.send(JSON.stringify({ type: 'endStream' }));
    };
    socket.onopen = openTransport;

    socket.onmessage = async event => {
      try {
        const message = JSON.parse(event.data);
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
      try { await dotnet.invokeMethodAsync('OnVoiceError', 'Voice transcription is unavailable. Text input remains available.'); } catch (_) { }
      await stop(false, 'socket_error');
    };
    socket.onclose = () => { if (state === current) state = null; };

    processor.port.onmessage = event => {
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
      if (current.diagnostics) {
        current.diagnostics.processorMessages++;
        current.diagnostics.sourceFrames += sourceSamples.length;
        current.diagnostics.rmsMax = Math.max(current.diagnostics.rmsMax, rms);
        current.diagnostics.peakMax = Math.max(current.diagnostics.peakMax, peak);
      }
      const isSpeech = rms >= 0.012;
      if (isSpeech) {
        current.speechSeen = true;
        if (current.diagnostics) current.diagnostics.speechSeen = true;
        clearTimeout(current.silenceTimer);
        current.silenceTimer = null;
      } else if (current.speechSeen && !current.silenceTimer) {
        current.silenceTimer = setTimeout(() => stop(false, 'silence'), 650);
      }
      const samples = resample(sourceSamples, context.sampleRate, targetSampleRate);
      if (!samples.length || socket.bufferedAmount >= 512 * 1024) return;
      const bytes = pcm16(samples);
      if (socket.readyState === WebSocket.OPEN) socket.send(bytes);
      else if (current.pendingFrameCount < maxPendingFrames) {
        current.pendingPcm.push(bytes);
        current.pendingFrameCount++;
      }
      if (current.diagnostics) {
        current.diagnostics.sentFrames++;
        current.diagnostics.sentBytes += bytes.byteLength;
      }
    };

    source.connect(processor);
    processor.connect(context.destination);
    if (socket.readyState !== WebSocket.OPEN) await waitForSocket(socket);
    openTransport();
  };

  if (window.__OC_VOICE_DIAGNOSTICS__) window.__ocVoiceTestHooks = { pcm16, resample };
  return { start, stop };
})();
