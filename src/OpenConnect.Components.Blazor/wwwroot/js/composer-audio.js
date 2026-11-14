/* Server-produced PCM playback for Kokoro. No browser speech synthesis fallback. */
(function () {
    let context = null;
    let source = null;
    let playbackEpoch = 0;
    let activeResolve = null;
    let progressFrame = 0;

    function decodeBase64(value) {
        const binary = atob(value);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    function stop() {
        playbackEpoch++;
        if (progressFrame) cancelAnimationFrame(progressFrame);
        progressFrame = 0;
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

    async function playPcm(base64, sampleRate, channels, epoch, dotnet, startCharacter, endCharacter) {
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
        const activeSource = source;
        const duration = buffer.duration;
        await new Promise(resolve => {
            activeResolve = resolve;
            const startedAt = context.currentTime;
            let lastProgressAt = 0;
            const progress = (timestamp) => {
                if (localEpoch !== playbackEpoch) return;
                const elapsed = Math.min(context.currentTime - startedAt, duration);
                if (timestamp - lastProgressAt >= 250) {
                    lastProgressAt = timestamp;
                    dotnet?.invokeMethodAsync("OnSpeechPlaybackProgress", epoch, startCharacter, endCharacter, elapsed, duration).catch(() => {});
                }
                if (elapsed < duration) progressFrame = requestAnimationFrame(progress);
            };
            activeSource.onended = async () => {
                if (progressFrame) cancelAnimationFrame(progressFrame);
                progressFrame = 0;
                if (source === activeSource) { activeSource.disconnect(); source = null; }
                const finish = activeResolve;
                activeResolve = null;
                if (localEpoch === playbackEpoch && dotnet) {
                    try { await dotnet.invokeMethodAsync("OnSpeechPlaybackEnded", epoch, startCharacter, endCharacter, duration); }
                    catch { }
                }
                finish?.();
            };
            activeSource.start();
            void (async () => {
                try {
                    if (dotnet) {
                        await dotnet.invokeMethodAsync("OnSpeechPlaybackStarted", epoch, startCharacter, endCharacter, duration);
                        const synchronizationOffsetMilliseconds = Math.max(0, (context.currentTime - startedAt) * 1000);
                        await dotnet.invokeMethodAsync("OnSpeechPlaybackSynchronizationMeasured", epoch,
                            startCharacter, endCharacter, synchronizationOffsetMilliseconds);
                    }
                } catch { }
                if (localEpoch === playbackEpoch) progressFrame = requestAnimationFrame(progress);
            })();
        });
    }

    window.ocComposerAudio = { playPcm, stop };
})();
