// macOS: a real Chrome with the unpacked extension talks to the real packaged wallet through the real native host (node 22+, no packages).
//
//   node mac_chrome_e2e.mjs --app "<Agent Wallet.app>" --work <a new folder> [--chrome "<path of the Chrome program>"]
//
// Run it inside the desktop session of the person who owns the Mac (launchctl asuser), on a data folder and a Chrome profile of its own: the person's
// wallet, browsers and Keychain are not touched. The Chrome profile is <work>/support/Google/Chrome, and Chrome looks for native hosts in
// <profile>/NativeMessagingHosts, which is where the wallet (told by BYOA_NATIVE_HOST_ROOT) writes its manifest.
//
// Checks: the extension loads with the ID the wallet's manifest allows; its popup says "Wallet connected" through the host; the wallet is stopped, the popup's
// "Open the wallet" button makes the host start it (macOS: `open -g -a`), and the popup says connected again; the wallet is stopped for good.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import process from 'node:process';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => (v.startsWith('--') ? [...acc, [v.slice(2), all[i + 1]]] : acc), []));
const app = args.app;
const work = args.work;
const chrome = args.chrome ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
if (!app || !work) { console.error('usage: --app <Agent Wallet.app> --work <new folder> [--chrome <program>]'); process.exit(2); }
if (existsSync(work)) { console.error('the work folder exists: choose a new one (this test deletes nothing)'); process.exit(2); }

const macos = join(app, 'Contents', 'MacOS');
const extension = join(app, 'Contents', 'Resources', 'extension');
const home = join(work, 'home');
const support = join(work, 'support');
const profile = join(support, 'Google', 'Chrome');
mkdirSync(join(profile, 'NativeMessagingHosts'), { recursive: true });
mkdirSync(home, { recursive: true });
const env = { ...process.env, BYOA_WALLET_HOME: home, BYOA_NATIVE_HOST_ROOT: support, AVALONIA_TELEMETRY_OPTOUT: '1' };

const failed = [];
const check = (label, ok, extra = '') => { console.log((ok ? 'ok   ' : 'FAIL ') + label + (extra ? `  [${extra}]` : '')); if (!ok) failed.push(label); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const until = async (fn, ms, step = 250) => { const end = Date.now() + ms; while (Date.now() < end) { try { const v = await fn(); if (v) return v; } catch { /* keep trying */ } await sleep(step); } return null; };

// the extension's ID, from the key in its manifest (the same rule as install.ps1 and NativeHostManifests.ExtensionId)
const manifest = JSON.parse(readFileSync(join(extension, 'manifest.json'), 'utf8'));
const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
const extId = [...digest].map((c) => String.fromCharCode(97 + parseInt(c, 16))).join('');

const children = [];
function startWallet() {
  const p = spawn(join(macos, 'Byoa.Wallet'), ['--background'], { env, stdio: 'ignore' });
  children.push(p);
  return p;
}

// a minimal DevTools protocol client over node's WebSocket
class Cdp {
  constructor(ws) { this.ws = ws; this.n = 0; this.pending = new Map(); ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && this.pending.has(m.id)) { this.pending.get(m.id)(m); this.pending.delete(m.id); } }; }
  static async connect(url) { const ws = new WebSocket(url); await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; }); return new Cdp(ws); }
  send(method, params = {}, sessionId) { const id = ++this.n; return new Promise((res) => { this.pending.set(id, res); this.ws.send(JSON.stringify({ id, method, params, sessionId })); }); }
}

let chromeProcess;
try {
  const wallet = startWallet();
  await sleep(5000);
  check('the wallet is running', wallet.exitCode === null, String(wallet.exitCode));
  const hostManifest = join(profile, 'NativeMessagingHosts', 'org.bringyourownagent.wallet.json');
  check('the wallet wrote the host manifest into the Chrome profile of the test', await until(() => existsSync(hostManifest), 20000) !== null);
  check('and it names the extension this test loads', existsSync(hostManifest) && readFileSync(hostManifest, 'utf8').includes(`chrome-extension://${extId}/`), extId);

  chromeProcess = spawn(chrome, [
    `--user-data-dir=${profile}`, `--load-extension=${extension}`, `--disable-extensions-except=${extension}`,
    '--disable-features=DisableLoadExtensionCommandLineSwitch', '--remote-debugging-port=9334', '--headless=new',
    '--no-first-run', '--no-default-browser-check', '--password-store=basic', '--use-mock-keychain', 'about:blank',
  ], { env, stdio: 'ignore' });
  children.push(chromeProcess);
  const version = await until(async () => (await fetch('http://127.0.0.1:9334/json/version')).json(), 30000);
  check('a Chrome of its own started', !!version, version?.Browser ?? '');
  const cdp = await Cdp.connect(version.webSocketDebuggerUrl);
  const worker = await until(async () => (await cdp.send('Target.getTargets')).result.targetInfos.find((t) => t.url.startsWith(`chrome-extension://${extId}/`)), 20000);
  check('the extension is loaded (its service worker runs) with the expected ID', !!worker, worker?.url ?? 'no target of the extension');

  async function popupChip() {
    const { result } = await cdp.send('Target.createTarget', { url: `chrome-extension://${extId}/popup.html` });
    const attached = await cdp.send('Target.attachToTarget', { targetId: result.targetId, flatten: true });
    const sessionId = attached.result.sessionId;
    const text = await until(async () => {
      const r = await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("chip").textContent', returnByValue: true }, sessionId);
      const v = r.result?.result?.value;
      return v && v !== 'Checking…' ? v : null;
    }, 20000);
    return { text, targetId: result.targetId, sessionId };
  }

  const first = await popupChip();
  check('the popup, through the real native host, says the wallet is connected', first.text === 'Wallet connected', String(first.text));

  // the wallet goes away; the popup's button makes the host start it again (through `open -g -a` for an app bundle)
  wallet.kill('SIGTERM');
  await until(() => wallet.exitCode !== null, 20000);
  check('the wallet was stopped', wallet.exitCode !== null);
  const second = await popupChip();
  check('the popup now says the wallet is not running', second.text === 'Wallet not running', String(second.text));
  await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("open").click()', returnByValue: true }, second.sessionId);
  const connected = await until(async () => {
    const r = await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("chip").textContent', returnByValue: true }, second.sessionId);
    return r.result?.result?.value === 'Wallet connected' ? true : null;
  }, 40000, 500);
  check('"Open the wallet" makes the host start the wallet, and the popup says connected', !!connected);
} catch (e) {
  check('no exception', false, String(e?.stack ?? e).slice(0, 400));
} finally {
  if (chromeProcess) chromeProcess.kill('SIGTERM');
  for (const p of children) if (p.exitCode === null) p.kill('SIGTERM');
  // The wallet the host started through `open` is not our child. It is the one program with this test's own path (the path of the app copy under
  // test); stop it by its pid, taken from `pgrep -f` on that full path, and nothing else.
  const found = spawnSync('/usr/bin/pgrep', ['-f', join(macos, 'Byoa.Wallet')], { encoding: 'utf8' });
  for (const pid of found.stdout.split('\n').map((s) => s.trim()).filter(Boolean)) {
    if (Number(pid) !== process.pid) { try { process.kill(Number(pid), 'SIGTERM'); } catch { /* already gone */ } }
  }
}
console.log(failed.length ? `\n${failed.length} FAILED: ${failed.join('; ')}` : '\nALL OK');
process.exit(failed.length ? 1 : 0);
