class OpenConnectPcmCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.port.postMessage({ type: 'ready' });
  }

  process(inputs) {
    const input = inputs[0];
    const channel = input?.[0];
    if (channel && channel.length > 0) {
      this.port.postMessage(channel.slice());
    }
    return true;
  }
}

registerProcessor('openconnect-pcm-capture', OpenConnectPcmCaptureProcessor);
