// The call gate: rules C1-C16 of spec/rules-0.1-draft.md (a draft for discussion).
//
// checkCall checks the hard limits that code can check, reliably and for free.
// Judging the declared purpose in words needs a model; that part plugs in
// through the purpose judge hook (options.judge or applyOpinion) and has no
// opinion by default.

import {
  formatNumber,
  get,
  isArray,
  isCount,
  isNonNegative,
  isObject,
  isSkillId,
  pointer,
  presentKeys,
  quote,
} from './common.js';
import { addMoney, formatMoney, parseMoney, zero } from './money.js';
import { parseTimestamp } from './time.js';
import { limitProblem, validateManifest, versionProblem } from './manifest.js';
import { DECISIONS, KNOWN_LIMITS, LOCAL_PROVIDERS, PROVIDER_KINDS, RATE_WINDOW_MS } from './rules.js';

const GRANT_FIELDS = ['byoa', 'skills', 'budget', 'expiresAt', 'limits', 'localOnly', 'warnIf'];
const STATE_FIELDS = ['spent', 'callTimes'];
const REFUSED = 'this version cannot enforce it, so the grant is refused';
const severity = (decision) => DECISIONS.indexOf(decision);

/**
 * @typedef {'allowed' | 'warn' | 'ask' | 'blocked'} Decision
 * @typedef {{ code: string, decision: Decision, message: string, path?: string }} Reason
 * @typedef {{ decision: Decision, reasons: Reason[] }} CheckResult
 * @typedef {{ decision: Decision, code?: string, message?: string }} Opinion
 * @typedef {(manifest: unknown, grant: unknown, call: unknown) => Opinion | null | undefined} PurposeJudge
 */

/**
 * The default purpose judge: it never has an opinion.
 * @type {PurposeJudge}
 */
export function noOpinion() {
  return null;
}

/**
 * Decide one call: `allowed`, `warn`, `ask` or `blocked`.
 *
 * `state` may be null or undefined (nothing spent, no calls yet).
 * `options.judge` is the purpose judge hook: absent or null means no judge;
 * anything else must be a synchronous function, otherwise the result is `ask`.
 * For an asynchronous judge, call checkCall without one, await your judge,
 * then use applyOpinion. Never throws for bad input: invalid documents block
 * the call. checkCall only evaluates: it does not run the call and does not
 * update the state.
 *
 * @param {unknown} manifest
 * @param {unknown} grant
 * @param {unknown} call
 * @param {unknown} [state]
 * @param {{ judge?: PurposeJudge }} [options]
 * @returns {CheckResult}
 */
export function checkCall(manifest, grant, call, state, options = {}) {
  /** @type {Reason[]} */
  let reasons = [];

  // Step 1: inputs (C1-C4).
  for (const issue of validateManifest(manifest).errors) {
    reasons.push(makeReason('manifest-invalid', 'blocked', `${issue.code}: ${issue.message}`, issue.path));
  }
  const parsedGrant = readGrant(grant);
  for (const [path, message] of parsedGrant.problems) {
    reasons.push(makeReason('grant-invalid', 'blocked', message, path));
  }
  const parsedCall = readCall(call);
  for (const [path, message] of parsedCall.problems) {
    reasons.push(makeReason('call-invalid', 'blocked', message, path));
  }
  const parsedState = readState(state);
  for (const [path, message] of parsedState.problems) {
    reasons.push(makeReason('state-invalid', 'blocked', message, path));
  }
  if (reasons.length > 0 || !parsedGrant.value || !parsedCall.value || !parsedState.value) {
    return makeResult(reasons);
  }

  // Step 1b: measurements that a limit or a warning in force needs (C3).
  const limits = effectiveLimits(manifest, parsedGrant.value);
  const missing = missingMeasurements(limits, parsedGrant.value, parsedCall.value);
  if (missing.length > 0) {
    return makeResult(missing.map(([path, message]) => makeReason('call-invalid', 'blocked', message, path)));
  }

  // Step 2: hard limits (C5-C16).
  reasons = hardLimits(manifest, limits, parsedGrant.value, parsedCall.value, parsedState.value);
  let result = makeResult(reasons);

  // Step 3: the purpose judge, only if the call is not already blocked.
  // Absent, null or undefined means no judge; anything else must be a function.
  const judge = options === null || options === undefined ? undefined : options.judge;
  if (judge !== undefined && judge !== null && result.decision !== 'blocked') {
    if (typeof judge !== 'function') {
      return judgeError(result, 'the purpose judge is not callable; ask the visitor');
    }
    let opinion;
    try {
      opinion = judge(manifest, grant, call);
    } catch {
      return judgeError(result, 'the purpose judge failed; ask the visitor');
    }
    if (opinion !== null && typeof opinion === 'object' && typeof opinion.then === 'function') {
      Promise.resolve(opinion).catch(() => {});
      return judgeError(
        result,
        'the purpose judge returned a Promise; checkCall is synchronous: await the judge yourself and use applyOpinion',
      );
    }
    result = applyOpinion(result, opinion);
  }
  return result;
}

