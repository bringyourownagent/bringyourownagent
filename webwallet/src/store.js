// What the web wallet keeps in the browser: the visitor's OpenRouter key and, per site, what he allowed. All of it is in the storage of this
// page's own origin, which is why that origin serves nothing else (see README.md). The key is read at the moment a call needs it and is
// never copied anywhere else.
//
// The key lives in the tab's session storage, so it is gone when the tab is closed. It goes into the browser's local storage as well only when the
// visitor ticked "Remember my key in this browser" (a shared computer should not). `local` and `session` are localStorage and sessionStorage in the
// page and a plain object with the same methods in tests.

const NS = 'byoa-web:';
const KEY = `${NS}key`;
const SITE = `${NS}site:`;
const MAX_KEY = 400;

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

export function createStore(local = globalThis.localStorage, session = globalThis.sessionStorage) {
  const read = (storage, name) => { try { return storage.getItem(name); } catch { return null; } };
  const write = (storage, name, value) => { try { storage.setItem(name, value); return true; } catch { return false; } };
  const remove = (storage, name) => { try { storage.removeItem(name); } catch { /* nothing to do */ } };
  const valid = (key) => typeof key === 'string' && key.length > 0 && key.length <= MAX_KEY;
  return {
    /** The key (this tab's, else the remembered one), or null. */
    getKey() {
      const own = read(session, KEY);
      if (valid(own)) return own;
      const kept = read(local, KEY);
      return valid(kept) ? kept : null;
    },
    /** Is the key kept in this browser beyond the tab? */
    remembers() { return valid(read(local, KEY)); },
    /**
     * Keeps the key for this tab, and in this browser too when `remember` is true. Without it an earlier remembered key is forgotten, so what the
     * visitor chose last is what holds. False when the storage would not take it.
     */
    setKey(key, { remember = false } = {}) {
      if (!valid(key)) return false;
      if (!write(session, KEY, key)) return false;
      if (remember) return write(local, KEY, key);
      remove(local, KEY);
      return true;
    },
    /** Signing out: the key leaves this browser (revoking it at OpenRouter ends it everywhere). */
    clearKey() { remove(session, KEY); remove(local, KEY); },
    /** What the visitor allowed this site last time: `{ models: string[], showNames: boolean, budgetUsd: number, allowedAt: string }`, or null. */
    getSite(origin) {
      const raw = read(local, SITE + origin);
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
      return write(local, SITE + origin, JSON.stringify({ models: record.models, showNames: record.showNames, budgetUsd: record.budgetUsd, allowedAt: record.allowedAt }));
    },
    forgetSite(origin) { remove(local, SITE + origin); },
  };
}
