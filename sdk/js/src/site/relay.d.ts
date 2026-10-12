// Type declarations for bringyourownagent/relay: the site's side of the direct link (spec/CONTRACT-DIRECT.md).

import type { EventEmitter } from 'node:events';
import type { Server } from 'node:http';
import type { Server as HttpsServer } from 'node:https';

/** The answer to one call. Never an exception: `{ ok: false }` means "unavailable for this call"; use your own path. */
export type CallResult =
  | { ok: true; output: Record<string, unknown>; receipt: { cost: string; provider: string } | null }
  | { ok: false; timedOut?: boolean; cancelled?: boolean };

/** One wallet connected to the server. */
export class WalletConnection extends EventEmitter {
  /** The pairing token this wallet was welcomed with. */
  readonly pairing: string | null;
  /** What the wallet said it was granted: skills and when the grant ends. */
  readonly grant: { skills: string[]; expiresAt: string | null } | null;
  /** The wallet's own name and version, as it announced them. Informational only. */
  readonly wallet: Record<string, unknown> | null;
  /** True while the wallet can take calls. */
  readonly live: boolean;
  /**
   * One use of a skill. Never rejects. At most 4 calls are in flight to one wallet; the rest wait their turn.
   * The first call after a connect gets a longer wait (a cold model may need time).
   */
  call(skill: string, input: unknown, params?: Record<string, unknown>, options?: { timeoutMs?: number }): Promise<CallResult>;
  /** Says "bye" and closes. */
  bye(): void;
  close(code?: number, reason?: string): void;
  on(event: 'close', listener: () => void): this;
}

export interface RelayOptions {
  /** The manifest this site asks with. It is validated now and travels, as it is, in every link (at most 4096 bytes). */
  manifest: object;
  /** The wallet's WebSocket URL, `wss://your-host/byoa/ws`. */
  endpoint: string;
  /** Codes one address may ask for in a minute. Default 10. */
  codesPerMinute?: number;
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
  /** Default 1000. */
  maxPairings?: number;
  /** Default 2000. */
  maxCodes?: number;
  /** A clock in milliseconds, for tests. */
  now?: () => number;
}

export class Relay extends EventEmitter {
  constructor(options: RelayOptions);
  readonly manifest: object;
  /** The manifest as the link carries it: base64url, no padding. */
  readonly manifestB64: string;
  readonly endpoint: string;
  readonly pairingCount: number;

  /**
   * A new one-time code for one page (192 random bits, valid 120 s, single use) and the link that carries it.
   * `pageKey` is any string that names the page or game session on your side: only the same key can ask about the code.
   * Returns null when this address asked for too many in the last minute.
   */
  newCode(pageKey: string, options?: { ip?: string }): { code: string; link: string; expiresAt: string } | null;
  /** Has the wallet come with this code? `connected` carries the pairing token for the page to keep. */
  codeState(pageKey: string, code: string): { state: 'waiting' | 'expired' } | { state: 'connected'; pairing: string };
  /** The page stopped waiting: a wallet that arrives with this code later hears "bad-session". */
  cancel(pageKey: string, code: string): boolean;
  /** The live wallet of a pairing token (what a page keeps after connecting), or undefined. */
  walletFor(pairing: string): WalletConnection | undefined;
  pairingState(pairing: string): 'live' | 'offline' | 'unknown';
  /** Forgets a pairing and says bye to its wallet. */
  forget(pairing: string): boolean;

  /** Serves wallet connections on an HTTP(S) server at `path` (default `/byoa/ws`). Needs the `ws` package. */
  attach(server: Server | HttpsServer, path?: string): Promise<this>;
  /** Serves one `ws` WebSocket that your framework already accepted. */
  serve(ws: unknown): void;
  /** Closes every wallet connection (shutdown). */
  closeAll(): void;

  on(event: 'connected', listener: (wallet: WalletConnection) => void): this;
  on(event: 'disconnected', listener: (wallet: WalletConnection) => void): this;
}

export function createRelay(options: RelayOptions): Relay;
/** Compact JSON, UTF-8, base64url without padding. Throws if it is not an object or is over 4096 bytes. */
export function encodeManifest(manifest: object): string;
/** `bringyourownagent://connect?v=0.1&endpoint=...&manifest=...&session=...` */
export function connectLink(endpoint: string, manifestB64: string, code: string): string;
export const CODE_LIFE_MS: number;
export const MAX_FRAME_BYTES: number;
export const MAX_MANIFEST_BYTES: number;
