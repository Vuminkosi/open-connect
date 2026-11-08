import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { hasDom, shouldCloseOnKey, showHighlight, tourProgressText } from "./highlighter.js";

describe("highlighter", () => {
  it("reports no DOM in the Node test environment", () => {
    expect(hasDom()).toBe(false);
    expect(
      showHighlight(
        { title: "T", body: "B", stepIndex: 0, stepTotal: 1 },
        { onNext: () => {}, onBack: () => {}, onSkip: () => {}, onClose: () => {} },
      ),
    ).toBeNull();
  });

  it("formats progress text and Escape handling", () => {
    expect(tourProgressText(0, 1)).toBe("Step 1 of 1");
    expect(tourProgressText(1, 5)).toBe("Step 2 of 5");
    expect(shouldCloseOnKey("Escape")).toBe(true);
    expect(shouldCloseOnKey("Enter")).toBe(false);
  });

  it("focuses an element inside the dialog card and traps Tab (NAV-UX-02)", () => {
    const harness = installFakeDom();
    try {
      expect(hasDom()).toBe(true);
      let closed = 0;
      const handle = showHighlight(
        { title: "Save your work", body: "Press Save.", stepIndex: 0, stepTotal: 2 },
        {
          onNext: () => {},
          onBack: () => {},
          onSkip: () => {},
          onClose: () => {
            closed += 1;
          },
        },
      );
      expect(handle).not.toBeNull();

      const dialog = harness.created.find((el) => el.getAttribute("role") === "dialog");
      expect(dialog).toBeDefined();
      // The dialog is the card — the full-viewport layer stays role-less.
      expect(dialog?.getAttribute("class")).toContain("ocnav-card");
      const layer = harness.created.find((el) => el.getAttribute("class") === "ocnav-layer");
      expect(layer?.getAttribute("role")).toBeNull();

      const focused = harness.doc.activeElement;
      expect(focused).not.toBeNull();
      expect(focused?.tagName).toBe("H2");
      expect(focused !== null && dialog !== undefined && focused.isDescendantOf(dialog)).toBe(true);

      // Escape dismisses via onClose.
      harness.emitKey({ key: "Escape" });
      expect(closed).toBe(1);

      // Tab on the last enabled button wraps to the first enabled button.
      const buttons = harness.created.filter((el) => el.tagName === "BUTTON");
      const enabled = buttons.filter((button) => !button.disabled);
      expect(enabled.length).toBeGreaterThanOrEqual(2);
      const first = enabled[0];
      const last = enabled[enabled.length - 1];
      expect(first).toBeDefined();
      expect(last).toBeDefined();
      if (first !== undefined && last !== undefined) {
        harness.doc.activeElement = last;
        const prevented: boolean[] = [];
        harness.emitKey({
          key: "Tab",
          shiftKey: false,
          markPrevented: () => prevented.push(true),
        });
        expect(prevented).toEqual([true]);
        expect(harness.doc.activeElement).toBe(first);
      }

      handle?.close();
    } finally {
      harness.cleanup();
    }
    expect(hasDom()).toBe(false);
  });

  it("bounds the overlay card with internal scroll so actions stay reachable (NAV-UX-03)", () => {
    const css = readFileSync(new URL("./styles.css", import.meta.url), "utf8");
    const cardBlock = /\.ocnav-card\s*\{[^}]*\}/s.exec(css)?.[0] ?? "";
    expect(cardBlock).toContain("display: flex");
    expect(cardBlock).toContain("max-height: calc(100vh - 32px)");
    expect(cardBlock).toContain("max-height: calc(100dvh - 32px)");
    expect(cardBlock).toContain("overflow: hidden");
    const bodyBlock = /\.ocnav-body\s*\{[^}]*\}/s.exec(css)?.[0] ?? "";
    expect(bodyBlock).toContain("overflow-y: auto");
    const actionsBlock = /\.ocnav-actions\s*\{[^}]*\}/s.exec(css)?.[0] ?? "";
    expect(actionsBlock).toContain("flex-shrink: 0");
  });
});

