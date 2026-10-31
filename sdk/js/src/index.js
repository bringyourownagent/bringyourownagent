// BYOA (Bring Your Own Agent): an early draft reference implementation.
//
// validateManifest(manifest) checks a requester's manifest and returns
// { ok, errors, warnings }. Problems are data, never exceptions.
// checkCall(manifest, grant, call, state) is the gate for one call: it returns
// { decision, reasons } where the decision is allowed, warn, ask or blocked.
//
// Rules: spec/rules-0.1-draft.md (a draft for discussion).
// https://bringyourownagent.org

export { validateManifest } from './manifest.js';
export { checkCall, applyOpinion, noOpinion } from './gate.js';
export {
  SUPPORTED_VERSIONS,
  KNOWN_SKILLS,
  PRIVACY_CLASSES,
  KNOWN_LIMITS,
  COUNT_LIMITS,
  SOURCE_KINDS,
  ON_MISMATCH,
  NEEDS_PROPERTIES,
  MODALITIES,
  RUNS,
  ABILITIES,
  COST_PERIODS,
  PROVIDER_KINDS,
  LOCAL_PROVIDERS,
  DECISIONS,
} from './rules.js';
export { VERSION } from './version.js';
