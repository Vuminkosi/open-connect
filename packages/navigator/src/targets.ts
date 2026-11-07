/**
 * Stable target registry for the Navigator bridge.
 *
 * Targets are framework-neutral descriptors: a stable `targetId`, the
 * `routeId` whose screen owns the target, and a short human label plus an
 * optional plain-language description. Re-registering an existing `targetId`
 * (for example on component remount) bumps `revision` instead of creating a
 * duplicate entry.
 *
 * This module is DOM-free and SSR-safe: importing it never touches
 * `window`, `document`, or `navigator`.
 */

/** Human-facing descriptor for one tour-able UI target. */
export interface NavigatorTarget {
  /** Stable identifier referenced by tour steps and commands. */
  targetId: string;
  /** Route/screen that owns this target; used for route scoping. */
  routeId: string;
  /** Short human label shown in text tours and overlay captions. */
  label: string;
  /** Optional plain-language description (human language only). */
  description?: string;
  /** Incremented on every re-registration of the same `targetId`. */
  revision: number;
}

/** Input accepted by {@link TargetRegistry.register}. */
export interface RegisterTargetInput {
  targetId: string;
  routeId: string;
  label: string;
  description?: string;
}

/** Bounds for registry validation (spec §4.5). */
export const TARGET_ID_MAX_LENGTH = 128;
export const ROUTE_ID_MAX_LENGTH = 256;
export const TARGET_LABEL_MAX_LENGTH = 120;
export const TARGET_DESCRIPTION_MAX_LENGTH = 500;

/** Validation failure raised by {@link TargetRegistry.register}. */
export class TargetError extends Error {
  readonly code: "invalid_request";
  constructor(message: string) {
    super(message);
    this.name = "TargetError";
    this.code = "invalid_request";
  }
}

function checkField(value: string, field: string, max: number): void {
  if (value.trim().length === 0) {
    throw new TargetError(`${field} must not be empty.`);
  }
  if (value.length > max) {
    throw new TargetError(`${field} must be at most ${String(max)} characters.`);
  }
}

/**
 * In-memory registry of tour-able targets. Framework adapters call
 * `register` on mount and `unregister` on unmount; the Navigator bridge
 * resolves tour steps and commands against this registry.
 */
export class TargetRegistry {
  private readonly targets = new Map<string, NavigatorTarget>();

  /**
   * Register (or re-register) a target. Re-registering the same `targetId`
   * keeps a single entry and increments `revision` so overlays can detect
   * remounts. Updating to a different `routeId` is allowed and also bumps
   * `revision`.
   */
  register(input: RegisterTargetInput): NavigatorTarget {
    checkField(input.targetId, "targetId", TARGET_ID_MAX_LENGTH);
    checkField(input.routeId, "routeId", ROUTE_ID_MAX_LENGTH);
    checkField(input.label, "label", TARGET_LABEL_MAX_LENGTH);
    if (input.description !== undefined && input.description.length > TARGET_DESCRIPTION_MAX_LENGTH) {
      throw new TargetError(
        `description must be at most ${String(TARGET_DESCRIPTION_MAX_LENGTH)} characters.`,
      );
    }
    const existing = this.targets.get(input.targetId);
    const revision = existing === undefined ? 1 : existing.revision + 1;
    const target: NavigatorTarget = {
      targetId: input.targetId,
      routeId: input.routeId,
      label: input.label,
      revision,
      ...(input.description === undefined ? {} : { description: input.description }),
    };
    this.targets.set(input.targetId, target);
    return target;
  }

  /** Remove a target. Returns `true` when an entry was present. */
  unregister(targetId: string): boolean {
    return this.targets.delete(targetId);
  }

  /** Look up a single target by stable id. */
  get(targetId: string): NavigatorTarget | undefined {
    return this.targets.get(targetId);
  }

  /** List targets owned by one route, sorted by `targetId`. */
  listForRoute(routeId: string): NavigatorTarget[] {
    return [...this.targets.values()]
      .filter((target) => target.routeId === routeId)
      .sort((a, b) => (a.targetId < b.targetId ? -1 : a.targetId > b.targetId ? 1 : 0));
  }

  /** Remove every registered target. */
  clear(): void {
    this.targets.clear();
  }

  /** Number of registered targets. */
  get size(): number {
    return this.targets.size;
  }
}
