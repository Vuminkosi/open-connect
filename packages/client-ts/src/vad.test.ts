import { describe, expect, it, vi } from "vitest";
import {
  OpenConnectClient,
  createOpenConnectClient,
  createClientVad,
} from "./index.js";

describe("createOpenConnectClient", () => {
  it("creates a client instance with apiBaseUrl and eventUrl", () => {
    const client = createOpenConnectClient({
      apiBaseUrl: "https://api.example.test/openconnect/v1",
      eventUrl: "wss://events.example.test/openconnect/v1/events",
      fetchImpl: vi.fn(),
    });

    expect(client).toBeInstanceOf(OpenConnectClient);
  });

  it("subscribes to events using custom eventUrl without duplicating /events", () => {
    let capturedUrl = "";
    const fakeSocket = {
      addEventListener: vi.fn(),
      close: vi.fn(),
    };
    const client = createOpenConnectClient({
      apiBaseUrl: "https://api.example.test/openconnect/v1",
      eventUrl: "wss://events.example.test/openconnect/v1/events",
      fetchImpl: vi.fn(),
      websocketImpl: (url: string) => {
        capturedUrl = url;
        return fakeSocket;
      },
    });

    const unsubscribe = client.subscribeEvents("session-123", {
      afterSequence: 5,
      onEvent: vi.fn(),
    });

    expect(capturedUrl).toBe(
      "wss://events.example.test/openconnect/v1/sessions/session-123/events?afterSequence=5",
    );
    unsubscribe();
    expect(fakeSocket.close).toHaveBeenCalledTimes(1);
  });
});

describe("createClientVad", () => {
  it("initializes cleanly with default options without DOM access", () => {
    const vad = createClientVad({
      engine: "silero-onnx",
      modelAssetBaseUrl: "/assets/openconnect/vad/",
      bargeIn: "local-fast",
      fallback: "none",
    });

    expect(vad.speechEpoch).toBe(0);
    expect(vad.isSpeechActive).toBe(false);
    expect(vad.options.engine).toBe("silero-onnx");
    expect(vad.options.bargeIn).toBe("local-fast");
    expect(vad.options.sampleRate).toBe(16000);
    expect(vad.options.frameSize).toBe(512);
  });

  it("detects silence and maintains zero probability without triggering barge-in", () => {
    const vad = createClientVad();
    const silence = new Float32Array(512); // All zeros

    for (let i = 0; i < 5; i++) {
      const result = vad.processFrame(silence);
      expect(result.isSpeech).toBe(false);
      expect(result.probability).toBeLessThan(0.1);
      expect(result.speechEpoch).toBe(0);
      expect(result.bargeInTriggered).toBe(false);
    }

    expect(vad.isSpeechActive).toBe(false);
    expect(vad.preRollFrames.length).toBe(5);
  });

  it("detects speech candidate, enters speech active state, and triggers local fast barge-in epoch increment", () => {
    const vad = createClientVad({
      bargeIn: "local-fast",
      speechThreshold: 0.5,
    });

    // Synthesize 512-sample loud tone (e.g. 440 Hz at 0.5 amplitude, RMS ~ 0.35)
    const voiceFrame = new Float32Array(512);
    for (let i = 0; i < voiceFrame.length; i++) {
      voiceFrame[i] = 0.5 * Math.sin((2 * Math.PI * 440 * i) / 16000);
    }

    const result = vad.processFrame(voiceFrame);
    expect(result.isSpeech).toBe(true);
    expect(result.probability).toBeGreaterThan(0.8);
    expect(result.bargeInTriggered).toBe(true);
    expect(result.speechEpoch).toBe(1);
    expect(vad.isSpeechActive).toBe(true);

    // Second consecutive frame within speech does not increment epoch again
    const secondResult = vad.processFrame(voiceFrame);
    expect(secondResult.isSpeech).toBe(true);
    expect(secondResult.bargeInTriggered).toBe(false);
    expect(secondResult.speechEpoch).toBe(1);
  });

  it("resamples 24 kHz audio down to 16 kHz and tracks sample offset", () => {
    const vad = createClientVad({ sampleRate: 16000 });
    // 768 samples at 24 kHz = 512 samples at 16 kHz
    const input24k = new Float32Array(768);
    for (let i = 0; i < input24k.length; i++) {
      input24k[i] = 0.4 * Math.sin((2 * Math.PI * 300 * i) / 24000);
    }

    const result = vad.processFrame(input24k, 24000);
    expect(result.sampleOffset).toBe(0);
    expect(vad.sampleOffset).toBe(768);
  });

  it("triggerBargeIn manually fences speech epoch", () => {
    const vad = createClientVad();
    expect(vad.speechEpoch).toBe(0);

    const newEpoch = vad.triggerBargeIn();
    expect(newEpoch).toBe(1);
    expect(vad.speechEpoch).toBe(1);
    expect(vad.isSpeechActive).toBe(true);
  });

  it("reset clears recurrent state and pre-roll queue", () => {
    const vad = createClientVad();
    const frame = new Float32Array(512).fill(0.2);
    vad.processFrame(frame);
    expect(vad.preRollFrames.length).toBeGreaterThan(0);

    vad.reset();
    expect(vad.preRollFrames.length).toBe(0);
    expect(vad.isSpeechActive).toBe(false);
    expect(vad.sampleOffset).toBe(0);
    expect(vad.frameIndex).toBe(0);
  });
});

describe("SSR and headless Node environment guarantees", () => {
  it("does not touch or define window, navigator, AudioContext, or WebAssembly on import", async () => {
    // Assert globals are not defined or required
    expect(typeof globalThis.window).toBe("undefined");
    expect(typeof (globalThis as Record<string, unknown>)["AudioContext"]).toBe("undefined");
    expect(typeof (globalThis as Record<string, unknown>)["webkitAudioContext"]).toBe("undefined");

    // Dynamic import to prove evaluation in pure Node environment succeeds without touching DOM
    const sdk = await import("./index.js");
    expect(sdk.createOpenConnectClient).toBeDefined();
    expect(sdk.createClientVad).toBeDefined();

    // Instantiate client and VAD
    const client = sdk.createOpenConnectClient({
      baseUrl: "https://api.example.test",
      fetchImpl: vi.fn(),
    });
    const vad = sdk.createClientVad();

    expect(client).toBeDefined();
    expect(vad).toBeDefined();
  });
});
