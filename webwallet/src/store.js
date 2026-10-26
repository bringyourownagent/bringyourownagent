// What the web wallet keeps in the browser: the visitor's OpenRouter key and, per site, what he allowed. All of it is in the storage of this
// page's own origin, which is why that origin serves nothing else (see README.md). The key is read at the moment a call needs it and is
// never copied anywhere else. `storage` is localStorage in the page and a plain object with the same three methods in tests.

const NS = 'byoa-web:';
const KEY = `${NS}key`;
const SITE = `${NS}site:`;
const MAX_KEY = 400;

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

export function createStore(storage = globalThis.localStorage) {
  const read = (name) => { try { return storage.getItem(name); } catch { return null; } };
  const write = (name, value) => { try { storage.setItem(name, value); return true; } catch { return false; } };
  return {
    /** The key, or null. */
    getKey() {
      const key = read(KEY);
      return typeof key === 'string' && key.length > 0 && key.length <= MAX_KEY ? key : null;
    },
    setKey(key) {
      if (typeof key !== 'string' || key.length === 0 || key.length > MAX_KEY) return false;
      return write(KEY, key);
    },
    /** Signing out: the key leaves this browser (revoking it at OpenRouter ends it everywhere). */
    clearKey() { try { storage.removeItem(KEY); } catch { /* nothing to do */ } },
    /** What the visitor allowed this site last time: `{ models: string[], showNames: boolean, budgetUsd: number, allowedAt: string }`, or null. */
    getSite(origin) {
      const raw = read(SITE + origin);
      if (raw === null) return null;
      try {
        const v = JSON.parse(raw);
        if (!isObject(v) || !Array.isArray(v.models) || !v.models.every((m) => typeof m === 'string')) return null;
        return { models: v.models.slice(0, 16), showNames: v.showNames !== false, budgetUsd: Number.isFinite(v.budgetUsd) && v.budgetUsd >= 0 ? v.budgetUsd : 0, allowedAt: typeof v.allowedAt === 'string' ? v.allowedAt : '' };
      } catch {
        return null;
      }
    },
    setSite(origin, record) {
      return write(SITE + origin, JSON.stringify({ models: record.models, showNames: record.showNames, budgetUsd: record.budgetUsd, allowedAt: record.allowedAt }));
    },
    forgetSite(origin) { try { storage.removeItem(SITE + origin); } catch { /* nothing to do */ } },
  };
}
