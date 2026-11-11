/**
 * Mount/dispose/send coverage for `createMiniComposer` using a minimal
 * fake DOM (no browser, no happy-dom). The harness implements exactly the
 * DOM surface the composer touches, including `AbortSignal`-scoped listener
 * removal so disposal is verifiable.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMiniComposer, bubbleMetrics, clamp, type ComposerClient } from "./composer.js";
import type { ComposerSessionEvent } from "./state.js";

type Listener = (event: Record<string, unknown>) => void;

class FakeElement {
  readonly tagName: string;
  className = "";
  textContent: string | null = null;
  readonly dataset: Record<string, string> = {};
  readonly style: Record<string, string> = {};
  readonly attributes: Record<string, string> = {};
  hidden = false;
  disabled = false;
  value = "";
  rows = 0;
  maxLength = 0;
  placeholder = "";
  id = "";
  htmlFor = "";
  scrollTop = 0;
  scrollHeight = 0;
  focused = false;
  parentNode: FakeElement | null = null;
  readonly children: FakeElement[] = [];
  readonly listeners: Record<string, Listener[]> = {};

  shadowRoot: FakeElement | null = null;

  constructor(tag: string) {
    this.tagName = tag;
  }

  attachShadow(options: { mode: string }): FakeElement {
    const shadow = new FakeElement("#shadow-root");
    this.shadowRoot = shadow;
    shadow.parentNode = this;
    return shadow;
  }

  get firstChild(): FakeElement | null {
    return this.children[0] ?? null;
  }

  setAttribute(key: string, value: string): void {
    this.attributes[key] = value;
  }

  getAttribute(key: string): string | null {
    return this.attributes[key] ?? null;
  }

  appendChild<T extends FakeElement>(child: T): T {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }

  removeChild<T extends FakeElement>(child: T): T {
    const index = this.children.indexOf(child);
    if (index >= 0) this.children.splice(index, 1);
    child.parentNode = null;
    return child;
  }

  addEventListener(
    type: string,
    listener: Listener,
    options?: { signal?: AbortSignal },
  ): void {
    if (options?.signal?.aborted === true) return;
    const list = this.listeners[type] ?? [];
    list.push(listener);
    this.listeners[type] = list;
    options?.signal?.addEventListener("abort", () => {
      const current = this.listeners[type] ?? [];
      const index = current.indexOf(listener);
      if (index >= 0) current.splice(index, 1);
    });
  }

  removeEventListener(type: string, listener: Listener): void {
    const current = this.listeners[type] ?? [];
    const index = current.indexOf(listener);
    if (index >= 0) current.splice(index, 1);
  }

  focus(): void {
    this.focused = true;
  }

  fire(type: string, event: Record<string, unknown> = {}): void {
    for (const listener of [...(this.listeners[type] ?? [])]) listener(event);
  }

  listenerCount(type: string): number {
    return this.listeners[type]?.length ?? 0;
  }

  findByClass(className: string): FakeElement | null {
    if (this.className.split(" ").includes(className)) return this;
    for (const child of this.children) {
      const found = child.findByClass(className);
      if (found !== null) return found;
    }
    return null;
  }
}

interface FakeDom {
  body: FakeElement;
  document: FakeElement & {
    createElement(tag: string): FakeElement;
    activeElement: undefined;
  };
  window: FakeElement & { innerWidth: number; innerHeight: number; visualViewport?: undefined };
}

function installFakeDom(): FakeDom {
  const body = new FakeElement("body");
  const doc = new FakeElement("#document") as FakeElement & {
    createElement(tag: string): FakeElement;
    activeElement: undefined;
    body: FakeElement;
  };
  doc.createElement = (tag: string): FakeElement => new FakeElement(tag);
  doc.activeElement = undefined;
  doc.body = body;
  const win = new FakeElement("window") as FakeElement & {
    innerWidth: number;
    innerHeight: number;
    visualViewport?: undefined;
  };
  win.innerWidth = 1024;
  win.innerHeight = 768;
  win.visualViewport = undefined;
  const globals = globalThis as Record<string, unknown>;
  globals["document"] = doc;
  globals["window"] = win;
  return {
    body,
    document: doc as FakeDom["document"],
    window: win as FakeDom["window"],
  };
}

function removeFakeDom(): void {
  const globals = globalThis as Record<string, unknown>;
  delete globals["document"];
  delete globals["window"];
}

afterEach(() => {
  removeFakeDom();
});

function keyEvent(key: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    key,
    shiftKey: false,
    ctrlKey: false,
    metaKey: false,
    isComposing: false,
    repeat: false,
    preventDefault: vi.fn(),
    ...overrides,
  };
}

describe("createMiniComposer mount/dispose", () => {
  it("mounts a FAB and a hidden bubble; toggle opens it", () => {
    installFakeDom();
    const composer = createMiniComposer({ assistantName: "Ada", sessionId: "s-1" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);

    const host = doc.body.children[0];
    expect(host?.className).toBe("oc-composer-host");
    const fab = host?.findByClass("oc-composer-fab");
    const bubble = host?.findByClass("oc-composer-bubble");
    expect(fab?.getAttribute("aria-label")).toBe("Open Ada");
    expect(fab?.getAttribute("aria-expanded")).toBe("false");
    expect(bubble?.getAttribute("role")).toBe("dialog");
    expect(bubble?.hidden).toBe(true);

    fab?.fire("click");
    expect(composer.getState().displayState).toBe("open");
    expect(bubble?.hidden).toBe(false);
    expect(fab?.getAttribute("aria-expanded")).toBe("true");

    composer.dispose();
  });

  it("dispose removes nodes, listeners, and subscriptions and is idempotent", () => {
    installFakeDom();
    const unsubscribe = vi.fn();
    const subscribeEvents = vi.fn(() => unsubscribe);
    const client = { createTask: vi.fn(async () => ({})), subscribeEvents } as unknown as ComposerClient;
    const composer = createMiniComposer({ client, sessionId: "s-1" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    expect(subscribeEvents).toHaveBeenCalledTimes(1);
    expect(doc.body.children).toHaveLength(1);

    composer.dispose();
    expect(doc.body.children).toHaveLength(0);
    expect(unsubscribe).toHaveBeenCalledTimes(1);
    // Idempotent: second dispose neither throws nor repeats cleanup.
    expect(() => composer.dispose()).not.toThrow();
    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });

  it("second mount is a no-op (single FAB)", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    const body = doc.body as unknown as HTMLElement;
    composer.mount(body);
    composer.mount(body);
    expect(doc.body.children).toHaveLength(1);
    composer.dispose();
  });

  it("Escape closes the open bubble", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
      fire(type: string, event?: Record<string, unknown>): void;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    composer.open();
    expect(composer.getState().displayState).toBe("open");
    (doc as unknown as FakeElement).fire("keydown", keyEvent("Escape"));
    expect(composer.getState().displayState).toBe("closed");
    composer.dispose();
  });

  it("mounts inside ShadowRoot when useShadowRoot is enabled", () => {
    installFakeDom();
    const composer = createMiniComposer({
      sessionId: "s-1",
      useShadowRoot: true,
      shadowRootStyles: ".oc-composer-host { position: fixed; }",
    });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    expect(doc.body.children).toHaveLength(1);
    const shadowHost = doc.body.children[0];
    expect(shadowHost.className).toBe("oc-composer-shadow-host");
    expect(shadowHost.shadowRoot).not.toBeNull();
    expect(shadowHost.shadowRoot?.children.some((c) => c.className === "oc-composer-host")).toBe(true);
    expect(shadowHost.shadowRoot?.children.some((c) => c.tagName === "style")).toBe(true);

    composer.dispose();
    expect(doc.body.children).toHaveLength(0);
  });
});

describe("explicit theme control (spec §3.2)", () => {
  function hostOf(body: FakeElement): FakeElement {
    return body.children[0] as FakeElement;
  }

  function docBody(): FakeElement & { body: FakeElement } {
    return (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
  }

  it("defaults to data-theme=system", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = docBody();
    composer.mount(doc.body as unknown as HTMLElement);
    expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe("system");
    composer.dispose();
  });

  it("pins light and dark themes via the mount option", () => {
    for (const theme of ["light", "dark"] as const) {
      installFakeDom();
      const composer = createMiniComposer({ sessionId: "s-1", theme });
      const doc = docBody();
      composer.mount(doc.body as unknown as HTMLElement);
      expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe(theme);
      composer.dispose();
      removeFakeDom();
    }
  });

  it("setTheme updates the host attribute at runtime", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = docBody();
    composer.mount(doc.body as unknown as HTMLElement);
    composer.setTheme("dark");
    expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe("dark");
    composer.setTheme("light");
    expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe("light");
    composer.setTheme("system");
    expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe("system");
    composer.dispose();
  });

  it("setTheme before mount applies on mount", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    composer.setTheme("dark");
    const doc = docBody();
    composer.mount(doc.body as unknown as HTMLElement);
    expect(hostOf(doc.body)?.getAttribute("data-theme")).toBe("dark");
    composer.dispose();
  });

  it("mirrors the explicit theme onto the bubble so it wins over the OS media query", () => {
    // Regression: theme:'light' rendered dark because data-theme lived only
    // on the host while the bubble declares its own token block.
    for (const theme of ["light", "dark", "system"] as const) {
      installFakeDom();
      const composer = createMiniComposer({ sessionId: "s-1", theme });
      const doc = docBody();
      composer.mount(doc.body as unknown as HTMLElement);
      const host = hostOf(doc.body);
      const bubble = host.findByClass("oc-composer-bubble");
      expect(host?.getAttribute("data-theme")).toBe(theme);
      expect(bubble?.getAttribute("data-theme")).toBe(theme);
      composer.dispose();
      removeFakeDom();
    }
  });

  it("setTheme mirrors runtime changes onto the bubble", () => {
    installFakeDom();
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = docBody();
    composer.mount(doc.body as unknown as HTMLElement);
    const bubbleOf = (): FakeElement | null =>
      hostOf(doc.body)?.findByClass("oc-composer-bubble") ?? null;
    composer.setTheme("light");
    expect(bubbleOf()?.getAttribute("data-theme")).toBe("light");
    composer.setTheme("dark");
    expect(bubbleOf()?.getAttribute("data-theme")).toBe("dark");
    composer.setTheme("system");
    expect(bubbleOf()?.getAttribute("data-theme")).toBe("system");
    composer.dispose();
  });

  it("stylesheet pins bubble tokens per explicit theme after the media query", async () => {
    const { readFileSync } = await import("node:fs");
    const { dirname, join } = await import("node:path");
    const { fileURLToPath } = await import("node:url");
    const here = dirname(fileURLToPath(import.meta.url));
    const css = readFileSync(join(here, "styles.css"), "utf8");
    // Bubble attribute selectors must exist (mirrored by applyTheme/mount)…
    expect(css).toContain('.oc-composer-bubble[data-theme="light"]');
    expect(css).toContain('.oc-composer-bubble[data-theme="dark"]');
    // …and sit after the media query so the explicit choice beats the OS.
    const mediaAt = css.indexOf("@media (prefers-color-scheme: dark)");
    const lightAt = css.indexOf('.oc-composer-bubble[data-theme="light"]');
    const darkAt = css.indexOf('.oc-composer-bubble[data-theme="dark"]');
    expect(mediaAt).toBeGreaterThanOrEqual(0);
    expect(lightAt).toBeGreaterThan(mediaAt);
    expect(darkAt).toBeGreaterThan(mediaAt);
  });
});

describe("bubble sizing (composer-spec §3: viewport − 2rem)", () => {
  it("clamps lower/upper bounds and NaN/inverted ranges", () => {
    expect(clamp(5, 0, 10)).toBe(5);
    expect(clamp(-1, 0, 10)).toBe(0);
    expect(clamp(99, 0, 10)).toBe(10);
    expect(clamp(Number.NaN, 2, 10)).toBe(2);
    expect(clamp(5, 10, 0)).toBe(10);
  });

  it("bubbleMetrics reserves 16px margins on both axes", () => {
    expect(bubbleMetrics(390, 844)).toEqual({ widthPx: 358, maxHeightPx: 560 });
    expect(bubbleMetrics(320, 600)).toEqual({ widthPx: 288, maxHeightPx: 480 });
    expect(bubbleMetrics(1440, 900)).toEqual({ widthPx: 376, maxHeightPx: 560 });
    // Degenerate viewport never goes negative.
    expect(bubbleMetrics(20, 20)).toEqual({ widthPx: 0, maxHeightPx: 0 });
  });

  it("mount at 390px pins inline width/maxWidth inside viewport − 2rem", () => {
    const fake = installFakeDom();
    fake.window.innerWidth = 390;
    fake.window.innerHeight = 844;
    const composer = createMiniComposer({ sessionId: "s-1" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    const host = doc.body.children[0] as FakeElement;
    const bubble = host.findByClass("oc-composer-bubble") as FakeElement;
    expect(bubble?.style["width"]).toBe("358px");
    expect(bubble?.style["maxWidth"]).toBe("358px");
    // Re-open re-applies bounds (fonts/viewport may shift while closed).
    composer.open();
    expect(bubble?.style["width"]).toBe("358px");
    expect(bubble?.style["maxWidth"]).toBe("358px");
    composer.dispose();
  });
});

describe("createMiniComposer send path", () => {
  function mountedWithClient(): {
    composer: ReturnType<typeof createMiniComposer>;
    client: { createTask: ReturnType<typeof vi.fn>; subscribeEvents: ReturnType<typeof vi.fn> };
    editor: FakeElement;
    doc: FakeElement & { body: FakeElement };
  } {
    installFakeDom();
    const createTask = vi.fn(async () => ({}));
    const subscribeEvents = vi.fn(() => vi.fn());
    const client = { createTask, subscribeEvents } as unknown as ComposerClient;
    const composer = createMiniComposer({ client, sessionId: "s-1", agentId: "agent-a" });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    const host = doc.body.children[0] as FakeElement;
    const editor = host.findByClass("oc-composer-editor") as FakeElement;
    return { composer, client: { createTask, subscribeEvents }, editor, doc };
  }

  it("Enter submits with a fresh idempotency key; second send while queued is ignored", async () => {
    const { composer, client, editor, doc } = mountedWithClient();
    void doc;
    editor.value = "hello";
    editor.fire("input");
    editor.fire("keydown", keyEvent("Enter"));
    await vi.waitFor(() => expect(client.createTask).toHaveBeenCalledTimes(1));

    const [input, key] = client.createTask.mock.calls[0] as unknown as [
      { clientTaskId: string; sessionId: string; agentId: string; input: string },
      string,
    ];
    expect(input).toMatchObject({ sessionId: "s-1", agentId: "agent-a", input: "hello" });
    expect(typeof key).toBe("string");
    expect(key.length).toBeGreaterThan(0);
    expect(input.clientTaskId).toBe(key);

    // While queued, Enter is ignored — no duplicate task.
    editor.value = "again";
    editor.fire("input");
    editor.fire("keydown", keyEvent("Enter"));
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(client.createTask).toHaveBeenCalledTimes(1);
    composer.dispose();
  });

  it("Shift+Enter, IME Enter, and held Enter never submit", async () => {
    const { composer, client, editor } = mountedWithClient();
    editor.value = "hello";
    editor.fire("input");
    editor.fire("keydown", keyEvent("Enter", { shiftKey: true }));
    editor.fire("keydown", keyEvent("Enter", { isComposing: true }));
    editor.fire("keydown", keyEvent("Enter", { repeat: true }));
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(client.createTask).not.toHaveBeenCalled();
    composer.dispose();
  });

  it("reports a recoverable error when no session is attached", async () => {
    installFakeDom();
    const createTask = vi.fn(async () => ({}));
    const composer = createMiniComposer({ client: { createTask } as ComposerClient });
    const doc = (globalThis as Record<string, unknown>)["document"] as FakeElement & {
      body: FakeElement;
    };
    composer.mount(doc.body as unknown as HTMLElement);
    await composer.send("hello");
    expect(createTask).not.toHaveBeenCalled();
    expect(composer.getState().lastError).toMatch(/No session/);
    composer.dispose();
  });

  it("applies live session events to status and streaming text", async () => {
    const { composer, client } = mountedWithClient();
    const handlers = client.subscribeEvents.mock.calls[0]?.[1] as unknown as {
      onEvent: (event: ComposerSessionEvent) => void;
    };
    expect(handlers).toBeDefined();
    handlers.onEvent({
      schemaVersion: "openconnect.v1",
      eventId: "e-1",
      eventType: "task.running",
      sessionId: "s-1",
      sequence: 1,
      occurredAt: "2026-01-01T00:00:00.000Z",
      payload: { data: "Hel" },
    });
    handlers.onEvent({
      schemaVersion: "openconnect.v1",
      eventId: "e-2",
      eventType: "message.delta",
      sessionId: "s-1",
      sequence: 2,
      occurredAt: "2026-01-01T00:00:00.000Z",
      payload: { data: "lo" },
    });
    expect(composer.getState().streamingText).toBe("Hello");
    expect(composer.getState().taskPhase).toBe("running");
    handlers.onEvent({
      schemaVersion: "openconnect.v1",
      eventId: "e-3",
      eventType: "task.completed",
      sessionId: "s-1",
      sequence: 3,
      occurredAt: "2026-01-01T00:00:00.000Z",
      payload: {},
    });
    expect(composer.getState().taskPhase).toBe("completed");
    expect(
      composer.getState().messages[composer.getState().messages.length - 1]?.text,
    ).toBe("Hello");
    composer.dispose();
  });

  it("rejected createTask retains input so Retry resubmits the same text", async () => {
    const { composer, client, editor, doc } = mountedWithClient();
    const host = doc.body.children[0] as FakeElement;
    const retry = host.findByClass("oc-composer-retry") as FakeElement;
    expect(retry).not.toBeNull();

    // First submit fails: begin-send clears the draft/editor, failure retains a copy.
    client.createTask.mockRejectedValueOnce(new Error("network failed"));
    await composer.send("hello retry");
    expect(client.createTask).toHaveBeenCalledTimes(1);
    expect(composer.getState().draft).toBe("");
    expect(editor.value).toBe("");
    expect(composer.getState().lastError).toBeDefined();

    // Retry with an empty draft must resubmit the retained input.
    retry.fire("click");
    await vi.waitFor(() => expect(client.createTask).toHaveBeenCalledTimes(2));
    const second = client.createTask.mock.calls[1] as unknown as [
      { clientTaskId: string; sessionId: string; agentId: string; input: string },
      string,
    ];
    expect(second[0]).toMatchObject({ sessionId: "s-1", agentId: "agent-a", input: "hello retry" });
    composer.dispose();
  });

  it("explicit edit after failure supersedes the retained input", async () => {
    const { composer, client, editor, doc } = mountedWithClient();
    const host = doc.body.children[0] as FakeElement;
    const retry = host.findByClass("oc-composer-retry") as FakeElement;

    client.createTask.mockRejectedValueOnce(new Error("network failed"));
    await composer.send("first attempt");
    expect(client.createTask).toHaveBeenCalledTimes(1);

    // User types a new draft: retained copy is cleared, retry sends the new draft.
    editor.value = "second attempt";
    editor.fire("input");
    retry.fire("click");
    await vi.waitFor(() => expect(client.createTask).toHaveBeenCalledTimes(2));
    const second = client.createTask.mock.calls[1] as unknown as [
      { clientTaskId: string; sessionId: string; agentId: string; input: string },
      string,
    ];
    expect(second[0]).toMatchObject({ input: "second attempt" });
    composer.dispose();
  });
});
