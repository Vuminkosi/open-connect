import type {
  MessagingConnectionState,
  MessagingEventEnvelope,
  MessagingEventReplayResponse,
} from "./types.js";

export interface MessagingWebSocketLike {
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onerror: ((event: unknown) => void) | null;
  onclose: ((event: { code?: number; reason?: string }) => void) | null;
  close(code?: number, reason?: string): void;
}

export interface MessagingRealtimeOptions {
  readonly webSocketUrl: string;
  readonly profileId: string;
  readonly profileEpoch: number;
  readonly isCurrentEpoch: (epoch: number, profileId: string) => boolean;
  readonly createWebSocket: (url: string) => MessagingWebSocketLike;
  readonly replaySinceCursor?: (
    cursor: string | undefined
  ) => Promise<MessagingEventReplayResponse>;
  readonly initialCursor?: string;
  readonly baseReconnectDelayMs?: number;
  readonly maxReconnectDelayMs?: number;
  readonly onEvent?: (event: MessagingEventEnvelope) => void;
  readonly onStateChange?: (state: MessagingConnectionState) => void;
}

/**
 * Profile-epoch-fenced realtime WebSocket client with cursor replay,
 * event-ID deduplication, and bounded exponential backoff with jitter.
 * Late callbacks from a superseded profile or epoch are dropped silently.
 */
export class MessagingRealtimeClient {
  private readonly options: MessagingRealtimeOptions;
  private readonly seenEventIds = new Set<string>();
  private socket: MessagingWebSocketLike | null = null;
  private cursor: string | undefined;
  private state: MessagingConnectionState = "disconnected";
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;

  constructor(options: MessagingRealtimeOptions) {
    this.options = options;
    this.cursor = options.initialCursor;
  }

  get currentCursor(): string | undefined {
    return this.cursor;
  }

  get connectionState(): MessagingConnectionState {
    return this.state;
  }

  async connect(): Promise<void> {
    if (this.disposed || !this.isFencedActive()) {
      return;
    }

    this.setState("connecting");
    const ws = this.options.createWebSocket(this.options.webSocketUrl);
    this.socket = ws;

    ws.onopen = () => {
      if (!this.isFencedActive() || this.disposed) {
        ws.close(1000, "stale_profile_epoch");
        return;
      }
      this.reconnectAttempts = 0;
      this.setState("connected");
      void this.catchUpFromCursor();
    };

    ws.onmessage = (msg) => {
      if (!this.isFencedActive() || this.disposed) {
        return;
      }
      if (typeof msg.data !== "string") {
        return;
      }
      try {
        const envelope = JSON.parse(msg.data) as MessagingEventEnvelope;
        this.handleEnvelope(envelope);
      } catch {
        // Malformed frames are ignored without breaking the stream.
      }
    };

    ws.onerror = () => {
      if (!this.isFencedActive() || this.disposed) {
        return;
      }
      this.setState("error");
    };

    ws.onclose = (ev) => {
      if (this.disposed || !this.isFencedActive()) {
        return;
      }
      if (ev.code === 4401 || ev.code === 4403) {
        this.setState("revoked");
        return;
      }
      this.scheduleReconnect();
    };
  }

  async catchUpFromCursor(): Promise<number> {
    if (!this.options.replaySinceCursor || this.disposed || !this.isFencedActive()) {
      return 0;
    }

    let delivered = 0;
    let hasMore = true;
    let guard = 0;
    while (hasMore && guard < 20 && !this.disposed && this.isFencedActive()) {
      guard++;
      const page = await this.options.replaySinceCursor(this.cursor);
      if (!this.isFencedActive() || this.disposed) {
        return delivered;
      }
      for (const envelope of page.events) {
        if (this.handleEnvelope(envelope)) {
          delivered++;
        }
      }
      if (page.nextCursor) {
        this.cursor = page.nextCursor;
      }
      hasMore = page.hasMore;
    }

    return delivered;
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.socket) {
      try {
        this.socket.close(1000, "client_disposed");
      } catch {
        // Ignore close failures during teardown.
      }
      this.socket = null;
    }
    this.state = "disconnected";
  }

  private isFencedActive(): boolean {
    return this.options.isCurrentEpoch(
      this.options.profileEpoch,
      this.options.profileId
    );
  }

  private handleEnvelope(envelope: MessagingEventEnvelope): boolean {
    if (!this.isFencedActive() || this.disposed) {
      return false;
    }
    if (!envelope || typeof envelope.eventId !== "string" || envelope.eventId.length === 0) {
      return false;
    }
    if (this.seenEventIds.has(envelope.eventId)) {
      return false;
    }
    this.seenEventIds.add(envelope.eventId);
    if (typeof envelope.cursor === "string" && envelope.cursor.length > 0) {
      this.cursor = envelope.cursor;
    }
    if (envelope.type === "messaging.device.revoked") {
      this.setState("revoked");
    }
    this.options.onEvent?.(envelope);
    return true;
  }

  private scheduleReconnect(): void {
    this.setState("reconnecting");
    const base = this.options.baseReconnectDelayMs ?? 500;
    const max = this.options.maxReconnectDelayMs ?? 30_000;
    const exp = Math.min(max, base * 2 ** Math.min(this.reconnectAttempts, 8));
    const jitter = Math.floor(Math.random() * Math.max(50, Math.floor(exp * 0.25)));
    this.reconnectAttempts++;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.connect();
    }, exp + jitter);
  }

  private setState(next: MessagingConnectionState): void {
    if (this.state === next) {
      return;
    }
    this.state = next;
    this.options.onStateChange?.(next);
  }
}
