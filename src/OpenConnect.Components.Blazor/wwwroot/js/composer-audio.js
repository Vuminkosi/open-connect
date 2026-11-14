/* Server-produced PCM playback for Kokoro. No browser speech synthesis fallback. */
(function () {
    let context = null;
    let source = null;
    let playbackEpoch = 0;
    let activeResolve = null;

    function decodeBase64(value) {
        const binary = atob(value);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    function stop() {
        playbackEpoch++;
        if (activeResolve) {
            activeResolve();
            activeResolve = null;
        }
        if (source) {
            try { source.stop(); } catch (_) { }
            source.disconnect();
            source = null;
        }
    }

    async function playPcm(base64, sampleRate, channels, epoch) {
        if (!base64 || sampleRate !== 24000 || channels !== 1 || !epoch) return;
        const localEpoch = playbackEpoch;
        context = context || new AudioContext({ sampleRate });
        if (context.state === 'suspended') await context.resume();
        const bytes = decodeBase64(base64);
        if ((bytes.byteLength & 1) !== 0) throw new Error('invalid_pcm16');
        const samples = new Float32Array(bytes.byteLength / 2);
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        for (let i = 0; i < samples.length; i++) samples[i] = view.getInt16(i * 2, true) / 32768;
        if (localEpoch !== playbackEpoch) return;
        const buffer = context.createBuffer(1, samples.length, sampleRate);
        buffer.copyToChannel(samples, 0);
        source = context.createBufferSource();
        source.buffer = buffer;
        source.connect(context.destination);
        await new Promise(resolve => {
            activeResolve = resolve;
            source.onended = () => {
                if (source) { source.disconnect(); source = null; }
                if (activeResolve) { activeResolve(); activeResolve = null; }
            };
            source.start();
        });
    }

    window.ocComposerAudio = { playPcm, stop };
})();
