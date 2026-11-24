class OpenConnectPcmPlaybackProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const opts = options?.processorOptions ?? {};
    this._sourceSampleRate = Number(opts.sourceSampleRate) || 24000;
    const maxBufferMs = Math.max(100, Math.min(2000, Number(opts.maxBufferMs) || 500));
    const initialBufferMs = Math.max(0, Math.min(maxBufferMs, Number(opts.initialBufferMs) || 60));
    const receiptIntervalMs = Math.max(40, Math.min(1000, Number(opts.receiptIntervalMs) || 120));
    const fadeOutMs = Math.max(1, Math.min(20, Number(opts.fadeOutMs) || 5));

    this._capacity = Math.max(256, Math.ceil((sampleRate * maxBufferMs) / 1000));
    this._ring = new Float32Array(this._capacity);
    this._readIndex = 0;
    this._writeIndex = 0;
    this._buffered = 0;

    this._initialBufferSamples = Math.ceil((sampleRate * initialBufferMs) / 1000);
    this._receiptIntervalSamples = Math.max(128, Math.ceil((sampleRate * receiptIntervalMs) / 1000));
    this._fadeOutTotalSamples = Math.max(1, Math.round((sampleRate * fadeOutMs) / 1000));

    this._speechEpoch = 0;
    this._responseId = null;
    this._startedPlayback = false;
    this._draining = false;
    this._totalSourceSamplesPushed = 0;
    this._renderedDeviceSamples = 0;
    this._samplesSinceLastReceipt = 0;
    this._lastReportedPlayedSamples = 0;
    this._underruns = 0;
    this._droppedSamples = 0;
    this._lastSample = 0;
    this._fadeOutRemaining = 0;
    this._fadeOutStartSample = 0;

    this.port.onmessage = (event) => this._onMessage(event.data);
    this.port.postMessage({ type: 'ready', sampleRate, capacity: this._capacity });
  }

  _playedSourceSamples() {
    if (this._renderedDeviceSamples <= 0 || this._totalSourceSamplesPushed <= 0) {
      return 0;
    }
    const scaled = Math.floor((this._renderedDeviceSamples * this._sourceSampleRate) / sampleRate);
    return Math.min(this._totalSourceSamplesPushed, scaled);
  }

  _onMessage(msg) {
    if (!msg || typeof msg !== 'object') return;
    switch (msg.type) {
      case 'reset':
        this._clearBuffer(false);
        this._speechEpoch = Number(msg.speechEpoch) || 0;
        this._responseId = msg.responseId ?? null;
        this._totalSourceSamplesPushed = 0;
        this._renderedDeviceSamples = 0;
        this._samplesSinceLastReceipt = 0;
        this._lastReportedPlayedSamples = 0;
        this._underruns = 0;
        this._droppedSamples = 0;
        this._startedPlayback = false;
        this._draining = false;
        break;

      case 'push':
        if (msg.speechEpoch !== undefined && this._speechEpoch !== 0 && msg.speechEpoch !== this._speechEpoch) {
          return;
        }
        this._enqueue(msg);
        break;

      case 'drain':
        this._draining = true;
        if (this._buffered === 0) {
          this._emitDrained();
        }
        break;

      case 'clear': {
        const playedSamples = this._playedSourceSamples();
        const underruns = this._underruns;
        this._clearBuffer(msg.fadeOut !== false);
        this._startedPlayback = false;
        this._draining = false;
        this.port.postMessage({
          type: 'cleared',
          playedSamples,
          underruns,
          speechEpoch: this._speechEpoch,
          responseId: this._responseId
        });
        break;
      }

      case 'stats':
        this.port.postMessage({
          type: 'receipt',
          playedSamples: this._playedSourceSamples(),
          underruns: this._underruns,
          droppedSamples: this._droppedSamples,
          bufferedSamples: this._buffered,
          speechEpoch: this._speechEpoch,
          responseId: this._responseId
        });
        break;
    }
  }

  _clearBuffer(fadeOut) {
    this._readIndex = 0;
    this._writeIndex = 0;
    this._buffered = 0;
    if (fadeOut && Math.abs(this._lastSample) > 1e-5) {
      this._fadeOutStartSample = this._lastSample;
      this._fadeOutRemaining = this._fadeOutTotalSamples;
    } else {
      this._fadeOutRemaining = 0;
      this._fadeOutStartSample = 0;
      this._lastSample = 0;
    }
  }

  _enqueue(msg) {
    let sourceSamples = null;
    if (msg.samples instanceof Float32Array) {
      sourceSamples = msg.samples;
    } else if (msg.pcm16 instanceof ArrayBuffer || ArrayBuffer.isView(msg.pcm16)) {
      const bytes = msg.pcm16 instanceof ArrayBuffer
        ? new Uint8Array(msg.pcm16)
        : new Uint8Array(msg.pcm16.buffer, msg.pcm16.byteOffset, msg.pcm16.byteLength);
      const count = bytes.byteLength >> 1;
      if (count <= 0) return;
      const view = new DataView(bytes.buffer, bytes.byteOffset, count * 2);
      sourceSamples = new Float32Array(count);
      for (let i = 0; i < count; i++) {
        sourceSamples[i] = view.getInt16(i * 2, true) / 32768;
      }
    }
    if (!sourceSamples || sourceSamples.length === 0) return;

    this._totalSourceSamplesPushed += sourceSamples.length;
    const inputRate = Number(msg.sampleRate) || this._sourceSampleRate;
    const deviceSamples = this._resample(sourceSamples, inputRate, sampleRate);

    for (let i = 0; i < deviceSamples.length; i++) {
      if (this._buffered >= this._capacity) {
        this._droppedSamples += deviceSamples.length - i;
        break;
      }
      this._ring[this._writeIndex] = deviceSamples[i];
      this._writeIndex = (this._writeIndex + 1) % this._capacity;
      this._buffered++;
    }
  }

  _resample(input, fromRate, toRate) {
    if (fromRate === toRate || input.length === 0) return input;
    const outLength = Math.max(1, Math.round((input.length * toRate) / fromRate));
    const output = new Float32Array(outLength);
    const ratio = fromRate / toRate;
    for (let i = 0; i < outLength; i++) {
      const pos = i * ratio;
      const idx = Math.floor(pos);
      const frac = pos - idx;
      const a = input[Math.min(idx, input.length - 1)];
      const b = input[Math.min(idx + 1, input.length - 1)];
      output[i] = a + (b - a) * frac;
    }
    return output;
  }

  _emitDrained() {
    this._draining = false;
    this._startedPlayback = false;
    const playedSamples = this._playedSourceSamples();
    this._lastReportedPlayedSamples = playedSamples;
    this._samplesSinceLastReceipt = 0;
    this.port.postMessage({
      type: 'drained',
      playedSamples,
      underruns: this._underruns,
      droppedSamples: this._droppedSamples,
      speechEpoch: this._speechEpoch,
      responseId: this._responseId
    });
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    if (!output || output.length === 0) return true;
    const primary = output[0];
    const frameLength = primary.length;

    if (this._fadeOutRemaining > 0) {
      for (let i = 0; i < frameLength; i++) {
        if (this._fadeOutRemaining > 0) {
          const gain = this._fadeOutRemaining / this._fadeOutTotalSamples;
          primary[i] = this._fadeOutStartSample * gain;
          this._fadeOutRemaining--;
        } else {
          primary[i] = 0;
        }
      }
      if (this._fadeOutRemaining === 0) {
        this._lastSample = 0;
      }
      this._copyToExtraChannels(output, primary);
      return true;
    }

    if (!this._startedPlayback) {
      if (this._buffered >= this._initialBufferSamples || (this._draining && this._buffered > 0)) {
        this._startedPlayback = true;
      } else {
        primary.fill(0);
        this._copyToExtraChannels(output, primary);
        return true;
      }
    }

    let renderedInBlock = 0;
    let hadUnderrun = false;
    for (let i = 0; i < frameLength; i++) {
      if (this._buffered > 0) {
        const sample = this._ring[this._readIndex];
        this._readIndex = (this._readIndex + 1) % this._capacity;
        this._buffered--;
        primary[i] = sample;
        this._lastSample = sample;
        renderedInBlock++;
      } else {
        primary[i] = 0;
        this._lastSample = 0;
        if (!this._draining) {
          hadUnderrun = true;
        }
      }
    }

    if (renderedInBlock > 0) {
      this._renderedDeviceSamples += renderedInBlock;
      this._samplesSinceLastReceipt += renderedInBlock;
    }

    if (hadUnderrun) {
      this._underruns++;
    }

    this._copyToExtraChannels(output, primary);

    if (this._draining && this._buffered === 0) {
      this._emitDrained();
      return true;
    }

    if (this._samplesSinceLastReceipt >= this._receiptIntervalSamples) {
      this._samplesSinceLastReceipt = 0;
      const playedSamples = this._playedSourceSamples();
      if (playedSamples > this._lastReportedPlayedSamples || hadUnderrun) {
        this._lastReportedPlayedSamples = playedSamples;
        this.port.postMessage({
          type: 'receipt',
          playedSamples,
          underruns: this._underruns,
          droppedSamples: this._droppedSamples,
          bufferedSamples: this._buffered,
          speechEpoch: this._speechEpoch,
          responseId: this._responseId
        });
      }
    }

    return true;
  }

  _copyToExtraChannels(output, primary) {
    for (let ch = 1; ch < output.length; ch++) {
      output[ch].set(primary);
    }
  }
}

registerProcessor('openconnect-pcm-playback', OpenConnectPcmPlaybackProcessor);