/**
 * Merge a purpose judge's opinion into a gate result.
 *
 * An opinion can only make the decision stricter. null, undefined and
 * `allowed` change nothing. Anything that is not a valid opinion adds
 * `purpose-judge-error` with decision `ask`. Returns a new result.
 *
 * @param {CheckResult} result
 * @param {Opinion | null | undefined} opinion
 * @returns {CheckResult}
 */
export function applyOpinion(result, opinion) {
  if (opinion === null || opinion === undefined) {
    return { decision: result.decision, reasons: [...result.reasons] };
  }
  if (!isOpinion(opinion)) {
    return judgeError(result, 'the purpose judge returned something that is not an opinion; ask the visitor');
  }
  const decision = get(opinion, 'decision');
  if (decision === 'allowed') {
    return { decision: result.decision, reasons: [...result.reasons] };
  }
  const reason = makeReason(
    get(opinion, 'code') || 'purpose-doubt',
    decision,
    get(opinion, 'message') || 'the purpose judge doubts that this call fits the declared purpose',
  );
  return escalate(result, reason);
}

// ---------------------------------------------------------------------------
// Effective limits and required measurements

/** The stricter of the manifest limit and the grant limit, per known limit. */
function effectiveLimits(manifest, grant) {
  let manifestLimits = get(get(manifest, 'purpose'), 'limits');
  if (!isObject(manifestLimits)) manifestLimits = {};
  const limits = {};
  for (const name of KNOWN_LIMITS) {
    const values = [get(manifestLimits, name), grant.limits[name]].filter((v) => v !== undefined);
    if (values.length > 0) limits[name] = Math.min(...values);
  }
  return limits;
}

function missingMeasurements(limits, grant, call) {
  const problems = [];
  if (limits.maxClipSeconds !== undefined && call.clipSeconds === undefined) {
    problems.push([
      '/input/clipSeconds',
      '"input.clipSeconds" is required while a clip length limit applies; use 0 when the call sends no audio',
    ]);
  }
  if ((limits.maxTextChars !== undefined || grant.warnSendsText) && call.textChars === undefined) {
    problems.push([
      '/input/textChars',
      '"input.textChars" is required while a text limit or a text warning applies; use 0 when the call sends no text',
    ]);
  }
  return problems;
}

// ---------------------------------------------------------------------------
// Step 2

