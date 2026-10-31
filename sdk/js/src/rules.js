// Vocabulary of the BYOA 0.1-draft rules.
// See spec/rules-0.1-draft.md in https://github.com/bringyourownagent/bringyourownagent.
// Everything here is a draft for discussion and may change.

/** Protocol versions this library understands (the `byoa` field). */
export const SUPPORTED_VERSIONS = Object.freeze(['0.1-draft']);

/** The first skill vocabulary. Well-formed ids outside it only produce a warning. */
export const KNOWN_SKILLS = Object.freeze([
  'text.generate',
  'text.understand',
  'text.embed',
  'audio.transcribe',
  'audio.speak',
  'image.understand',
  'image.generate',
  'video.understand',
  'video.generate',
]);

/** How sensitive the requester's data is (the manifest `privacy` field). */
export const PRIVACY_CLASSES = Object.freeze([
  'public',
  'personal',
  'personal-voice',
  'sensitive',
  'local-only',
]);

/** Hard limits a manifest (`purpose.limits`) or a grant (`limits`) may set. */
export const KNOWN_LIMITS = Object.freeze(['maxCallsPerMinute', 'maxClipSeconds', 'maxFrameBytes', 'maxTextChars']);

/** Limits that count things and therefore must be whole numbers. */
export const COUNT_LIMITS = Object.freeze(['maxCallsPerMinute', 'maxFrameBytes', 'maxTextChars']);

/** Source kinds a skill's `models.kind` may name (which sources may fill the skill). */
export const SOURCE_KINDS = Object.freeze(['model', 'agent', 'any']);

/** What a wallet does with a source that fails a known condition (`models.onMismatch`). */
export const ON_MISMATCH = Object.freeze(['drop', 'show']);

/** The four properties a `models.needs` condition may name. */
export const NEEDS_PROPERTIES = Object.freeze(['context', 'input', 'output', 'runs']);

/** Modalities of the `input` and `output` properties. */
export const MODALITIES = Object.freeze(['audio', 'image', 'text']);

/** Values of the `runs` property: the grant's own vocabulary, never a second one. */
export const RUNS = Object.freeze(['api-key', 'home-server', 'local']);

/** Abilities a `models.abilities` entry may name; `image.read` is reserved and not enforced. */
export const ABILITIES = Object.freeze(['web.read', 'web.search']);

/** Periods allowed in the manifest `expectedCost` object. */
export const COST_PERIODS = Object.freeze(['perCall', 'perSession', 'perHour']);

/** Kinds of provider the agent wallet can route a call to. */
export const PROVIDER_KINDS = Object.freeze([
  'subscription',
  'api-key',
  'connected-account',
  'local-model',
  'home-server',
]);

/** Providers that keep the data on the visitor's computer or in their home. */
export const LOCAL_PROVIDERS = Object.freeze(['local-model', 'home-server']);

/** The four decisions of the call gate, mildest first. */
export const DECISIONS = Object.freeze(['allowed', 'warn', 'ask', 'blocked']);

/** Longest allowed `purpose.summary`, in Unicode code points. */
export const SUMMARY_MAX_LENGTH = 200;

/** Longest allowed skill id, in characters. */
export const SKILL_ID_MAX_LENGTH = 64;

/** The rate limit window, in milliseconds. */
export const RATE_WINDOW_MS = 60000;
