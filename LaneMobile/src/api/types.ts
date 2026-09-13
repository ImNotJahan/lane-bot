/**
 * Mirrors Lane.Surfaces.Api/Contracts.cs.
 *
 * ApiJson uses JsonSerializerDefaults.Web, so the wire is camelCase and nulls are omitted
 * rather than sent — every optional field here is optional on the wire too.
 */

export type LaneRole = 'System' | 'User' | 'Assistant' | 'Tool';

export type MessageKind = 'Utterance' | 'Thought' | 'Observation' | 'Summary';

export interface SessionResponse {
  id: string;
  key: string;
  displayName: string;
  surface: string;
  kind: string;
  memoryGroup: string;
  state: string;
  mine: boolean;
  lastActivity: string;
  participants: string[];
  capabilities: string[];
}

export interface MessageResponse {
  id: string;
  sequence: number;
  role: LaneRole;
  kind: MessageKind;
  author: string;
  text: string;
  timestamp: string;
  externalId?: string;
}

export interface HistoryResponse {
  sessionId: string;
  messages: MessageResponse[];
}

export interface AcceptedResponse {
  sessionId: string;
  messageId: string;
}

export interface ErrorResponse {
  error: string;
  detail?: string;
}

// ---- stream frames --------------------------------------------------------

export interface DeltaFrame {
  text: string;
}

export interface ToolFrame {
  name: string;
  phase: 'start' | 'end';
  isError?: boolean;
}

export interface MessageFrame {
  sessionId: string;
  text: string;
  replyTo?: string;
}

export interface DoneFrame {
  silent: boolean;
  reason?: string;
}

export interface ErrorFrame {
  error: string;
}

/** One decoded frame from `POST /v1/sessions/{key}/messages?stream=true`. */
export type TurnEvent =
  | { type: 'accepted'; data: AcceptedResponse }
  | { type: 'delta'; data: DeltaFrame }
  | { type: 'tool'; data: ToolFrame }
  | { type: 'message'; data: MessageFrame }
  | { type: 'done'; data: DoneFrame }
  | { type: 'error'; data: ErrorFrame };
