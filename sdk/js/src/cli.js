// The `bringyourownagent` command line tool.
//
//   bringyourownagent validate manifest.json
//   bringyourownagent check --manifest m.json --grant g.json --call c.json [--state s.json]
//
// Exit codes: 0 the manifest is valid / the call may proceed (allowed, warn);
// 1 the manifest is invalid / the call may not proceed (ask, blocked);
// 2 usage error or unreadable input.

import { readFileSync } from 'node:fs';
import { checkCall } from './gate.js';
import { validateManifest } from './manifest.js';
import { VERSION } from './version.js';

export const PROGRAM = 'bringyourownagent';

export const HELP = `usage: bringyourownagent <command> [options]

Commands:
  validate <manifest.json>
      check a manifest against the BYOA 0.1-draft rules
  check --manifest <file> --grant <file> --call <file> [--state <file>]
      decide one call: allowed, warn, ask or blocked

Options:
  --json         print the result as JSON
  -h, --help     show this help
  -V, --version  show the version

Exit codes:
  0  the manifest is valid, or the call may proceed (allowed, warn)
  1  the manifest is invalid, or the call may not proceed (ask, blocked)
  2  usage error or unreadable input

Rules (draft): https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/rules-0.1-draft.md
`;

const FILE_OPTIONS = ['--manifest', '--grant', '--call', '--state'];

class UsageError extends Error {}
class InputError extends Error {}

/**
 * Run the tool and return the exit code.
 * @param {string[]} argv arguments after the program name
 * @param {{ stdout: { write(s: string): unknown }, stderr: { write(s: string): unknown } }} [io]
 * @returns {number}
 */
export function main(argv, io = { stdout: process.stdout, stderr: process.stderr }) {
  const out = io.stdout;
  const err = io.stderr;
  try {
    const options = parse(argv);
    if (options.help) {
      out.write(HELP);
      return 0;
    }
    if (options.version) {
      out.write(`${PROGRAM} ${VERSION}\n`);
      return 0;
    }
    if (options.command === 'validate') return runValidate(options, out);
    if (options.command === 'check') return runCheck(options, out);
    throw new UsageError(`unknown command ${JSON.stringify(options.command)}`);
  } catch (e) {
    if (e instanceof UsageError) {
      err.write(`${PROGRAM}: ${e.message}\nTry '${PROGRAM} --help'.\n`);
      return 2;
    }
    if (e instanceof InputError) {
      err.write(`${PROGRAM}: ${e.message}\n`);
      return 2;
    }
    throw e;
  }
}

function parse(args) {
  const options = { help: false, version: false, json: false, files: {}, positional: [], command: null };
  let index = 0;
  while (index < args.length) {
    const arg = args[index];
    index += 1;
    const name = arg.split('=', 1)[0];
    if (arg === '-h' || arg === '--help') {
      options.help = true;
    } else if (arg === '-V' || arg === '--version') {
      options.version = true;
    } else if (arg === '--json') {
      options.json = true;
    } else if (FILE_OPTIONS.includes(name)) {
      let value;
      if (arg.includes('=')) {
        value = arg.slice(name.length + 1);
      } else {
        if (index >= args.length) throw new UsageError(`option ${name} needs a file`);
        value = args[index];
        index += 1;
      }
      if (!value) throw new UsageError(`option ${name} needs a file`);
      options.files[name.slice(2)] = value;
    } else if (arg.startsWith('-') && arg !== '-') {
      throw new UsageError(`unknown option ${arg}`);
    } else {
      options.positional.push(arg);
    }
  }
  options.command = options.positional.length > 0 ? options.positional[0] : null;
  if (options.command === null && !(options.help || options.version)) throw new UsageError('missing command');
  return options;
}

function runValidate(options, out) {
  const files = options.positional.slice(1);
  if (files.length !== 1 || Object.keys(options.files).length > 0) {
    throw new UsageError(`validate takes exactly one file: ${PROGRAM} validate manifest.json`);
  }
  const path = files[0];
  const result = validateManifest(load(path));
  if (options.json) {
    out.write(JSON.stringify(result, null, 2) + '\n');
  } else {
    const counts = [];
    if (result.errors.length) counts.push(plural(result.errors.length, 'error'));
    if (result.warnings.length) counts.push(plural(result.warnings.length, 'warning'));
    const status = result.ok ? 'valid' : 'invalid';
    out.write(`${path}: ${[status, ...counts].join(', ')}\n`);
    for (const [level, issues] of [['error', result.errors], ['warning', result.warnings]]) {
      for (const issue of issues) {
        out.write(`  ${level} ${issue.code} at ${showPath(issue.path)}: ${issue.message}\n`);
      }
    }
  }
  return result.ok ? 0 : 1;
}

function runCheck(options, out) {
  if (options.positional.length !== 1) {
    throw new UsageError(
      `check takes its files as options: ${PROGRAM} check --manifest m.json --grant g.json --call c.json`,
    );
  }
  const files = options.files;
  for (const name of ['manifest', 'grant', 'call']) {
    if (!(name in files)) throw new UsageError(`check needs --${name} <file>`);
  }
  const manifest = load(files.manifest);
  const grant = load(files.grant);
  const call = load(files.call);
  const state = 'state' in files ? load(files.state) : undefined;
  const result = checkCall(manifest, grant, call, state);
  if (options.json) {
    out.write(JSON.stringify(result, null, 2) + '\n');
  } else {
    out.write(`decision: ${result.decision}\n`);
    for (const reason of result.reasons) {
      const where = 'path' in reason ? ` at ${showPath(reason.path)}` : '';
      out.write(`  ${reason.decision} ${reason.code}${where}: ${reason.message}\n`);
    }
  }
  return result.decision === 'allowed' || result.decision === 'warn' ? 0 : 1;
}

const ERRNO_TEXT = {
  ENOENT: 'No such file or directory',
  EACCES: 'Permission denied',
  EISDIR: 'Is a directory',
};

function load(path) {
  let bytes;
  try {
    bytes = readFileSync(path);
  } catch (e) {
    throw new InputError(`cannot read ${path}: ${ERRNO_TEXT[e.code] || e.message}`);
  }
  let text;
  try {
    // fatal: refuse invalid UTF-8; a leading byte order mark is skipped.
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw new InputError(`${path} is not UTF-8 text`);
  }
  try {
    return JSON.parse(text);
  } catch (e) {
    throw new InputError(`${path} is not valid JSON: ${e.message}`);
  }
}

function showPath(path) {
  return path ? path : '(root)';
}

function plural(count, word) {
  return `${count} ${word}${count === 1 ? '' : 's'}`;
}
