import assert from 'node:assert/strict';
import { test } from 'node:test';
import { checkCode, isSessionCode, linkFromFragment, normalizeHanded, parseLink } from '../src/link.js';
import { expectedCheck, makeLink, SESSION } from './helpers.js';

const chars = (...codes) => String.fromCharCode(...codes);
const bad = (text, part, options) => {
  const r = parseLink(text, options);
  assert.equal(r.ok, false, text);
  assert.match(r.why, part);
};

test('a good link is read: endpoint, manifest, session, origin', () => {
  const r = parseLink(makeLink());
  assert.equal(r.ok, true);
  assert.equal(r.link.session, SESSION);
  assert.equal(r.link.host, 'quiz.example.org');
  assert.equal(r.link.origin, 'https://quiz.example.org');
  assert.equal(r.link.manifestJson.skills['text.generate'].required, true);
});

test('a port other than 443 is part of the origin', () => {
  assert.equal(parseLink(makeLink({ endpoint: 'wss://quiz.example.org:8443/ws' })).link.origin, 'https://quiz.example.org:8443');
  assert.equal(parseLink(makeLink({ endpoint: 'wss://quiz.example.org:443/ws' })).link.origin, 'https://quiz.example.org');
});

test('the check code is the spec\'s, for several sessions', async () => {
  for (const s of [SESSION, 'x'.repeat(22), 'a-b_c-d_e-f_g-h_i-j_k-l_m', 'Zz9'.repeat(10)]) {
    assert.equal(await checkCode(s), expectedCheck(s));
    assert.match(await checkCode(s), /^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}$/);
  }
});

test('links that are not well formed are refused with a reason', () => {
  bad('', /empty/);
  bad('https://example.org/', /not a bringyourownagent/);
  bad('bringyourownagent://other?v=0.1', /unknown kind/);
  bad('bringyourownagent://connect', /no parameters/);
  bad(`${makeLink()}#x`, /fragment/);
  bad(makeLink({ overrides: { v: null } }), /no version/);
  bad(makeLink({ overrides: { v: '9.9' } }), /unsupported/);
  bad(makeLink({ overrides: { session: 'short' } }), /session/);
  bad(makeLink({ overrides: { endpoint: null } }), /no endpoint/);
  bad(makeLink({ overrides: { manifest: null } }), /no manifest/);
  bad(makeLink({ overrides: { manifest: 'not base64 !!' } }), /base64url/);
  bad(makeLink({ overrides: { manifest: Buffer.from('[1]').toString('base64url') } }), /JSON object/);
  bad(makeLink({ overrides: { manifest: Buffer.from('nope').toString('base64url') } }), /not JSON/);
  bad(`${makeLink()}&v=0.1`, /repeats/);
  bad(`${makeLink()}${chars(0xe9)}`, /characters/);
  bad(`bringyourownagent://connect?${'a=1&'.repeat(17)}`, /too many/);
  bad(`${makeLink()}&x=${'a'.repeat(13000)}`, /too long/);
});

test('a manifest over 4096 bytes is refused', () => {
  bad(makeLink({ manifest: { byoa: '0.1-draft', pad: 'x'.repeat(4100) } }), /4096/);
});

test('endpoints: only wss, only public names', () => {
  const endpoint = (e, options) => parseLink(makeLink({ endpoint: e }), options);
  for (const e of ['ws://quiz.example.org/ws', 'https://quiz.example.org/ws', 'wss://user:pw@quiz.example.org/ws', 'wss://quiz.example.org/ws#frag',
    'wss://127.0.0.1/ws', 'wss://10.0.0.5/ws', 'wss://[::1]/ws', 'wss://localhost/ws', 'wss://printer.local/ws', 'wss://nas.home.arpa/ws',
    'wss://intranet/ws', 'wss://a.b.123/ws', 'wss://-bad.example.org/ws', 'wss://quiz.example.org./ws', `wss://${'a'.repeat(64)}.example.org/ws`]) {
    assert.equal(endpoint(e).ok, false, e);
  }
  assert.equal(endpoint('ws://localhost:8080/ws').ok, false);
  assert.equal(endpoint('ws://localhost:8080/ws', { devLocal: true }).ok, true);
  assert.equal(endpoint('ws://quiz.example.org/ws', { devLocal: true }).ok, false);
});

test('a pasted link: spaces, quotes, brackets, wrapped lines and zero-width characters go; more text does not', () => {
  const link = makeLink();
  assert.equal(normalizeHanded(`  ${link}\n`).link, link);
  assert.equal(normalizeHanded(`"${link}"`).link, link);
  assert.equal(normalizeHanded(`<${link}>`).link, link);
  assert.equal(normalizeHanded(`${chars(0x201c)}${link}${chars(0x201d)}`).link, link);
  assert.equal(normalizeHanded(`${link.slice(0, 40)}${chars(13, 10)}${link.slice(40)}`).link, link);
  assert.equal(normalizeHanded(`${link.slice(0, 40)}${chars(0x200b)}${link.slice(40)}`).link, link);
  assert.equal(normalizeHanded(`${link} ${link}`).ok, false);
  assert.equal(normalizeHanded(`${link}${link}`).ok, false);
  assert.equal(normalizeHanded(`see ${link}`).ok, false);
  assert.equal(normalizeHanded('hello').ok, false);
  assert.equal(normalizeHanded('   ').ok, false);
  assert.equal(normalizeHanded(null).ok, false);
  assert.equal(normalizeHanded('x'.repeat(30000)).ok, false);
});

test('the address bar fragment: the link as it is, or percent-encoded', () => {
  const link = makeLink();
  assert.equal(linkFromFragment(`#link=${link}`), link);
  assert.equal(linkFromFragment(`#link=${encodeURIComponent(link)}`), link);
  assert.equal(linkFromFragment('#other'), null);
  assert.equal(linkFromFragment(''), null);
  assert.equal(linkFromFragment('#link=bringyourownagent%3a%zz'), null);
});

test('a session code is 22 to 256 URL-safe characters', () => {
  assert.equal(isSessionCode('a'.repeat(22)), true);
  assert.equal(isSessionCode('a'.repeat(21)), false);
  assert.equal(isSessionCode('a'.repeat(257)), false);
  assert.equal(isSessionCode(`${'a'.repeat(21)}!`), false);
  assert.equal(isSessionCode(null), false);
});
