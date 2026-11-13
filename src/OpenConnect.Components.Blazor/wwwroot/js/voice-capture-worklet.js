class OpenConnectPcmCaptureProcessor extends AudioWorkletProcessor {
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
