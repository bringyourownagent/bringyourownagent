// Type declarations for bringyourownagent/browser: the page's side (spec/CONTRACT.md, spec/CONTRACT-DIRECT.md).

export interface Grant { grantId: string; skills: string[]; expiresAt: string }
export type ExtensionCallResult =
  | { ok: true; output: Record<string, unknown>; receipt: { cost: string; provider: string } }
  | { ok: false };

/** True when the wallet's browser extension is installed (it puts `window.byoa` on the page). */
export function hasExtension(): boolean;
/** Resolves true when the extension is on the page (it may announce itself before your script runs), or false after `timeoutMs` (default 1500). */
export function whenExtensionReady(timeoutMs?: number): Promise<boolean>;
/** Call from a click. A grant, or null for every kind of "no" (declined, blocked, no wallet, no fitting model). */
export function requestWithExtension(manifest: object): Promise<Grant | null>;
/** Never throws. The output is user input: validate it. */
export function callWithExtension(grant: Grant | null, skill: string, input: unknown, params?: Record<string, unknown>): Promise<ExtensionCallResult>;
export function releaseWithExtension(grant: Grant): Promise<void>;
/** Starts the wallet with its `bringyourownagent://connect?...` link. Throws for any other link. */
export function openWalletLink(link: string): void;

export interface ConnectOptions {
  /** POST, answers `{ code, link }` (or `{ session, link }`), see `relay.newCode`. Default `/byoa/code`. */
  newCodeUrl?: string;
  /** GET `?code=`, answers `{ state, pairing? }` (see `relay.codeState`). Default `/byoa/state`. */
  stateUrl?: string;
  /** POST `{ code }`. Default `/byoa/cancel`. */
  cancelUrl?: string;
  /** Default 90000. */
  waitMs?: number;
  /** Default 1000; never less than 5. */
  pollMs?: number;
  signal?: AbortSignal;
  /** Called once the server has answered, before the link opens: show `check` (the connect check code) and the link while you wait. */
  onIssued?: (issued: { code: string; link: string; check?: string }) => void;
  /** Replaces `openWalletLink`; on a phone pass `() => {}` and offer the link for another device (`bringyourownagent/crossdevice`). */
  open?: (link: string) => void;
}
export type ConnectResult =
  | { state: 'connected'; pairing: string }
  | { state: 'expired' | 'timeout' | 'cancelled' | 'error' };

/**
 * Call from a click. Asks the site's server for a code, opens the link, waits until the wallet has connected to the server.
 * A second call while one is waiting returns the first one's promise (one link is opened, not two).
 */
export function connectWithoutExtension(options?: ConnectOptions): Promise<ConnectResult>;
