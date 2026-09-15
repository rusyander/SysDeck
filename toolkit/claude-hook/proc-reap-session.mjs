#!/usr/bin/env node
// SessionEnd: reap processes this session's shells left behind.
// Detached + unref: a full CIM sweep takes seconds, session teardown must not wait.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const reap = resolve(here, '..', 'tools', 'proc-reaper', 'reap.ps1');

if (existsSync(reap)) {
  spawn(
    'powershell.exe',
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', reap, '-Force', '-Quiet'],
    { detached: true, stdio: 'ignore', windowsHide: true }
  ).unref();
}

process.exit(0);
