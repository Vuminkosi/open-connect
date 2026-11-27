/**
 * Framework-neutral IMessaging state and Linked Devices integration for `@openconnect/composer`.
 * SSR-safe: does not access DOM globals.
 */

export type ComposerMessagingConnectionState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "offline"
  | "revoked"
  | "error";

export interface ComposerMessagingOptions {
  readonly enableMessaging?: boolean;
  readonly enableLinkedDevices?: boolean;
  readonly showUnreadBadge?: boolean;
}

export interface ComposerMessagingState {
  readonly profileId: string | null;
  readonly profileEpoch: number;
  readonly connectionState: ComposerMessagingConnectionState;
  readonly unreadCount: number;
  readonly linkedDevicesOpen: boolean;
  readonly messagingDrawerOpen: boolean;
  readonly pendingCallId: string | null;
}

export type ComposerMessagingAction =
  | { type: "profile/switched"; profileId: string | null; profileEpoch: number }
  | { type: "connection/changed"; profileEpoch: number; state: ComposerMessagingConnectionState }
  | { type: "unread/updated"; profileEpoch: number; count: number }
  | { type: "linked-devices/toggle"; open?: boolean }
  | { type: "messaging/toggle"; open?: boolean }
  | { type: "call/incoming"; profileEpoch: number; callId: string }
  | { type: "call/cleared"; profileEpoch: number };

export function initialComposerMessagingState(): ComposerMessagingState {
  return {
    profileId: null,
    profileEpoch: 0,
    connectionState: "disconnected",
    unreadCount: 0,
    linkedDevicesOpen: false,
    messagingDrawerOpen: false,
    pendingCallId: null,
  };
}

/**
 * Pure reducer for composer messaging state.
 * Enforces profile-epoch fencing so late callbacks from a previous profile cannot mutate state.
 */
export function composerMessagingReducer(
  state: ComposerMessagingState,
  action: ComposerMessagingAction
): ComposerMessagingState {
  switch (action.type) {
    case "profile/switched":
      return {
        ...state,
        profileId: action.profileId,
        profileEpoch: action.profileEpoch,
        connectionState: action.profileId ? "connecting" : "disconnected",
        unreadCount: 0,
        pendingCallId: null,
      };
    case "connection/changed":
      if (action.profileEpoch !== state.profileEpoch) {
        return state;
      }
      return {
        ...state,
        connectionState: action.state,
        pendingCallId: action.state === "revoked" ? null : state.pendingCallId,
      };
    case "unread/updated":
      if (action.profileEpoch !== state.profileEpoch) {
        return state;
      }
      return {
        ...state,
        unreadCount: Math.max(0, action.count),
      };
    case "linked-devices/toggle":
      return {
        ...state,
        linkedDevicesOpen: action.open ?? !state.linkedDevicesOpen,
      };
    case "messaging/toggle":
      return {
        ...state,
        messagingDrawerOpen: action.open ?? !state.messagingDrawerOpen,
      };
    case "call/incoming":
      if (action.profileEpoch !== state.profileEpoch) {
        return state;
      }
      return {
        ...state,
        pendingCallId: action.callId,
      };
    case "call/cleared":
      if (action.profileEpoch !== state.profileEpoch) {
        return state;
      }
      return {
        ...state,
        pendingCallId: null,
      };
  }
}