function hardLimits(manifest, limits, grant, call, state) {
  /** @type {Reason[]} */
  const reasons = [];
  const add = (code, decision, message) => reasons.push(makeReason(code, decision, message));
  const effective = (name) => limits[name];

  // C5
  if (call.at >= grant.expiresAt) {
    add('grant-expired', 'blocked', `the grant expired at ${grant.expiresText}`);
  }
  // C6
  if (get(get(manifest, 'skills'), call.skill) === undefined) {
    add('skill-not-declared', 'blocked', `skill ${quote(call.skill)} is not declared in the manifest`);
  }
  // C7
  if (!grant.skills.has(call.skill)) {
    add('skill-not-granted', 'blocked', `skill ${quote(call.skill)} is not in the grant`);
  }
  // C8
  const byGrant = grant.localOnly;
  const byManifest = get(manifest, 'privacy') === 'local-only';
  if ((byGrant || byManifest) && !LOCAL_PROVIDERS.includes(call.provider)) {
    const who =
      byGrant && byManifest
        ? 'the grant and the manifest allow'
        : byGrant
          ? 'the grant allows'
          : 'the manifest allows';
    add(
      'not-local',
      'blocked',
      `${who} local providers only (local-model, home-server); this call would use ${quote(call.provider)}`,
    );
  }
  // C9
  const maxClip = effective('maxClipSeconds');
  if (maxClip !== undefined && call.clipSeconds !== undefined && call.clipSeconds > maxClip) {
    add(
      'clip-too-long',
      'blocked',
      `the clip is ${formatNumber(call.clipSeconds)} seconds; the limit is ${formatNumber(maxClip)}`,
    );
  }
  // C10
  const maxText = effective('maxTextChars');
  if (maxText !== undefined && call.textChars !== undefined && call.textChars > maxText) {
    add(
      'text-too-long',
      'blocked',
      `the text has ${formatNumber(call.textChars)} characters; the limit is ${formatNumber(maxText)}`,
    );
  }
  // C11
  const maxRate = effective('maxCallsPerMinute');
  if (maxRate !== undefined) {
    const recent = state.callTimes.filter((t) => call.at - RATE_WINDOW_MS < t && t <= call.at).length;
    if (recent >= maxRate) {
      add('rate-exceeded', 'blocked', `${recent} calls in the last minute; the limit is ${formatNumber(maxRate)} per minute`);
    }
  }
  // C12
  const budget = grant.budget;
  const spent = state.spent !== undefined ? state.spent : zero(budget.currency);
  const mismatches = [];
  if (call.cost.currency !== budget.currency) mismatches.push(`the estimated cost is in ${call.cost.currency}`);
  if (spent.currency !== budget.currency) mismatches.push(`the session spend is in ${spent.currency}`);
  let after;
  if (mismatches.length > 0) {
    add(
      'currency-mismatch',
      'blocked',
      `${mismatches.join(' and ')}, but the budget is in ${budget.currency}; the gate does not convert currencies`,
    );
  } else {
    // C13
    after = addMoney(spent, call.cost);
    if (after.micros > budget.micros) {
      add(
        'budget-exceeded',
        'ask',
        `this call (${formatMoney(call.cost)}) would bring the session spend to ${formatMoney(after)}, above the budget of ${formatMoney(budget)}`,
      );
    }
  }
  // C14
  if (grant.warnSendsText && call.textChars !== undefined && call.textChars > 0) {
    add(
      'text-sent',
      'warn',
      `this call sends text (${formatNumber(call.textChars)} characters) and the visitor asked to be warned`,
    );
  }
  if (after !== undefined) {
    // C15
    if (grant.warnCallCost !== undefined && call.cost.micros > grant.warnCallCost.micros) {
      add(
        'call-cost-high',
        'warn',
        `this call costs ${formatMoney(call.cost)}, above the visitor's warning level of ${formatMoney(grant.warnCallCost)}`,
      );
    }
    // C16
    if (grant.warnSpend !== undefined && after.micros > grant.warnSpend.micros) {
      add(
        'spend-high',
        'warn',
        `the session spend would reach ${formatMoney(after)}, above the visitor's warning level of ${formatMoney(grant.warnSpend)}`,
      );
    }
  }
  return reasons;
}

// ---------------------------------------------------------------------------
// Step 1: reading the grant, the call and the state

