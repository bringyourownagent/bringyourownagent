// Type declarations for bringyourownagent/crossdevice: the page's "Open on another device" block (spec/CONTRACT-PAIR.md).

/** The words of the block. A site in another language passes its own for any of them. */
export interface CrossDeviceLabels {
  title: string;
  check: string;
  checkHow: string;
  linkHow: string;
  handoffHow: string;
  walletAsks: string;
  copy: string;
  copied: string;
  copyFailed: string;
  share: string;
  shareTitle: string;
  shareFailed: string;
  webWallet: string;
  qrLink: string;
  qrHandoff: string;
  qrWebWallet: string;
  qrAlt: string;
}

export const DEFAULT_LABELS: Readonly<CrossDeviceLabels>;
/** A little CSS for the block; add it to the site's own style sheet. */
export const CROSS_DEVICE_CSS: string;

/** True for a phone or tablet browser. The page should not fire the wallet's link itself there: nothing handles it. */
export function isMobileBrowser(nav?: Navigator): boolean;
/** True where the browser has the Web Share API. */
export function canShare(nav?: Navigator): boolean;
/** `<web wallet address>#link=<the link>`. Throws TypeError for a link that is not a BYOA link or an address that is not https. */
export function webWalletUrl(walletBase: string, link: string): string;
/** The session code inside a link, or null. */
export function sessionOfLink(link: string): string | null;
/** The connect check code of a session (four characters). The site's server sends it as `check`; this is for a page that has only the link. */
export function checkCodeOf(session: string, subtle?: SubtleCrypto): Promise<string>;

export interface CrossDevicePlan {
  /** What the visitor copies and shares: the hand-off address when there is one, else the link. */
  text: string;
  textKind: 'handoff' | 'link';
  walletUrl: string | null;
  /** What the QR code carries. */
  qrText: string;
  qrKind: 'webwallet' | 'handoff' | 'link';
}
export function planCrossDevice(options: { link: string; handoff?: string | null; webWallet?: string | null }): CrossDevicePlan;

export interface CrossDeviceOptions {
  /** The `bringyourownagent://connect?...` link the site's server made for this page. */
  link: string;
  /** The check code the server sent next to the link: shown large, with the line that tells the visitor to compare it. */
  check?: string;
  /** An https address the site made for this link; offered instead of the raw link. */
  handoff?: string;
  /** The address of a web wallet the site recommends. */
  webWallet?: string;
  /** 'auto' (the default) hides the QR code on a phone, which cannot scan itself. */
  showQr?: boolean | 'auto';
  qrEcl?: 'L' | 'M' | 'Q' | 'H';
  qrPixels?: number;
  labels?: Partial<CrossDeviceLabels>;
  document?: Document;
  navigator?: Navigator;
}

/** Builds the block. Everything is set as text: nothing in `link`, `check` or the labels is ever read as markup. */
export function createCrossDeviceBlock(options: CrossDeviceOptions): HTMLElement;
