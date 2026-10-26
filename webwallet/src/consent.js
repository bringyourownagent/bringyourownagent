// What the consent screen shows and what it lets the visitor do: built from the link's manifest and the models he has ticked, with no
// DOM and no network, so that the rules can be tested. The rules are the native wallet's where they apply (CONTRACT.md section 3, CONTRACT-PAIR.md section 4):
// a skill this wallet cannot serve cannot be allowed if the site requires it, a manifest that handles data a cloud must not see is not served,
// and a site that shows the check code gets no Allow before the visitor ticks that the two are the same.

export const SERVED_SKILLS = Object.freeze(['text.generate']);

/** Privacy classes that cannot go to a company's cloud: this wallet's only source is one. `personal` can, with a warning. */
export const REFUSED_PRIVACY = Object.freeze(['sensitive', 'personal-voice', 'local-only']);

/** What the wallet holds a site to unless the site, or the visitor, says less. The free tier allows 20 requests a minute, and a tab is slow to wait in. */
export const DEFAULT_LIMITS = Object.freeze({
  maxTextChars: 100_000,
  maxAnswerTokens: 4_000,
  maxCallSeconds: 120,
  maxParallelCalls: 4,
  maxCallsPerMinute: 20,
});

export const CEILINGS = Object.freeze({
  maxTextChars: 400_000,
  maxAnswerTokens: 32_000,
  maxCallSeconds: 600,
  maxParallelCalls: 8,
  maxCallsPerMinute: 60,
});

/** What one connection may spend on paid models, in US dollars: the start of the field, and the most the wallet lets the visitor give. */
export const BUDGET = Object.freeze({ start: 0.25, min: 0.01, max: 5 });

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/**
 * The budget the consent screen starts with for paid models: what the visitor gave this site last time, else what the site says it expects to
 * spend in a session, else a small start. Never above the most the wallet allows.
 */
export function startBudget(manifest, remembered = 0) {
  if (Number.isFinite(remembered) && remembered > 0) return Math.min(remembered, BUDGET.max);
  const text = manifest?.expectedCost?.perSession;
  const match = typeof text === 'string' ? /^(\d+(?:\.\d{1,6})?)\s*USD$/.exec(text.trim()) : null;
  const asked = match ? Number(match[1]) : 0;
  return Math.min(asked > 0 ? Math.max(asked, BUDGET.min) : BUDGET.start, BUDGET.max);
}

/** The text of a budget box as dollars, or null when it is not an amount the wallet accepts (from 0.01 up to the most it allows). */
export function parseBudget(text) {
  const value = Number(String(text).trim().replace(',', '.'));
  return Number.isFinite(value) && value >= BUDGET.min && value <= BUDGET.max ? Math.round(value * 1e6) / 1e6 : null;
}

/** What a model costs, for the list: "free", or the dollars per million tokens in and out. */
export function priceText(model) {
  if (model.free) return 'free';
  const dollars = (n) => (n < 0.01 ? '<$0.01' : `$${n.toFixed(2)}`);
  return `${dollars(model.pricePerMillion.prompt)} in / ${dollars(model.pricePerMillion.completion)} out per million tokens`;
}

/** A model name in OpenRouter's style, compared as CONTRACT.md section 3.3 says: case, dots, underscores, a date suffix and a `:variant` do not matter. */
export function normalizeModelName(name) {
  return String(name ?? '')
    .toLowerCase()
    .replace(/:[^/:]*$/, '')
    .replace(/-?\d{4}-?\d{2}-?\d{2}$/, '')
    .replace(/[._]/g, '-');
}

/** Does a name or a mask (ending in `*`) of the manifest fit a model's id? A pattern without a vendor is compared with the model part only. */
export function nameMatches(pattern, id) {
  const mask = String(pattern).endsWith('*');
  const p = normalizeModelName(mask ? String(pattern).slice(0, -1) : pattern);
  const m = normalizeModelName(id);
  const target = p.includes('/') ? m : m.split('/').pop();
  return mask ? target.startsWith(p) : target === p;
}

/** The skill's `models` block as the manifest states it, with the defaults of CONTRACT.md section 3.3. Null when the skill asks for no set of models. */
export function modelSpec(manifest) {
  const block = manifest?.skills?.['text.generate']?.models;
  if (!isObject(block)) return null;
  const count = block.count;
  const min = isObject(count) ? count.min ?? 1 : typeof count === 'number' ? count : 1;
  const max = isObject(count) ? count.max ?? 5 : typeof count === 'number' ? count : 5;
  return {
    min: Math.max(1, Math.min(16, Number(min) || 1)),
    max: Math.max(1, Math.min(16, Number(max) || 5)),
    require: Array.isArray(block.require) ? block.require : [],
    recommend: Array.isArray(block.recommend) ? block.recommend : [],
    why: typeof block.why === 'string' ? block.why.slice(0, 160) : null,
    names: block.names === true,
  };
}

