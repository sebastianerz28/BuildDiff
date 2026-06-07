#!/usr/bin/env node
'use strict';

// Sets the wrapper package version and pins every optionalDependency to the same
// version, so a release publishes a self-consistent set.
//   node set-wrapper-version.js <version>

const fs = require('node:fs');
const path = require('node:path');

const version = process.argv[2];
if (!version) {
  console.error('usage: set-wrapper-version.js <version>');
  process.exit(1);
}

const pkgPath = path.join(__dirname, '..', 'package.json');
const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
pkg.version = version;
for (const dep of Object.keys(pkg.optionalDependencies || {})) {
  pkg.optionalDependencies[dep] = version;
}
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n');

console.log(`wrapper builddiff set to ${version} (+ ${Object.keys(pkg.optionalDependencies || {}).length} platform deps)`);
