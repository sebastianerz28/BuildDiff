#!/usr/bin/env node
'use strict';

// Thin launcher: resolve the prebuilt native (Native AOT) binary for this OS/arch —
// shipped as an optional, platform-specific dependency (the esbuild/SWC model) — and
// exec it with the user's args. No .NET runtime required; the binary is native code.

const { spawnSync } = require('node:child_process');

function resolveBinary() {
  const platform = process.platform; // 'linux' | 'darwin' | 'win32'
  const arch = process.arch;         // 'x64' | 'arm64'
  const pkg = `@builddiff/cli-${platform}-${arch}`;
  const exe = platform === 'win32' ? 'builddiff.exe' : 'builddiff';
  try {
    return require.resolve(`${pkg}/bin/${exe}`);
  } catch {
    return null;
  }
}

const bin = resolveBinary();
if (!bin) {
  console.error(
    `builddiff: no prebuilt binary for ${process.platform}-${process.arch}.\n` +
    `Supported: linux, macOS, Windows on x64 and arm64.\n` +
    `Alternatively install the .NET global tool: dotnet tool install -g BuildDiff`
  );
  process.exit(1);
}

const result = spawnSync(bin, process.argv.slice(2), { stdio: 'inherit' });
if (result.error) {
  console.error(`builddiff: failed to launch ${bin}: ${result.error.message}`);
  process.exit(1);
}
process.exit(result.status === null ? 1 : result.status);
