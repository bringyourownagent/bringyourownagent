// Type declarations for bringyourownagent (BYOA, Bring Your Own Agent).
// Rules: spec/rules-0.1-draft.md (a draft for discussion).

/** The four decisions of the call gate, mildest first. */
export type Decision = 'allowed' | 'warn' | 'ask' | 'blocked';

/** How sensitive the requester's data is. */
export type PrivacyClass = 'public' | 'personal' | 'personal-voice' | 'sensitive' | 'local-only';

/** Kinds of provider the agent wallet can route a call to. */
export type ProviderKind = 'subscription' | 'api-key' | 'connected-account' | 'local-model' | 'home-server';

/** A money string: "<amount> <CUR>", e.g. "0.06 USD". At most 12 digits before the point and 6 after. */
export type MoneyString = string;

/** An RFC 3339 date-time with an offset, e.g. "2026-10-10T13:00:00Z". */
export type Timestamp = string;

/** Hard limits, in the manifest (`purpose.limits`) or the grant (`limits`). */
export interface Limits {
  maxClipSeconds?: number;
  maxCallsPerMinute?: number;
  /** Bytes of one frame from the wallet (the direct link only). */
  maxFrameBytes?: number;
  maxTextChars?: number;
}

/** A condition on one property of the sources a site asks for, inside `models.needs`. */
export type NeedsCondition =
  | ('audio' | 'image' | 'text')[]
  | { includes: ('audio' | 'image' | 'text')[] }
  | number
  | { min: number }
  | ('local' | 'home-server' | 'api-key')[]
  | { oneOf: ('local' | 'home-server' | 'api-key')[] };

/** The `models` block of a skill (CONTRACT.md section 3.3). Only these fields are typed; a block without `count` is valid. */
export interface ModelsRequest {
  count?: number | { min?: number; max?: number };
  distinct?: 'vendor' | 'model';
  require?: unknown[];
  recommend?: unknown[];
  /** Conditions on the properties of the sources (wallet 0.36). */
  needs?: { input?: NeedsCondition; output?: NeedsCondition; context?: NeedsCondition; runs?: NeedsCondition; [property: string]: unknown };
  /** Which sources may fill the skill: a model, the visitor's agent, or either (wallet 0.36). */
  kind?: 'model' | 'agent' | 'any';
  /** What happens to a source that fails a known condition (wallet 0.36). */
  onMismatch?: 'drop' | 'show';
  /** The agent abilities the site hopes for (wallet 0.36). */
  abilities?: string[];
  why?: string;
  whyLong?: string;
  names?: boolean;
}

/** One skill the requester asks for. Other parameters are skill-specific. */
export interface SkillRequest {
  required: boolean;
  models?: ModelsRequest;
  [parameter: string]: unknown;
}

/** What the requester needs and why. */
export interface Manifest {
  byoa: '0.1-draft';
  purpose: { summary: string; limits?: Limits };
  /** Keyed by skill id in the form `domain.action`, e.g. `audio.transcribe`. */
  skills: Record<string, SkillRequest>;
  privacy: PrivacyClass;
  expectedCost?: { perCall?: MoneyString; perSession?: MoneyString; perHour?: MoneyString };
}

/** What the visitor allowed. Unknown fields make a grant invalid (grants fail closed). */
export interface Grant {
  byoa: '0.1-draft';
  skills: string[];
  budget: { perSession: MoneyString };
  expiresAt: Timestamp;
  limits?: Limits;
  localOnly?: boolean;
  warnIf?: { sendsText?: boolean; callCostAbove?: MoneyString; spendAbove?: MoneyString };
}

/** One use of a skill, after the wallet has chosen a provider and measured the input. */
export interface Call {
  skill: string;
  at: Timestamp;
  provider: ProviderKind;
  estimatedCost: MoneyString;
  input?: { clipSeconds?: number; textChars?: number; [measurement: string]: unknown };
  params?: Record<string, unknown>;
  [field: string]: unknown;
}

/** What the wallet remembers within the session. */
export interface CallState {
  spent?: MoneyString;
  callTimes?: Timestamp[];
}

/** One problem found in a manifest. */
export interface Issue {
  /** Stable rule code, e.g. "privacy-unknown". */
  code: string;
  /** JSON Pointer into the manifest; "" is the whole document. */
  path: string;
  /** For people; may change between versions. */
  message: string;
}

export interface ValidationResult {
  ok: boolean;
  errors: Issue[];
  warnings: Issue[];
}

/** Why the gate decided as it did. */
export interface Reason {
  code: string;
  decision: Decision;
  message: string;
  /** Only on manifest-invalid, grant-invalid, call-invalid and state-invalid: a JSON Pointer into that document. */
  path?: string;
}

export interface CheckResult {
  decision: Decision;
  reasons: Reason[];
}

/** What a purpose judge thinks of one call. */
export interface Opinion {
  decision: Decision;
  /** Defaults to "purpose-doubt". */
  code?: string;
  message?: string;
}

/** The purpose judge hook. Return null or undefined for no opinion. Must be synchronous inside checkCall. */
export type PurposeJudge = (manifest: unknown, grant: unknown, call: unknown) => Opinion | null | undefined;

export interface CheckOptions {
  /** Absent or null: no judge. A value that is not a function gives `purpose-judge-error` (ask). */
  judge?: PurposeJudge | null;
}

/** Check a manifest against the 0.1-draft rules. Never throws for bad input. */
export function validateManifest(manifest: unknown): ValidationResult;

/**
 * Decide one call: allowed, warn, ask or blocked. Never throws for bad input; invalid documents block.
 * It only evaluates: it does not run the call and does not update the state.
 */
export function checkCall(
  manifest: unknown,
  grant: unknown,
  call: unknown,
  state?: unknown,
  options?: CheckOptions,
): CheckResult;

/** Merge a purpose judge's opinion into a gate result. It can only make the decision stricter. */
export function applyOpinion(result: CheckResult, opinion: Opinion | null | undefined): CheckResult;

/** The default purpose judge: it never has an opinion. */
export const noOpinion: PurposeJudge;

export const SUPPORTED_VERSIONS: readonly string[];
export const KNOWN_SKILLS: readonly string[];
export const PRIVACY_CLASSES: readonly PrivacyClass[];
export const KNOWN_LIMITS: readonly (keyof Limits)[];
export const COUNT_LIMITS: readonly (keyof Limits)[];
export const SOURCE_KINDS: readonly ('model' | 'agent' | 'any')[];
export const ON_MISMATCH: readonly ('drop' | 'show')[];
export const NEEDS_PROPERTIES: readonly ('input' | 'output' | 'context' | 'runs')[];
export const MODALITIES: readonly ('audio' | 'image' | 'text')[];
export const RUNS: readonly ('local' | 'home-server' | 'api-key')[];
export const ABILITIES: readonly string[];
export const COST_PERIODS: readonly ('perCall' | 'perSession' | 'perHour')[];
export const PROVIDER_KINDS: readonly ProviderKind[];
export const LOCAL_PROVIDERS: readonly ProviderKind[];
export const DECISIONS: readonly Decision[];

/** Version of this library (not of the protocol). */
export const VERSION: string;
