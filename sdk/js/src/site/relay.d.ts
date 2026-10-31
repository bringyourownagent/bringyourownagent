// Type declarations for bringyourownagent/relay: the site's side of the direct link (spec/CONTRACT-DIRECT.md).

import type { EventEmitter } from 'node:events';
import type { Server } from 'node:http';
import type { Server as HttpsServer } from 'node:https';

/**
 * The answer to one call. Never an exception: `{ ok: false }` means "unavailable for this call"; use your own path.
 * `output` is what the visitor's software sent: it is user input, never trusted; validate it and cap it before use.
 * `receipt` is reduced to two short strings or null.
 * `lacks` (the visitor's agent only): abilities the site hoped for that the agent does not have, so the answer is to be read as partial.
 * `reason` (wallet 0.36): why a call failed, a closed list; absent when the wallet said nothing (an older wallet), "timeout" when the site's own wait ran out.
 */
export type CallResult =
  | { ok: true; output: Record<string, unknown>; receipt: { cost: string; provider: string; model?: string } | null; lacks?: string[] }
  | { ok: false; reason?: 'timeout' | 'cancelled' | 'limit' | 'failed'; timedOut?: boolean; cancelled?: boolean; tooBig?: boolean };

/** The visitor's address as the limits count it: IPv4-mapped IPv6 becomes IPv4, any other IPv6 becomes its /64. */
export function addressKey(ip: string): string;

/** One participant of a grant, as the relay passes it on (CONTRACT.md section 3.3). */
export interface GrantedModel {
  ref: string;
  name: string;
  id: string | null;
  runs: string | null;
  kind: 'model' | 'agent';
  /** The wallet's verdicts for the conditions the site asked about in `needs`; absent when it asked none or the wallet said none. */
  can?: Partial<Record<'input' | 'output' | 'context' | 'runs', boolean>>;
  /** For an agent entry: verdicts for the abilities the site asked about. Absent otherwise. */
  abilities?: Record<string, boolean>;
}

/** What the wallet said it was granted (CONTRACT.md sections 3.3 and 3.4). */
export interface Grant {
  skills: string[];
  expiresAt: string | null;
  /** The limits it holds the site to; null from an older wallet. */
  limits: Partial<Record<'maxTextChars' | 'maxAnswerTokens' | 'maxCallSeconds' | 'maxParallelCalls' | 'maxCallsPerMinute', number>> | null;
  /** The feature names of the closed list the wallet supports (wallet 0.36); empty when it sent none (CONTRACT.md section 3.3). */
  capabilities: string[];
  /** The `text.generate` participants, as before (the calls of this relay are text-only). */
  models: GrantedModel[];
  /** Every skill's participants, one list per skill, as the wallet sent them. */
  modelsBySkill: Record<string, GrantedModel[]>;
}

/** One wallet connected to the server. */
export class WalletConnection extends EventEmitter {
  /** The pairing token this wallet was welcomed with. */
  readonly pairing: string | null;
  /** What the wallet said it was granted: skills, when the grant ends, the capabilities it understands, the limits it holds the site to and the models the visitor gave the site. */
  readonly grant: Grant | null;
  /** How many calls may be in flight now: the wallet's `maxParallelCalls` (4 when it says none), at most the relay's `maxInFlight` option. */
  readonly maxInFlight: number;
  /** The wallet's own name and version, as it announced them. Informational only. */
  readonly wallet: Record<string, unknown> | null;
  /** True while the wallet can take calls. */
  readonly live: boolean;
  /** True when a newer connection with the same pairing took this one's place. */
  readonly replaced: boolean;
  /** How many calls in a row have failed (a timeout during the warm-up does not count). */
  readonly failures: number;
  /** How many calls wait for a free place. */
  readonly queued: number;
  /**
   * One use of a skill. Never rejects. As many calls are in flight to one wallet as its `maxParallelCalls` says (4 when
   * it says nothing), up to `maxQueued` more wait their turn, and any call beyond that fails at once. The wait (`timeoutMs`, default 12 s) counts from the moment of the
   * call, including the time spent waiting for a place. The first call after a connect gets a longer wait (a cold
   * model may need time). A call whose frame would exceed 4 MB fails with `tooBig`.
   */
  call(skill: string, input: unknown, params?: Record<string, unknown>, options?: { timeoutMs?: number; onChunk?: (text: string) => void }): Promise<CallResult>;
  /** Says "bye" and closes. */
  bye(): void;
  close(code?: number, reason?: string): void;
  on(event: 'close', listener: () => void): this;
  /** The visitor changed this site's models or limits in the wallet: `grant` is up to date. */
  on(event: 'grant', listener: (connection: WalletConnection) => void): this;
  /** This many calls in a row failed (`failureLimit`): the site should give up on this wallet and use its own path. */
  on(event: 'failing', listener: (failures: number) => void): this;
}

