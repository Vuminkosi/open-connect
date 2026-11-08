import { describe, expect, it } from "vitest";
import { announceFor, currentStep, type NavigatorState } from "./types.js";

const steps = [
  { id: "s-1", target: "#save", title: "Save your work", body: "Press Save to keep changes." },
  { id: "s-2", target: "", title: "All done", body: "You finished the tour." },
];

function stateOf(status: NavigatorState["status"], index: number): NavigatorState {
  return { status, index, steps };
}

describe("currentStep", () => {
  it("returns the active step and null when the tour is not on a step", () => {
    expect(currentStep(stateOf("active", 1))?.id).toBe("s-2");
    expect(currentStep(stateOf("paused", 0))?.id).toBe("s-1");
    expect(currentStep(stateOf("done", 1))).toBeNull();
    expect(currentStep(stateOf("skipped", 0))).toBeNull();
    expect(currentStep(stateOf("idle", 0))).toBeNull();
  });

  it("returns null for an out-of-range index instead of throwing", () => {
    expect(currentStep(stateOf("active", 9))).toBeNull();
  });
});

describe("announceFor", () => {
  it("announces position, title, and body as plain text", () => {
    expect(announceFor(stateOf("active", 0))).toBe(
      "Step 1 of 2. Save your work. Press Save to keep changes.",
    );
  });

  it("marks paused tours and terminal states", () => {
    expect(announceFor(stateOf("paused", 0)).startsWith("Paused. Step 1 of 2.")).toBe(true);
    expect(announceFor(stateOf("done", 1))).toBe("Tour complete.");
    expect(announceFor(stateOf("skipped", 0))).toBe("Tour skipped.");
    expect(announceFor(stateOf("idle", 0))).toBe("Tour closed.");
  });
});
