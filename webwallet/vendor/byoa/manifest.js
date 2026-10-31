// Manifest rules M1-M28 of spec/rules-0.1-draft.md (a draft for discussion).

import {
  codePointLength,
  get,
  isBlank,
  isCount,
  isNonNegative,
  isObject,
  isSkillId,
  pointer,
  presentKeys,
  quote,
} from './common.js';
import { parseMoney } from './money.js';
import {
  ABILITIES,
  COST_PERIODS,
  COUNT_LIMITS,
  KNOWN_LIMITS,
  KNOWN_SKILLS,
  MODALITIES,
  NEEDS_PROPERTIES,
  ON_MISMATCH,
  PRIVACY_CLASSES,
  RUNS,
  SOURCE_KINDS,
  SUMMARY_MAX_LENGTH,
  SUPPORTED_VERSIONS,
} from './rules.js';

const TOP_LEVEL_FIELDS = ['byoa', 'purpose', 'skills', 'privacy', 'expectedCost'];
const PURPOSE_FIELDS = ['summary', 'limits'];

/**
 * @typedef {{ code: string, path: string, message: string }} Issue
 * @typedef {{ ok: boolean, errors: Issue[], warnings: Issue[] }} ValidationResult
 */

/**
 * Why a known limit value is invalid, or null if it is fine.
 * @param {string} name
 * @param {unknown} value
 * @returns {string | null}
 */
export function limitProblem(name, value) {
  if (COUNT_LIMITS.includes(name)) {
    if (!isCount(value)) return `limit ${quote(name)} must be a whole number from 0 to 9007199254740991`;
  } else if (!isNonNegative(value)) {
    return `limit ${quote(name)} must be a number from 0 to 9007199254740991`;
  }
  return null;
}

/**
 * @param {unknown} value
 * @returns {string | null}
 */
export function versionProblem(value) {
  if (value === undefined) {
    return 'field "byoa" is missing; it names the protocol version, e.g. "0.1-draft"';
  }
  if (!(typeof value === 'string' && SUPPORTED_VERSIONS.includes(value))) {
    return `protocol version ${quote(value)} is not supported; supported: ${SUPPORTED_VERSIONS.map(quote).join(', ')}`;
  }
  return null;
}

/**
 * Check a manifest against the 0.1-draft rules.
 *
 * Never throws for bad input: problems are returned as data. `ok` is true
 * when there are no errors; warnings never make a manifest invalid.
 *
 * @param {unknown} manifest
 * @returns {ValidationResult}
 */
export function validateManifest(manifest) {
  /** @type {Issue[]} */
  const errors = [];
  /** @type {Issue[]} */
  const warnings = [];
  const error = (code, path, message) => errors.push({ code, path, message });
  const warn = (code, path, message) => warnings.push({ code, path, message });

  // M1
  if (!isObject(manifest)) {
    error('manifest-not-object', '', 'the manifest must be a JSON object');
    return { ok: false, errors, warnings };
  }

  // M2, M3
  const version = get(manifest, 'byoa');
  const problem = versionProblem(version);
  if (problem !== null) {
    error(version === undefined ? 'version-missing' : 'version-unsupported', '/byoa', problem);
  }

  // M4-M9
  const purpose = get(manifest, 'purpose');
  if (!isObject(purpose)) {
    error('purpose-missing', '/purpose', 'field "purpose" is missing or is not an object');
  } else {
    checkPurpose(purpose, error, warn);
  }

  // M10-M15
  const skills = get(manifest, 'skills');
  if (!isObject(skills) || presentKeys(skills).length === 0) {
    error('skills-missing', '/skills', 'field "skills" must be an object with at least one skill');
  } else {
    checkSkills(skills, error, warn);
  }

  // M16, M17
  const privacy = get(manifest, 'privacy');
  const classes = PRIVACY_CLASSES.join(', ');
  if (privacy === undefined) {
    error('privacy-missing', '/privacy', `field "privacy" is missing; use one of: ${classes}`);
  } else if (!(typeof privacy === 'string' && PRIVACY_CLASSES.includes(privacy))) {
    error('privacy-unknown', '/privacy', `privacy class ${quote(privacy)} is unknown; use one of: ${classes}`);
  }

  // M18-M20
  const cost = get(manifest, 'expectedCost');
  if (cost === undefined) {
    warn(
      'cost-missing',
      '/expectedCost',
      'field "expectedCost" is missing; visitors decide faster when they see an expected cost',
    );
  } else if (!isObject(cost)) {
    error('cost-invalid', '/expectedCost', '"expectedCost" must be an object such as {"perSession": "0.06 USD"}');
  } else {
    const keys = presentKeys(cost);
    if (!keys.some((key) => COST_PERIODS.includes(key))) {
      warn('cost-missing', '/expectedCost', `"expectedCost" has none of: ${COST_PERIODS.join(', ')}`);
    }
    for (const key of keys) {
      const path = pointer('expectedCost', key);
      if (COST_PERIODS.includes(key)) {
        if (parseMoney(cost[key]) === null) {
          error('cost-invalid', path, `"expectedCost.${key}" must be a money string such as "0.06 USD"`);
        }
      } else {
        warn('field-unknown', path, `unknown field ${quote(key)} in "expectedCost"; this version ignores it`);
      }
    }
  }

  // M21
  for (const key of presentKeys(manifest)) {
    if (!TOP_LEVEL_FIELDS.includes(key)) {
      warn('field-unknown', pointer(key), `unknown field ${quote(key)}; this version ignores it`);
    }
  }

  return { ok: errors.length === 0, errors, warnings };
}