/** Minimal DOM stub sufficient for showHighlight (no target lookup). */
function installFakeDom() {
  class FakeHTMLElement {}
  class FakeElement extends FakeHTMLElement {
    readonly tagName: string;
    readonly attributes = new Map<string, string>();
    readonly children: FakeElement[] = [];
    parent: FakeElement | null = null;
    readonly style: Record<string, string> = {};
    textContent = "";
    disabled = false;
    constructor(
      tag: string,
      private readonly docRef: { activeElement: FakeElement | null },
    ) {
      super();
      this.tagName = tag.toUpperCase();
    }
    setAttribute(name: string, value: string): void {
      this.attributes.set(name, value);
    }
    getAttribute(name: string): string | null {
      return this.attributes.get(name) ?? null;
    }
    appendChild(child: FakeElement): FakeElement {
      child.parent = this;
      this.children.push(child);
      return child;
    }
    append(...nodes: FakeElement[]): void {
      for (const node of nodes) this.appendChild(node);
    }
    addEventListener(): void {
      // Buttons need no listener tracking for these tests.
    }
    remove(): void {
      if (this.parent !== null) {
        const index = this.parent.children.indexOf(this);
        if (index >= 0) this.parent.children.splice(index, 1);
        this.parent = null;
      }
    }
    focus(): void {
      this.docRef.activeElement = this;
    }
    getBoundingClientRect(): { left: number; top: number; width: number; height: number } {
      return { left: 0, top: 0, width: 10, height: 10 };
    }
    scrollIntoView(): void {}
    isDescendantOf(ancestor: FakeElement): boolean {
      let current = this.parent;
      while (current !== null) {
        if (current === ancestor) return true;
        current = current.parent;
      }
      return false;
    }
  }
  interface FakeDoc {
    activeElement: FakeElement | null;
    createElement: (tag: string) => FakeElement;
    body: FakeElement;
    documentElement: FakeElement;
    addEventListener: (type: string, handler: (event: KeyboardEvent) => void) => void;
    removeEventListener: (type: string, handler: (event: KeyboardEvent) => void) => void;
    contains: (node: FakeElement | null) => boolean;
    querySelector: (selector: string) => FakeElement | null;
    getElementById: (id: string) => FakeElement | null;
  }

  const state: { activeElement: FakeElement | null } = { activeElement: null };
  const created: FakeElement[] = [];
  let keydownHandler: ((event: KeyboardEvent) => void) | null = null;

  const body = new FakeElement("body", state);
  const documentElement = new FakeElement("html", state);
  const fakeDoc: FakeDoc = {
    get activeElement(): FakeElement | null {
      return state.activeElement;
    },
    set activeElement(value: FakeElement | null) {
      state.activeElement = value;
    },
    createElement: (tag: string) => {
      const el = new FakeElement(tag, state);
      created.push(el);
      return el;
    },
    body,
    documentElement,
    addEventListener: (type: string, handler: (event: KeyboardEvent) => void) => {
      if (type === "keydown") keydownHandler = handler;
    },
    removeEventListener: (type: string) => {
      if (type === "keydown") keydownHandler = null;
    },
    contains: () => false,
    querySelector: () => null,
    getElementById: () => null,
  };

  const fakeWindow = {
    matchMedia: () => ({ matches: false }),
    addEventListener: () => {},
    removeEventListener: () => {},
    scrollX: 0,
    scrollY: 0,
  };

  const globals = globalThis as unknown as Record<string, unknown>;
  globals["HTMLElement"] = FakeHTMLElement;
  globals["document"] = fakeDoc;
  globals["window"] = fakeWindow;

  return {
    doc: fakeDoc,
    created,
    emitKey: (key: { key: string; shiftKey?: boolean; markPrevented?: () => void }) => {
      const handler = keydownHandler;
      if (handler === null) throw new Error("keydown handler was not registered");
      handler({
        key: key.key,
        shiftKey: key.shiftKey ?? false,
        preventDefault: () => key.markPrevented?.(),
      } as unknown as KeyboardEvent);
    },
    cleanup: () => {
      delete globals["document"];
      delete globals["window"];
      delete globals["HTMLElement"];
    },
  };
}