function readGrant(grant) {
  if (!isObject(grant)) return { value: null, problems: [['', 'the grant must be a JSON object']] };
  const problems = [];
  const bad = (path, message) => problems.push([path, message]);

  const problem = versionProblem(get(grant, 'byoa'));
  if (problem !== null) bad('/byoa', problem);

  const skills = get(grant, 'skills');
  const granted = new Set();
  if (!isArray(skills)) {
    bad('/skills', '"skills" must be an array of skill ids');
  } else {
    skills.forEach((skillId, index) => {
      if (isSkillId(skillId)) granted.add(skillId);
      else bad(`/skills/${index}`, `${quote(skillId)} is not a skill id of the form domain.action`);
    });
  }

  let budget = null;
  const budgetObj = get(grant, 'budget');
  if (!isObject(budgetObj)) {
    bad('/budget', '"budget" must be an object such as {"perSession": "0.20 USD"}');
  } else {
    budget = parseMoney(get(budgetObj, 'perSession'));
    if (budget === null) bad('/budget/perSession', '"budget.perSession" must be a money string such as "0.20 USD"');
    for (const key of presentKeys(budgetObj)) {
      if (key !== 'perSession') bad(pointer('budget', key), `unknown budget field ${quote(key)}; ${REFUSED}`);
    }
  }

  const expiresText = get(grant, 'expiresAt');
  const expiresAt = parseTimestamp(expiresText);
  if (expiresAt === null) {
    bad('/expiresAt', '"expiresAt" must be an RFC 3339 timestamp such as "2026-10-10T13:00:00Z"');
  }

  const limits = {};
  const limitsObj = get(grant, 'limits');
  if (limitsObj !== undefined) {
    if (!isObject(limitsObj)) {
      bad('/limits', '"limits" must be an object');
    } else {
      for (const name of presentKeys(limitsObj)) {
        const path = pointer('limits', name);
        if (!KNOWN_LIMITS.includes(name)) {
          bad(path, `unknown limit ${quote(name)}; ${REFUSED}`);
          continue;
        }
        const limit = limitProblem(name, limitsObj[name]);
        if (limit !== null) bad(path, limit);
        else limits[name] = limitsObj[name];
      }
    }
  }

  let localOnly = get(grant, 'localOnly');
  if (localOnly === undefined) {
    localOnly = false;
  } else if (typeof localOnly !== 'boolean') {
    bad('/localOnly', '"localOnly" must be true or false');
    localOnly = false;
  }

  let sendsText = false;
  const thresholds = { callCostAbove: undefined, spendAbove: undefined };
  const warnIf = get(grant, 'warnIf');
  if (warnIf !== undefined) {
    if (!isObject(warnIf)) {
      bad('/warnIf', '"warnIf" must be an object');
    } else {
      for (const key of presentKeys(warnIf)) {
        const path = pointer('warnIf', key);
        const value = warnIf[key];
        if (key === 'sendsText') {
          if (typeof value === 'boolean') sendsText = value;
          else bad(path, '"warnIf.sendsText" must be true or false');
        } else if (key === 'callCostAbove' || key === 'spendAbove') {
          const money = parseMoney(value);
          if (money === null) bad(path, `"warnIf.${key}" must be a money string such as "0.05 USD"`);
          else if (budget !== null && money.currency !== budget.currency) {
            bad(path, `"warnIf.${key}" must be in the budget currency (${budget.currency})`);
          } else thresholds[key] = money;
        } else {
          bad(path, `unknown condition ${quote(key)}; ${REFUSED}`);
        }
      }
    }
  }

  for (const key of presentKeys(grant)) {
    if (!GRANT_FIELDS.includes(key)) bad(pointer(key), `unknown grant field ${quote(key)}; ${REFUSED}`);
  }

  if (problems.length > 0 || budget === null || expiresAt === null) return { value: null, problems };
  return {
    value: {
      skills: granted,
      budget,
      expiresAt,
      expiresText,
      limits,
      localOnly,
      warnSendsText: sendsText,
      warnCallCost: thresholds.callCostAbove,
      warnSpend: thresholds.spendAbove,
    },
    problems: [],
  };
}