/** The entries of `require` that none of the ticked models satisfies (an entry that is an array means "any of these"). */
export function unmetRequirements(spec, pickedIds) {
  if (!spec) return [];
  return spec.require.filter((entry) => {
    const options = Array.isArray(entry) ? entry : [entry];
    return !options.some((pattern) => pickedIds.some((id) => nameMatches(pattern, id)));
  });
}

/**
 * The limits a site is held to: the wallet's, tightened by what the site declares in `purpose.limits`, never above the ceilings.
 * @returns {{ maxTextChars: number, maxAnswerTokens: number, maxCallSeconds: number, maxParallelCalls: number, maxCallsPerMinute: number }}
 */
export function effectiveLimits(manifest, base = DEFAULT_LIMITS) {
  const declared = manifest?.purpose?.limits;
  const result = {};
  for (const name of Object.keys(DEFAULT_LIMITS)) {
    let value = Math.min(base[name] ?? DEFAULT_LIMITS[name], CEILINGS[name]);
    const asked = isObject(declared) ? declared[name] : undefined;
    if (Number.isFinite(asked) && asked >= 1) value = Math.min(value, Math.floor(asked));
    result[name] = value;
  }
  return result;
}

const label = (skill) => (skill === 'text.generate' ? 'Text answers' : skill);

/**
 * The consent screen's model.
 * @param {object} input
 * @param {string} input.host         The host of the site's endpoint (shown, as the site's identity).
 * @param {object} input.manifest     The link's manifest.
 * @param {string} input.check        The check code of the link's session.
 * @param {boolean} input.hasKey      The visitor has signed in.
 * @param {string[]} input.pickedIds  The OpenRouter ids of the models he has ticked.
 * @param {boolean} [input.paid]      At least one of the ticked models costs money: a budget is then needed.
 * @param {number|null} [input.budgetUsd]  The budget in the box (null when it is not an amount).
 */
export function buildConsent({ host, manifest, check, hasKey, pickedIds, paid = false, budgetUsd = 0 }) {
  const skills = Object.entries(isObject(manifest?.skills) ? manifest.skills : {}).map(([id, entry]) => ({
    id, name: label(id), required: isObject(entry) && entry.required === true, served: SERVED_SKILLS.includes(id),
  }));
  const privacy = typeof manifest?.privacy === 'string' ? manifest.privacy : null;
  const spec = modelSpec(manifest);
  const blockers = [];
  const warnings = [];

  if (!hasKey) blockers.push('Sign in with OpenRouter first: this wallet has no key yet.');
  if (!skills.some((s) => s.served)) blockers.push('This site asks for nothing this wallet can do. The web wallet answers text only.');
  for (const s of skills.filter((x) => x.required && !x.served)) blockers.push(`This site needs ${s.id}, which the web wallet cannot do.`);
  if (privacy !== null && REFUSED_PRIVACY.includes(privacy)) {
    blockers.push(`This site handles data marked "${privacy}", which must not go to a cloud model. Use the wallet on your own computer.`);
  } else if (privacy === 'personal') {
    warnings.push('This site says its data is personal. Your answers go to a cloud model.');
  }
  if (pickedIds.length === 0) blockers.push('Tick at least one model to give this site.');
  if (paid && !(Number.isFinite(budgetUsd) && budgetUsd >= BUDGET.min && budgetUsd <= BUDGET.max)) {
    blockers.push(`Set a budget for the paid models you ticked: from ${BUDGET.min} up to ${BUDGET.max} USD.`);
  }
  if (spec) {
    if (pickedIds.length < spec.min) blockers.push(`This site works with at least ${spec.min} different models; you have ticked ${pickedIds.length}.`);
    for (const entry of unmetRequirements(spec, pickedIds)) {
      const names = (Array.isArray(entry) ? entry : [entry]).join(' or ');
      blockers.push(`This site requires ${names}, which is not among the models you ticked.`);
    }
  }

  return {
    host,
    summary: typeof manifest?.purpose?.summary === 'string' ? manifest.purpose.summary : '',
    privacy,
    skills,
    models: spec,
    check: { code: check, required: manifest?.connect?.check === true },
    blockers,
    warnings,
    paid,
    notes: [
      'Your prompts go to OpenRouter and on to the company that runs the model. Free models may keep them and use them for training.',
      ...(paid ? ['The paid models you ticked are billed to your OpenRouter account. This connection stops at the budget you set; a new connection starts a new budget.'] : []),
      'This tab is the wallet. Keep it open while the site is using it.',
    ],
    limits: effectiveLimits(manifest),
  };
}

/** May the visitor press Allow now? `confirmed` is his tick that the check code is the same as on the site's page. */
export function canAllow(model, { confirmed }) {
  return model.blockers.length === 0 && (!model.check.required || confirmed === true);
}