function checkPurpose(purpose, error, warn) {
  const summary = get(purpose, 'summary');
  if (typeof summary !== 'string' || isBlank(summary)) {
    error(
      'summary-missing',
      '/purpose/summary',
      '"purpose.summary" must be a non-empty string: one short sentence for the visitor',
    );
  } else if (codePointLength(summary) > SUMMARY_MAX_LENGTH) {
    error(
      'summary-too-long',
      '/purpose/summary',
      `"purpose.summary" has ${codePointLength(summary)} characters; the limit is ${SUMMARY_MAX_LENGTH}`,
    );
  }

  const limits = get(purpose, 'limits');
  if (limits !== undefined) {
    if (!isObject(limits)) {
      error('limit-invalid', '/purpose/limits', '"purpose.limits" must be an object');
    } else {
      for (const name of presentKeys(limits)) {
        const path = pointer('purpose', 'limits', name);
        if (KNOWN_LIMITS.includes(name)) {
          const problem = limitProblem(name, limits[name]);
          if (problem !== null) error('limit-invalid', path, problem);
        } else {
          warn('limit-unknown', path, `unknown limit ${quote(name)}; wallets that do not know it will ignore it`);
        }
      }
    }
  }

  for (const key of presentKeys(purpose)) {
    if (!PURPOSE_FIELDS.includes(key)) {
      warn('field-unknown', pointer('purpose', key), `unknown field ${quote(key)} in "purpose"; this version ignores it`);
    }
  }
}

function checkSkills(skills, error, warn) {
  let anyRequired = false;
  for (const skillId of presentKeys(skills)) {
    const path = pointer('skills', skillId);
    if (!isSkillId(skillId)) {
      error(
        'skill-id-invalid',
        path,
        `skill id ${quote(skillId)} must have the form domain.action in lower case, e.g. "audio.transcribe"`,
      );
    } else if (!KNOWN_SKILLS.includes(skillId)) {
      warn('skill-unknown', path, `skill ${quote(skillId)} is not in the 0.1-draft vocabulary; check the spelling`);
    }
    const entry = skills[skillId];
    if (!isObject(entry)) {
      error('skill-invalid', path, `skill ${quote(skillId)} must be an object of parameters`);
      continue;
    }
    const required = get(entry, 'required');
    if (typeof required !== 'boolean') {
      error('skill-required-invalid', path + '/required', `skill ${quote(skillId)} must state "required": true or false`);
    } else if (required) {
      anyRequired = true;
    }
    const models = get(entry, 'models');
    if (models !== undefined) {
      checkModels(skillId, models, error, warn);
    }
  }
  if (!anyRequired) {
    warn(
      'no-required-skill',
      '/skills',
      'no skill is marked "required": true; the site claims to work without any of them',
    );
  }
}

