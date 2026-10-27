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

// a minimal DevTools protocol client over the pipe Chrome offers (--remote-debugging-pipe: messages are JSON texts each ended with a NUL byte)
class Cdp {
  constructor(toChrome, fromChrome) {
    this.toChrome = toChrome; this.n = 0; this.pending = new Map(); let buffer = '';
    fromChrome.setEncoding('utf8');
    fromChrome.on('data', (chunk) => {
      buffer += chunk;
      for (let end = buffer.indexOf('\0'); end >= 0; end = buffer.indexOf('\0')) {
        const m = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
        if (m.id && this.pending.has(m.id)) { this.pending.get(m.id)(m); this.pending.delete(m.id); }
      }
    });
  }
  send(method, params = {}, sessionId) { const id = ++this.n; return new Promise((res) => { this.pending.set(id, res); this.toChrome.write(JSON.stringify({ id, method, params, sessionId }) + '\0'); }); }
}

let chromeProcess;
try {
  const wallet = startWallet();
  await sleep(5000);
  check('the wallet is running', wallet.exitCode === null, String(wallet.exitCode));
  const hostManifest = join(profile, 'NativeMessagingHosts', 'org.bringyourownagent.wallet.json');
  check('the wallet wrote the host manifest into the Chrome profile of the test', await until(() => existsSync(hostManifest), 20000) !== null);
  check('and it names the extension this test loads', existsSync(hostManifest) && readFileSync(hostManifest, 'utf8').includes(`chrome-extension://${extId}/`), extId);

  // Chrome 137+ ignores --load-extension in its branded builds; the DevTools command Extensions.loadUnpacked still installs an unpacked extension when
  // the browser is driven through a pipe and started with --enable-unsafe-extension-debugging.
  chromeProcess = spawn(chrome, [
    `--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', '--headless=new',
    '--no-first-run', '--no-default-browser-check', '--password-store=basic', '--use-mock-keychain', 'about:blank',
  ], { env, stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  children.push(chromeProcess);
  const cdp = new Cdp(chromeProcess.stdio[3], chromeProcess.stdio[4]);
  const version = await until(async () => (await cdp.send('Browser.getVersion')).result, 30000);
  check('a Chrome of its own started', !!version, version?.product ?? '');
  const loaded = await cdp.send('Extensions.loadUnpacked', { path: extension });
  check('the extension is loaded into it with the expected ID', loaded.result?.id === extId, loaded.result?.id ?? JSON.stringify(loaded.error ?? loaded));
  const worker = await until(async () => (await cdp.send('Target.getTargets')).result.targetInfos.find((t) => t.url.startsWith(`chrome-extension://${extId}/`)), 20000);
  check('its service worker runs', !!worker, worker?.url ?? 'no target of the extension');

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
  if (!connected) {
    // what a person would look at: is a wallet of this copy running, what does the popup say, what is in the data folder
    const mine = spawnSync('/usr/bin/pgrep', ['-fl', join(macos, 'Byoa.Wallet')], { encoding: 'utf8' }).stdout.trim();
    const shown = await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("chip").textContent + " | " + document.getElementById("note").textContent', returnByValue: true }, second.sessionId);
    console.log('  diag: wallet processes of this copy: ' + (mine || 'none'));
    console.log('  diag: popup shows: ' + shown.result?.result?.value);
    console.log('  diag: host processes: ' + (spawnSync('/usr/bin/pgrep', ['-fl', join(macos, 'byoa-host')], { encoding: 'utf8' }).stdout.trim() || 'none'));
    console.log('  diag: sockets in TMPDIR: ' + (spawnSync('/bin/sh', ['-c', 'ls -la "${TMPDIR}"byoa-* 2>&1'], { encoding: 'utf8', env }).stdout.trim()));
  }
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
