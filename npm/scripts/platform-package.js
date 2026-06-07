#!/usr/bin/env node
'use strict';

// Builds one platform-specific npm package from a freshly-compiled native binary.
// Run on each build runner in the release matrix.
//   node platform-package.js <version> <nodeOs> <nodeCpu> <binarySrcPath> <outDir>

const fs = require('node:fs');
const path = require('node:path');

const [, , version, nodeOs, nodeCpu, binSrc, outDir] = process.argv;
if (!version || !nodeOs || !nodeCpu || !binSrc || !outDir) {
  console.error('usage: platform-package.js <version> <nodeOs> <nodeCpu> <binarySrcPath> <outDir>');
  process.exit(1);
}

const exe = nodeOs === 'win32' ? 'builddiff.exe' : 'builddiff';
const pkgName = `@builddiff/cli-${nodeOs}-${nodeCpu}`;
const dir = path.join(outDir, `${nodeOs}-${nodeCpu}`);

fs.mkdirSync(path.join(dir, 'bin'), { recursive: true });
fs.copyFileSync(binSrc, path.join(dir, 'bin', exe));
if (nodeOs !== 'win32') fs.chmodSync(path.join(dir, 'bin', exe), 0o755);

const pkg = {
  name: pkgName,
  version,
  description: `BuildDiff native binary for ${nodeOs}-${nodeCpu}`,
  os: [nodeOs],
  cpu: [nodeCpu],
  files: ['bin/'],
  license: 'MIT',
  repository: { type: 'git', url: 'https://github.com/sebastianerz28/BuildDiff.git' },
};
fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify(pkg, null, 2) + '\n');

console.log(`wrote ${pkgName}@${version} -> ${dir}`);
