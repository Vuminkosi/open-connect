import { describe, expect, it } from "vitest";
import { TargetError, TargetRegistry } from "./targets.js";

describe("TargetRegistry", () => {
  it("registers a target with revision 1", () => {
    const registry = new TargetRegistry();
    const target = registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    expect(target.revision).toBe(1);
    expect(registry.size).toBe(1);
  });

  it("bumps revision on remount of the same targetId", () => {
    const registry = new TargetRegistry();
    registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    const again = registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    expect(again.revision).toBe(2);
    expect(registry.size).toBe(1);
  });

  it("scopes listings by routeId", () => {
    const registry = new TargetRegistry();
    registry.register({ targetId: "b", routeId: "/a", label: "B" });
    registry.register({ targetId: "a", routeId: "/a", label: "A" });
    registry.register({ targetId: "c", routeId: "/b", label: "C" });
    expect(registry.listForRoute("/a").map((t) => t.targetId)).toEqual(["a", "b"]);
    expect(registry.listForRoute("/b").map((t) => t.targetId)).toEqual(["c"]);
    expect(registry.listForRoute("/missing")).toEqual([]);
  });

  it("unregisters targets and reports presence", () => {
    const registry = new TargetRegistry();
    registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    expect(registry.unregister("save")).toBe(true);
    expect(registry.unregister("save")).toBe(false);
    expect(registry.get("save")).toBeUndefined();
  });

  it("rejects empty ids and overlong labels", () => {
    const registry = new TargetRegistry();
    expect(() => registry.register({ targetId: "  ", routeId: "/a", label: "X" })).toThrow(TargetError);
    expect(() => registry.register({ targetId: "x", routeId: "", label: "X" })).toThrow(TargetError);
    expect(() =>
      registry.register({ targetId: "x", routeId: "/a", label: "L".repeat(121) }),
    ).toThrow(TargetError);
  });
});
