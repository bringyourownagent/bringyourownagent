// Linux: a real Chrome with the unpacked extension talks to the real packaged wallet through the real native host (node 18+, no packages).
//
//   node linux_chrome_e2e.mjs --app <folder with Byoa.Wallet, byoa-host and extension/> --work <a new folder> [--chrome <program>]
//
// Run it inside a display and a session bus of its own:
//   xvfb-run -a -s "-screen 0 1280x800x24" dbus-run-session -- node linux_chrome_e2e.mjs --app <folder> --work <new folder>
// The work folder holds the wallet's data folder, the Chrome profile and the temporary folder, so the person's wallet,
// browsers and keyring are not touched. The Chrome profile is <work>/support/.config/google-chrome, which is also the
// user-data-dir Chrome is given, so Chrome looks for native hosts in <profile>/NativeMessagingHosts, where the wallet
// (told by BYOA_NATIVE_HOST_ROOT) writes its manifest.
//
// Checks: the extension loads with the ID the wallet's manifest allows; its popup says "Wallet connected" through the host; the wallet is stopped,
// the popup's "Open the wallet" button makes the host start it, and the popup says connected again.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';
import process from 'node:process';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => (v.startsWith('--') ? [...acc, [v.slice(2), all[i + 1]]] : acc), []));
const app = args.app && resolve(args.app);
const work = args.work && resolve(args.work);
const chrome = args.chrome ?? '/usr/bin/google-chrome';
if (!app || !work) { console.error('usage: --app <folder> --work <a new folder> [--chrome <program>]'); process.exit(2); }
if (existsSync(work)) { console.error('the work folder exists: choose a new one (this test deletes nothing)'); process.exit(2); }

const home = join(work, 'home');
const support = join(work, 'support');
const profile = join(support, '.config', 'google-chrome');
const runtime = join(work, 'run');
mkdirSync(join(profile, 'NativeMessagingHosts'), { recursive: true });
mkdirSync(join(home, '.config'), { recursive: true });
mkdirSync(runtime, { recursive: true });
spawnSync('/bin/chmod', ['700', runtime]);
const env = {
  ...process.env,
  HOME: home,
  XDG_DATA_HOME: join(home, '.local', 'share'),
  XDG_CONFIG_HOME: join(home, '.config'),
  XDG_RUNTIME_DIR: runtime,
  BYOA_WALLET_HOME: join(work, 'wallet'),
  BYOA_NATIVE_HOST_ROOT: support,
  AVALONIA_TELEMETRY_OPTOUT: '1',
};

const failed = [];
const check = (label, ok, extra = '') => { console.log((ok ? 'ok   ' : 'FAIL ') + label + (extra ? `  [${extra}]` : '')); if (!ok) failed.push(label); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const until = async (fn, ms, step = 250) => { const end = Date.now() + ms; while (Date.now() < end) { try { const v = await fn(); if (v) return v; } catch { /* keep trying */ } await sleep(step); } return null; };

// the extension's ID, from the key in its manifest (the same rule as install.ps1 and NativeHostManifests.ExtensionId)
const manifest = JSON.parse(readFileSync(join(app, 'extension', 'manifest.json'), 'utf8'));
const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
const extId = [...digest].map((c) => String.fromCharCode(97 + parseInt(c, 16))).join('');

const children = [];
function startWallet() {
  const p = spawn(join(app, 'Byoa.Wallet'), ['--background'], { env, stdio: 'ignore' });
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

  // Headful (the display is Xvfb): Chrome 137+ ignores --load-extension in its branded builds; the DevTools command Extensions.loadUnpacked still
  // installs an unpacked extension when the browser is driven through a pipe and started with --enable-unsafe-extension-debugging.
  chromeProcess = spawn(chrome, [
    `--user-data-dir=${profile}`, '--remote-debugging-pipe', '--enable-unsafe-extension-debugging',
    '--no-first-run', '--no-default-browser-check', '--password-store=basic', '--disable-gpu', '--disable-dev-shm-usage',
    '--window-size=1280,800', 'about:blank',
  ], { env, stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });
  children.push(chromeProcess);
  const cdp = new Cdp(chromeProcess.stdio[3], chromeProcess.stdio[4]);
  const version = await until(async () => (await cdp.send('Browser.getVersion')).result, 30000);
  check('a Chrome of its own started', !!version, version?.product ?? '');
  const loaded = await cdp.send('Extensions.loadUnpacked', { path: join(app, 'extension') });
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

  // the wallet goes away; the popup's button makes the host start it again (the host runs Byoa.Wallet next to itself on Linux)
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
    const shown = await cdp.send('Runtime.evaluate', { expression: 'document.getElementById("chip").textContent + " | " + document.getElementById("note").textContent', returnByValue: true }, second.sessionId);
    console.log('  diag: popup shows: ' + shown.result?.result?.value);
    console.log('  diag: wallet processes of this copy: ' + (spawnSync('/usr/bin/pgrep', ['-fl', join(app, 'Byoa.Wallet')], { encoding: 'utf8' }).stdout.trim() || 'none'));
    console.log('  diag: host processes: ' + (spawnSync('/usr/bin/pgrep', ['-fl', join(app, 'byoa-host')], { encoding: 'utf8' }).stdout.trim() || 'none'));
  }
} catch (e) {
  check('no exception', false, String(e?.stack ?? e).slice(0, 400));
} finally {
  if (chromeProcess) chromeProcess.kill('SIGTERM');
  for (const p of children) if (p.exitCode === null) p.kill('SIGTERM');
  // the wallet the host started is not our child: stop the one program with this test's own path, and nothing else
  for (const tool of ['Byoa.Wallet', 'byoa-host']) {
    const found = spawnSync('/usr/bin/pgrep', ['-f', join(app, tool)], { encoding: 'utf8' });
    for (const pid of found.stdout.split('\n').map((s) => s.trim()).filter(Boolean)) {
      if (Number(pid) !== process.pid) { try { process.kill(Number(pid), 'SIGTERM'); } catch { /* already gone */ } }
    }
  }
}
console.log(failed.length ? `\n${failed.length} FAILED: ${failed.join('; ')}` : '\nALL OK');
process.exit(failed.length ? 1 : 0);