// M22-M28: the `models` block of a skill. Only `kind`, `onMismatch`, `needs` and `abilities` are
// checked here; `count`, `distinct`, `require`, `recommend`, `why`, `whyLong` and `names` stay the
// wallet's to read, and a block without `count` is valid (CONTRACT.md section 3.3).
function checkModels(skillId, models, error, warn) {
  const path = pointer('skills', skillId, 'models');
  if (!isObject(models)) {
    error('models-invalid', path, `"models" of skill ${quote(skillId)} must be an object`);
    return;
  }

  const kind = get(models, 'kind');
  if (kind !== undefined && !SOURCE_KINDS.includes(kind)) {
    error(
      'models-kind-invalid',
      path + '/kind',
      `"models.kind" must be one of: ${SOURCE_KINDS.map(quote).join(', ')}; got ${quote(kind)}`,
    );
  }

  const onMismatch = get(models, 'onMismatch');
  if (onMismatch !== undefined && !ON_MISMATCH.includes(onMismatch)) {
    error(
      'models-onmismatch-invalid',
      path + '/onMismatch',
      `"models.onMismatch" must be "drop" or "show"; got ${quote(onMismatch)}`,
    );
  }

  const needs = get(models, 'needs');
  if (needs !== undefined) {
    checkNeeds(skillId, needs, error, warn);
  }

  const abilities = get(models, 'abilities');
  if (abilities !== undefined) {
    checkAbilities(skillId, abilities, error, warn);
  }
}

function checkNeeds(skillId, needs, error, warn) {
  const path = pointer('skills', skillId, 'models', 'needs');
  if (!isObject(needs)) {
    error('needs-invalid', path, `"models.needs" of skill ${quote(skillId)} must be an object of conditions by property`);
    return;
  }
  for (const key of presentKeys(needs)) {
    const keyPath = path + pointer(key);
    const value = needs[key];
    if (!NEEDS_PROPERTIES.includes(key)) {
      if (!key.startsWith('x-')) {
        warn('needs-unknown', keyPath, `unknown property ${quote(key)} in "models.needs"; wallets that do not know it will ignore it`);
      }
      continue;
    }
    const problem = needsProblem(key, value);
    if (problem !== null) error('needs-invalid', keyPath, problem);
  }
}

// Why a condition of `needs` is invalid, or null if it is fine. An array is the short form of the
// one long form each property takes: {"includes": [...]} for a modality, {"oneOf": [...]} for `runs`,
// {"min": n} for `context` (CONTRACT.md sections 3.3 and 3.5).
function needsProblem(key, value) {
  if (key === 'input' || key === 'output') {
    const list = listOf(value, 'includes');
    if (list !== null && isModalityList(list)) return null;
    return `"models.needs.${key}" must be an array of ${MODALITIES.map(quote).join(', ')} with at least one value, or {"includes": [...]} of those; got ${quote(value)}`;
  }
  if (key === 'runs') {
    const list = listOf(value, 'oneOf');
    if (list !== null && isList(RUNS, list)) return null;
    return `"models.needs.${key}" must be an array of ${RUNS.map(quote).join(', ')} with at least one value, or {"oneOf": [...]} of those; got ${quote(value)}`;
  }
  // context: a minimum, in thousands of tokens
  if (isNonNegative(value)) return null;
  if (isObject(value) && presentKeys(value).length === 1 && get(value, 'min') !== undefined && isNonNegative(value.min)) return null;
  return `"models.needs.${key}" must be a number of thousands of tokens (the minimum) or {"min": n}; got ${quote(value)}`;
}

// The array of a short form, or of the one long form `key`, or null when the value is neither.
function listOf(value, key) {
  if (Array.isArray(value)) return value;
  if (isObject(value) && presentKeys(value).length === 1 && Array.isArray(value[key])) return value[key];
  return null;
}

function isModalityList(list) {
  return isList(MODALITIES, list);
}

// A non-empty array of strings, all of them in `allowed`.
function isList(allowed, list) {
  return Array.isArray(list) && list.length > 0 && list.every((item) => typeof item === 'string' && allowed.includes(item));
}

function checkAbilities(skillId, abilities, error, warn) {
  const path = pointer('skills', skillId, 'models', 'abilities');
  if (!Array.isArray(abilities) || abilities.length === 0 || !abilities.every((item) => typeof item === 'string')) {
    error('abilities-invalid', path, `"models.abilities" of skill ${quote(skillId)} must be an array of ability names, e.g. ["web.search"]`);
    return;
  }
  abilities.forEach((item, at) => {
    if (!ABILITIES.includes(item) && !item.startsWith('x-')) {
      warn(
        'abilities-unknown',
        path + pointer(String(at)),
        `ability ${quote(item)} is not in the 0.1 vocabulary; wallets that do not know it will ignore it`,
      );
    }
  });
}
