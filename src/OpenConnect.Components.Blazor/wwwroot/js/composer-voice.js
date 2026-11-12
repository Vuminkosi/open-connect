window.ocComposerVoice = (() => {
  let state = null;
  const toWs = (url) => url.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:');
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
    if (from === to) return input;
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
  const stop = async (notify = true) => {
    const current = state;
    state = null;
    if (!current) return;
    clearTimeout(current.silenceTimer);
    try { if (current.socket.readyState === WebSocket.OPEN) current.socket.send(JSON.stringify({ type: 'endStream' })); } catch (_) { }
    try { current.processor.disconnect(); current.source.disconnect(); current.context.close(); } catch (_) { }
    try { current.stream.getTracks().forEach(track => track.stop()); } catch (_) { }
    if (notify) try { await current.dotnet.invokeMethodAsync('OnVoiceStopped'); } catch (_) { }
  };
  const start = async (dotnet, sessionId, speechEpoch, endpoint) => {
    await stop(false);
    const captureId = crypto.randomUUID();
    const url = toWs(endpoint || `${window.location.origin}/openconnect/v1/voice/realtime/${encodeURIComponent(sessionId)}`);
    const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    const context = new AudioContext();
    const source = context.createMediaStreamSource(stream);
    const processor = context.createScriptProcessor(4096, 1, 1);
    const socket = new WebSocket(url, 'openconnect.voice.v1');
    socket.binaryType = 'arraybuffer';
    const current = { dotnet, captureId, speechEpoch, stream, context, source, processor, socket, silenceTimer: null, speechSeen: false };
    state = current;
    socket.onopen = () => socket.send(JSON.stringify({ type: 'open', sessionId, captureId, speechEpoch, sampleRate: 24000, channels: 1, bitsPerSample: 16 }));
    socket.onmessage = async event => {
      try {
        const message = JSON.parse(event.data);
        if (message.type === 'transcript-partial') await dotnet.invokeMethodAsync('OnVoicePartial', message.text || '', speechEpoch);
        if (message.type === 'transcript-final' && message.authoritative) await dotnet.invokeMethodAsync('OnVoiceFinal', message.text || '', speechEpoch);
      } catch (_) { }
    };
    socket.onerror = async () => { try { await dotnet.invokeMethodAsync('OnVoiceError', 'Voice transcription is unavailable. Text input remains available.'); } catch (_) { } await stop(false); };
    socket.onclose = () => { if (state === current) state = null; };
    processor.onaudioprocess = event => {
      if (!state || socket.readyState !== WebSocket.OPEN) return;
      const sourceSamples = event.inputBuffer.getChannelData(0);
      let energy = 0;
      for (let i = 0; i < sourceSamples.length; i++) energy += sourceSamples[i] * sourceSamples[i];
      const rms = Math.sqrt(energy / sourceSamples.length);
      const isSpeech = rms >= 0.012;
      if (isSpeech) { current.speechSeen = true; clearTimeout(current.silenceTimer); }
      else if (current.speechSeen && !current.silenceTimer) current.silenceTimer = setTimeout(() => stop(false), 650);
      const samples = resample(sourceSamples, context.sampleRate, 24000);
      if (samples.length > 0 && socket.bufferedAmount < 512 * 1024) socket.send(pcm16(samples));
    };
    source.connect(processor);
    processor.connect(context.destination);
  };
  return { start, stop };
})();