function readCall(call) {
  if (!isObject(call)) return { value: null, problems: [['', 'the call must be a JSON object']] };
  const problems = [];
  const bad = (path, message) => problems.push([path, message]);

  const skill = get(call, 'skill');
  if (!isSkillId(skill)) bad('/skill', '"skill" must be a skill id such as "audio.transcribe"');
  const at = parseTimestamp(get(call, 'at'));
  if (at === null) bad('/at', '"at" must be an RFC 3339 timestamp such as "2026-10-10T12:00:00Z"');
  const provider = get(call, 'provider');
  if (!(typeof provider === 'string' && PROVIDER_KINDS.includes(provider))) {
    bad('/provider', `"provider" must be one of: ${PROVIDER_KINDS.join(', ')}`);
  }
  const cost = parseMoney(get(call, 'estimatedCost'));
  if (cost === null) {
    bad('/estimatedCost', '"estimatedCost" must be a money string such as "0.003 USD" ("0 USD" for a local model)');
  }
  let clipSeconds;
  let textChars;
  const measured = get(call, 'input');
  if (measured !== undefined) {
    if (!isObject(measured)) {
      bad('/input', '"input" must be an object');
    } else {
      clipSeconds = get(measured, 'clipSeconds');
      if (clipSeconds !== undefined && !isNonNegative(clipSeconds)) {
        bad('/input/clipSeconds', '"input.clipSeconds" must be a number from 0 to 9007199254740991');
      }
      textChars = get(measured, 'textChars');
      if (textChars !== undefined && !isCount(textChars)) {
        bad('/input/textChars', '"input.textChars" must be a whole number from 0 to 9007199254740991');
      }
    }
  }
  const params = get(call, 'params');
  if (params !== undefined && !isObject(params)) bad('/params', '"params" must be an object');

  if (problems.length > 0 || at === null || cost === null) return { value: null, problems };
  return { value: { skill, at, provider, cost, clipSeconds, textChars }, problems: [] };
}

function readState(state) {
  if (state === null || state === undefined) return { value: { spent: undefined, callTimes: [] }, problems: [] };
  if (!isObject(state)) return { value: null, problems: [['', 'the state must be a JSON object']] };
  const problems = [];

  let spent;
  const rawSpent = get(state, 'spent');
  if (rawSpent !== undefined) {
    spent = parseMoney(rawSpent);
    if (spent === null) problems.push(['/spent', '"spent" must be a money string such as "0.04 USD"']);
  }

  const callTimes = [];
  const rawTimes = get(state, 'callTimes');
  if (rawTimes !== undefined) {
    if (!isArray(rawTimes)) {
      problems.push(['/callTimes', '"callTimes" must be an array of RFC 3339 timestamps']);
    } else {
      rawTimes.forEach((value, index) => {
        const instant = parseTimestamp(value);
        if (instant === null) problems.push([`/callTimes/${index}`, `${quote(value)} is not an RFC 3339 timestamp`]);
        else callTimes.push(instant);
      });
    }
  }

  for (const key of presentKeys(state)) {
    if (!STATE_FIELDS.includes(key)) {
      problems.push([
        pointer(key),
        `unknown state field ${quote(key)}; refused so that a misspelt field cannot reset the spend`,
      ]);
    }
  }

  if (problems.length > 0) return { value: null, problems };
  return { value: { spent, callTimes }, problems: [] };
}

// ---------------------------------------------------------------------------
// Results

function makeReason(code, decision, message, path) {
  /** @type {Reason} */
  const reason = { code, decision, message };
  if (path !== undefined) reason.path = path;
  return reason;
}

function makeResult(reasons) {
  let decision = 'allowed';
  for (const reason of reasons) {
    if (severity(reason.decision) > severity(decision)) decision = reason.decision;
  }
  return { decision, reasons };
}

function escalate(result, reason) {
  let decision = result.decision;
  if (severity(reason.decision) > severity(decision)) decision = reason.decision;
  return { decision, reasons: [...result.reasons, reason] };
}

function judgeError(result, message) {
  return escalate(result, makeReason('purpose-judge-error', 'ask', message));
}

function isOpinion(value) {
  if (!isObject(value)) return false;
  const decision = get(value, 'decision');
  if (!(typeof decision === 'string' && DECISIONS.includes(decision))) return false;
  const code = get(value, 'code');
  if (code !== undefined && !(typeof code === 'string' && code.length > 0)) return false;
  const message = get(value, 'message');
  return message === undefined || typeof message === 'string';
}