export interface RelayOptions {
  /** The manifest this site asks with. It is validated now and travels, as it is, in every link (at most 4096 bytes). */
  manifest: object;
  /** The wallet's WebSocket URL, `wss://your-host/byoa/ws` (no user name, no fragment). */
  endpoint: string;
  /** Your page shows the connect check code: the links' manifest gets `connect.check` and `newCode` answers with `check`. Default false (on when the manifest says so). */
  connectCheck?: boolean;
  /** Codes one address may ask for in a minute (see `addressKey`). Default 10. */
  codesPerMinute?: number;
  /** Codes one page (`pageKey`) may ask for in a minute. Default 6. */
  codesPerPageMinute?: number;
  /** Default 12000. */
  callTimeoutMs?: number;
  /** For the first call after a connect. Default 30000. */
  firstCallTimeoutMs?: number;
  /** From the connection to its hello. Default 10000. */
  helloMs?: number;
  /** Silence after welcome before the wallet is dropped (it pings every 30 s). Default 75000. */
  idleMs?: number;
  /** Default 5000. */
  sendTimeoutMs?: number;
  /** A connection older than this is let go with "bye"; the wallet comes back on its own with its pairing. Default 12 hours. */
  maxLifetimeMs?: number;
  /** Open WebSockets, welcomed or not; more are refused before the handshake. Default 500. */
  maxSockets?: number;
  /** When full, the oldest pairing without a wallet goes first, then the live pairing used least recently. Default 1000. */
  maxPairings?: number;
  /** Default 2000. */
  maxCodes?: number;
  /** Calls waiting for a free place, per wallet. Default 32. */
  maxQueued?: number;
  /** At most this many calls in flight to one wallet, or fewer if the wallet's own `maxParallelCalls` is lower. Default: the wallet's. */
  maxInFlight?: number;
  /** The biggest `output` taken from a wallet, as JSON. Default 65536 (a text answer may be 128 000 characters: raise it for text.generate). */
  maxOutputBytes?: number;
  /** Calls in a row that fail before a connection emits `failing`. Default 3. */
  failureLimit?: number;
  /** A clock in milliseconds, for tests. */
  now?: () => number;
}

export class Relay extends EventEmitter {
  constructor(options: RelayOptions);
  readonly manifest: object;
  /** The manifest as the link carries it: base64url, no padding. Keep this exact string: `welcome` must echo it byte for byte. */
  readonly manifestB64: string;
  readonly endpoint: string;
  readonly pairingCount: number;
  /** Open WebSockets, welcomed or not. */
  readonly socketCount: number;

  /**
   * A new one-time code for one page (192 random bits, valid 120 s, single use) and the link that carries it.
   * `pageKey` is any string that names the page or game session on your side and does not change while the page waits:
   * only the same key can ask about the code. `ip` is the visitor's address and is required: take it from the
   * connection, or from a header that your own reverse proxy sets (never one the visitor can send).
   * Returns null when this address or page asked for too many in the last minute, or the table is full.
   */
  /** `check` is there when the relay checks connections (`connectCheck`, or `connect.check` in the manifest). */
  newCode(pageKey: string, options: { ip: string }): { code: string; link: string; expiresAt: string; check?: string } | null;
  /** Has the wallet come with this code? `connected` carries the pairing token for the page to keep. */
  codeState(pageKey: string, code: string): { state: 'waiting' | 'expired' } | { state: 'connected'; pairing: string };
  /** The page stopped waiting: a wallet that arrives with this code later hears "bad-session". */
  cancel(pageKey: string, code: string): boolean;
  /** The live wallet of a pairing token (what a page keeps after connecting), or undefined. The pairing is a bearer secret for your server. */
  walletFor(pairing: string): WalletConnection | undefined;
  pairingState(pairing: string): 'live' | 'offline' | 'unknown';
  /** Forgets a pairing and says bye to its wallet. */
  forget(pairing: string): boolean;

  /** Serves wallet connections on an HTTP(S) server at `path` (default `/byoa/ws`). Call once per server. Needs the `ws` package. */
  attach(server: Server | HttpsServer, path?: string): Promise<this>;
  /** Stops listening for wallets on every attached server (open connections stay until `closeAll`). */
  detach(): void;
  /** Serves one `ws` WebSocket that your framework already accepted. */
  serve(ws: unknown): void;
  /** Closes every wallet connection, welcomed or not, and stops the housekeeping timer (shutdown). */
  closeAll(): void;

  on(event: 'connected', listener: (wallet: WalletConnection) => void): this;
  /** A connection ended. If `wallet.replaced` is true a newer one with the same pairing is already live: do not tear down the page. */
  on(event: 'disconnected', listener: (wallet: WalletConnection) => void): this;
  /** A pairing was pushed out of a full table (its wallet got "bye"). */
  on(event: 'evicted', listener: (pairing: string) => void): this;
  /** `forget(pairing)` was called. */
  on(event: 'forgotten', listener: (pairing: string) => void): this;
}

export function createRelay(options: RelayOptions): Relay;
/** Compact JSON, UTF-8, base64url without padding. Throws if it is not an object or is over 4096 bytes. */
export function encodeManifest(manifest: object): string;
/** `bringyourownagent://connect?v=0.1&endpoint=...&manifest=...&session=...` */
export function connectLink(endpoint: string, manifestB64: string, code: string): string;
/** The alphabet of the connect check code (no I, L, O, 0, 1). */
export const CHECK_ALPHABET: string;
/** The connect check code of a session (spec/CONTRACT-DIRECT.md section 3a): four characters the page and the wallet both show. */
export function connectCheck(session: string): string;
export const CODE_LIFE_MS: number;
export const MAX_FRAME_BYTES: number;
export const MAX_MANIFEST_BYTES: number;
