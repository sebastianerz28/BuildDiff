# BuildDiff

[![ci](https://github.com/sebastianerz28/BuildDiff/actions/workflows/ci.yml/badge.svg)](https://github.com/sebastianerz28/BuildDiff/actions/workflows/ci.yml)

**Why does it build here but not there?**

BuildDiff is a cross-platform CLI that captures build-relevant environment state on two machines and tells you — ranked by severity — which differences are actually likely to break your build. Run `capture` on the machine that works and on the one that doesn't, then `compare` the two snapshots. It ends the "works on my machine" argument with evidence.

Runs on **Windows, macOS, and Linux** and understands the toolchains of every modern stack, not just one. No accounts, no server, no telemetry — snapshots are local JSON files you can read yourself.

## Install

Prebuilt packages (npm and NuGet) are not published yet. For now, build from source — you need the [.NET SDK](https://dotnet.microsoft.com/download):

```bash
git clone https://github.com/sebastianerz28/BuildDiff.git
cd BuildDiff
dotnet build
dotnet run -- providers
```

## Quickstart

```bash
# On both machines
builddiff capture                       # writes builddiff-<machine>.json

# Then, with both snapshots in one place
builddiff compare desk-pc.json old-laptop.json
```

See everything BuildDiff can detect on the current machine:

```bash
builddiff providers
```

Compare against what a repo *declares* it needs — reads `.nvmrc`, `package.json` engines, `go.mod`, `Package.swift`, `CMakeLists.txt`, `global.json`, and more:

```bash
builddiff capture --project .
```

## Example

```
BUILD DIFF: desk-pc vs old-laptop

  CRITICAL  .NET SDK — 10.0.100 present on desk-pc, MISSING on old-laptop
  CRITICAL  MSVC toolset — MSVC 14.50.35717 present on desk-pc, MISSING on old-laptop
  CRITICAL  Node — node version differs: desk-pc=25.8.2, old-laptop=20.11.0
            → Different Node MAJOR versions frequently break native addons, lockfile resolution and engines constraints.
  HIGH      Node — node ABI (NODE_MODULE_VERSION) differs: desk-pc=141, old-laptop=115
            → Prebuilt native addons compiled for a different ABI fail to load.
  HIGH      Python — Python version differs: desk-pc=3.14.3, old-laptop=3.11.9
  MEDIUM    Node package manager — yarn 1.22.22 on desk-pc, absent on old-laptop

  Most likely cause: .net sdk + msvc toolset + node.
```

The "Most likely cause" line at the bottom is the whole point. Anyone can diff two JSON files; the value here is the ranking, across every ecosystem at once.

## What it detects

BuildDiff is built around **providers** — one self-contained module per ecosystem. Run `builddiff providers` to see which apply to the current OS.

| Provider | Detects |
| -------- | ------- |
| `visual-studio` *(win)* | VS installs, MSVC toolsets, Windows SDKs, MSBuild |
| `dotnet` | .NET SDKs & runtimes, CLI version |
| `nuget` | NuGet feeds, config, global package cache |
| `native-runtime` *(win)* | VC++ runtime DLLs (load-time linking) |
| `javascript-node` | node (version/arch/ABI), npm/pnpm/yarn/bun, corepack, version managers, installed node versions |
| `swift-apple` *(macOS)* | Swift, Xcode, Command Line Tools, SDKs, simulators, CocoaPods |
| `cmake-cpp` | CMake, ninja/make, gcc/clang/cl, vcpkg/conan, ccache |
| `go` | Go toolchain + `go env` (GOROOT/GOPATH/GOARCH/CGO/toolchain) |
| `rust` | rustc/cargo, active + installed toolchains, targets, host triple |
| `jvm` | JDK(s) + vendor, Gradle, Maven, Kotlin |
| `ruby` | Ruby, RubyGems, Bundler |
| `php` | PHP, loaded extensions, Composer |
| `python` | interpreters reachable on PATH + installed packages |
| `swig` | SWIG (native binding generator) |
| `container-docker` | Docker client/server, Buildx, Compose, Podman |
| `android-sdk` | Android SDK root, build-tools, platforms, platform-tools/adb, NDK, cmdline-tools, license acceptance |
| `infra-iac` | Terraform/OpenTofu (state-version aware), Pulumi, kubectl/helm/aws/gcloud/az |
| `bazel` | Bazel/Bazelisk versions, `.bazelversion` pin, bzlmod-vs-WORKSPACE resolution |
| `environment` | PATH + build-relevant environment variables (secrets redacted) |
| `generic-toolchain` | any other build tool on PATH — deno, sbt, ghc, dart/flutter, meson, protoc, … |

Adding a new ecosystem is one provider file plus one line in the registry.

## Severity model

Each provider encodes real domain knowledge about what breaks builds:

| Severity | Examples |
| -------- | -------- |
| CRITICAL | toolchain missing on one side (compiler, SDK, runtime); Node/Java **major** version mismatch; missing NuGet feed |
| HIGH     | minor toolchain version mismatch; arch / ABI mismatch; missing native runtime DLL; missing PHP extension |
| MEDIUM   | package-manager / env-var differences; component installed on one side only |
| LOW      | OS version string; patch-level drift (hidden unless `--verbose`) |

## Commands

```
builddiff capture  [-o file.json] [--project DIR] [--only id,id]
builddiff compare  A.json B.json [--verbose]
builddiff providers
```

`--only` limits capture to specific providers (e.g. `--only javascript-node,go`).

## Privacy

Snapshots stay on your machines. Nothing is uploaded anywhere. Environment variables that look like secrets are redacted at capture time, lockfiles are recorded as hashes (never contents), and you can open the JSON to verify exactly what was captured before sharing it with a teammate.

## Architecture

| File | Role |
| ---- | ---- |
| `Program.cs` | CLI dispatch + Spectre.Console rendering |
| `Capture.cs` / `Compare.cs` | orchestrators that route to providers |
| `Providers/*` | one ecosystem per file: detect + capture + diff + severity |
| `Proc.cs` | cross-platform subprocess + PATH resolution |
| `Snapshot.cs` | slim, extensible JSON model (schema v2) |
| `SnapshotUpgrader.cs` | reads v1 snapshots so old captures still compare |

Snapshots are an open map of provider payloads, so the schema never has to change to add a stack.

## Development

```bash
dotnet build              # build the CLI + tests
dotnet test               # run the unit suite (upgrader, diff rules, ranking, redaction, project scan)
dotnet run -- providers   # run from source
```

CI (`.github/workflows/ci.yml`) builds, tests, and **runs the tool** on Windows, macOS and Linux on every push/PR — so the cross-platform providers are exercised at runtime, not just compiled.

## Status

v0.3 — cross-platform, multi-environment. Old v1 snapshots upgrade transparently on `compare`. Issues and PRs welcome — especially reports of environment differences BuildDiff missed, since each one usually becomes a provider improvement.

## License

[MIT](LICENSE)
