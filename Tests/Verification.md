# Standalone verification — 2026-10-13

- .NET Release build: passed, zero warnings/errors after successful NuGet restore.
- `Smoke.ps1`: passed for Windows x64 Debug/Release, C# module definitions,
  Source-root includes, header invalidation, no-op incremental builds, intentional
  compile failure, Clean, project regeneration, and folder filters.
- `Smoke.ps1 -Android`: passed, Android NDK 29, ARM64 Debug/Release APKs.
  Both APK signatures verified with apksigner. Each APK contains only the project
  shared library and the standard `libc++_shared.so`; no engine library.
- Visual Studio 2026 MSBuild: generated vcxproj `/t:Rebuild`, Debug x64 passed.
- Portability: copied only `BuildTool/bin` to a separate temporary directory,
  generated `PortableApp`, built Release and ran it successfully.
- Source scan: no references to SDK build integration, UI entry macros, RHI shader
  compilation, or hot-reload implementation in the standalone tool/templates.

Android installation/runtime and interactive VS F5 were not tested in this run.
Release APKs use the local Android debug signing key for tests, not a store key.
The intentional compiler-error output in Smoke.ps1 is an expected negative test.

## Targets and acceleration follow-up — 2026-10-13

- `Targets.ps1 -Android`: Windows Debug/Release EXE, LIB and DLL; Android ARM64
  archive/shared library; dependency visibility, cycles, missing modules, no-op,
  duplicate source basenames and selective compiler-emitted header dependencies passed.
- `Acceleration.ps1 -AndroidSdk ...`: passed content change with preserved timestamp,
  touch without rebuild, reverted header cache hit, clean/cache preservation,
  damaged object recovery, shared filesystem cache after deleting the test's local cache,
  optional-header addition/removal, memory admission rejection, worker authentication
  rejection and missing-worker timeout.
- Actual MSVC Debug and NDK ARM64 compilation ran in separate worker processes,
  using HMAC-authenticated filesystem jobs and isolated output staging; produced
  Windows executable ran and Android static archive was generated.
- `BuildGraphTests`: dependency ordering, parallel readiness, duplicate-output/cycle
  detection, failure propagation, cancellation, depfile escaping, sequential admission
  under a one-job memory budget and rejection below the minimum budget passed.
- Latest Windows `Smoke.ps1`: passed, including legacy projects without Target.cs.
- Visual Studio 2026 MSBuild: generated TargetDemo.vcxproj, Debug x64,
  `/m:1 /nr:false /t:Build` passed (exit 0). Removed the redundant XML namespace
  on generated ItemGroup found by this check. Parallel solution-wide MSBuild
  invocation did not complete in the sandbox and is not counted as verified.
- .NET Release build: zero warnings/errors.

### Cache retention and mixed worker queue follow-up

- `BuildGraphTests`: local 1 MB quota evicted the oldest complete generated entry;
  shared entries and unrelated files remained intact. Zero quota means unlimited;
  matching local/shared directories disable eviction. Rebuild reasons and recovery
  from an old corrupt Makefile depfile passed; invalid freshly emitted dependency
  metadata was rejected without publishing a successful action state.
- `Acceleration.ps1 -AndroidSdk ...` passed again. An NDK worker waited on the same
  queue while MSVC compiled the Windows Debug application, then handled the Android
  Release archive. The existing cache, authentication and timeout checks also passed.
- Logs: `Tests/Generated/quota-graph-isolated.log` and
  `Tests/Generated/mixed-queue-acceleration.log` (generated, not versioned).

### Executor benchmarks and corrections

- See [Benchmarks.md](Benchmarks.md): actual Box2D archive (45 C++ files) and a
  separate synthetic STL workload (256 C++ files), clean/no-op/header/cache runs.
- Long archive commands reproduced a real failure; response files fixed both
  MSVC and llvm-ar archive invocation. Original prototypes were not modified.
- Direct executable invocation, literal `%PATH%`/`&` arguments, large simultaneous
  stdout/stderr streams, cancellation, memory queue fairness, and same-timestamp
  input changes during an invocation passed `BuildGraphTests`.
- `Acceleration.ps1 -AndroidSdk ...` passed again after the executor changes,
  including actual Windows executable and Android worker-generated static archive.
- Logs: `Tests/Generated/executor-final-graph.log`, `executor-acceleration.log`.

Worker transport/shared cache were tested on ONE computer with separate processes
and shared directories. No claim of validated multi-machine/SMB operation or measured
speedup on a large project is made. Worker requires identical shared paths/toolchains;
there is no input upload, worker sandbox, machine discovery or remote linking.
Memory costs are scheduling estimates, not OS-enforced hard limits. Gradle/APK signing and interactive VS debugging were
not revalidated for this acceleration follow-up (earlier checks above describe the
preceding standalone version).

## Hardening — 2026-10-14

- Release build: zero warnings/errors. `BuildGraphTests` passed, including cache
  quota with the new dependency index, low-memory no-op chains, input/directory
  snapshot validation, recursive RC closure and Windows child-job termination.
- `Hardening.ps1`: successful MSVC warning visible; external RC header content edit
  with unchanged timestamp and external resource payload edit rebuild the resource
  without rebuilding C++; 1 MB-budget no-op succeeds. Actual Clang/NDK ARM64
  object cached in project A is restored into B with identical bytes and mapped
  `__FILE__`; rewritten depfile permits a subsequent no-op. Same local NDK install.
- `Acceleration.ps1 -AndroidSdk ...`: all previous cases passed plus deliberately
  killed Windows worker, timeout, new attempt ID, replacement worker and runnable
  resulting EXE. Two claimed requests prove the retry did not reuse staging.
  MSVC/NDK mixed queue, authentication rejection and final timeout also passed.
- `Targets.ps1 -Android` and Windows `Smoke.ps1` passed after the changes.
- New `NativeLibraries` benchmark builds actual Box2D + ImGui core/demo (50 C++
  translation units), checks 45 header consumers and 50 restored objects. See
  [Benchmarks.md](Benchmarks.md) for results; this is not a whole large engine build.
- Generated logs: `hardening-graph-verified.log`, `hardening-integration.log`,
  `hardening-acceleration.log`, `hardening-targets.log`, `hardening-smoke.log` and
  `benchmark-native-hardening.log` under `Tests/Generated`.

Remaining boundaries: only one computer available; no LAN/SMB validation. Worker
is trusted and has no security sandbox or source upload; Windows Job Object controls
process lifetime after attachment, not access rights. MSVC cache is still path-bound;
portable caching is opt-in Clang only. Memory learning is process-level and per-run,
not a hard limit or persisted model. Worker logs reach the coordinator on completion.
RC scanning is conservative, not a complete custom-resource macro preprocessor.
APK packaging/signing and interactive Visual Studio were not revalidated in this run.

## PCH and Unity — 2026-10-14

- `CompilationModes.ps1 -Android`: all four combinations on MSVC Windows and
  Clang/NDK ARM64, Debug. Windows executables produced expected results after
  source and header edits. C files and explicitly excluded C++ sources compile
  separately; no PCH macro leaks into them. PCH-only exclusions are also excluded
  from Unity. Output directories include spaces.
- No-op, one-source selective rebuild, unchanged PCH on source edit, nested PCH
  header content change with preserved timestamp, touch-only header followed by
  a consumer rebuild, intentional PCH error/recovery, and disabling both modes
  passed. PCH/generated sources are not written into the user's Source tree.
- `BuildGraphTests` adds deterministic Unity generation, unchanged generated-file
  timestamps, source removal/obsolete unit exclusion, invalid batch/exclusion
  rejection and invalid `.cpp` PCH selection checks.
- All four modes also built a real Box2D Release static library on both backends,
  twice, with no-op and source-edit checks. Results and limitations are in
  [Benchmarks.md](Benchmarks.md). The timings were collected separately from
  functional tests, with artifact caching off.
- PCH creation and PCH consumers stay local and are not artifact-cache candidates;
  their up-to-date key includes the machine name. C/excluded/non-PCH actions retain
  the ordinary cache/worker path. PCH uses compiler-emitted dependency metadata.

Logs: `Tests/Generated/compilation-modes.log`, `compilation-modes-final.log`,
`optimization-graph.log`, `optimization-acceleration.log`,
`optimizations-box-windows.log`, `optimizations-box-android.log`.
Interactive Visual Studio, APK packaging/device launch and multi-machine execution
were not part of this PCH/Unity validation. Unity can alter translation-unit semantics;
passing these fixtures does not imply every project can enable it unchanged.

## Custom build steps and runtime dependencies — 2026-10-14

- Release build of the tool: zero warnings/errors.
- `BuildSteps.ps1 -Android`: passed with real MSVC Windows Debug EXE and NDK
  Android ARM64 Debug static library, in fixture paths containing spaces.
  PowerShell generates C++/headers/runtime JSON; a second generator consumes the
  first one's output. Generated C++ participates in Unity and a generated header
  is used as PCH. A separate HeaderOnly module generates a public header into a
  token-expanded include directory. Both platforms pass cold build and no-op.
- A schema edit with preserved timestamp updates the executable result (41 → 42),
  generated PCH/code and deployed JSON. Config-only changes execute exactly one
  copy. A runtime-only generator edit executes that generator and its copy, without
  C++ rebuild. Default DLL destination is verified byte-for-byte; recursive asset
  paths, new files and corrupted destination repair are checked.
- Deleting a generated output reruns its producer. Cycles, missing inputs, output
  path escapes, duplicate products and action-state conflicts fail validation.
  A zero-exit generator omitting an output fails; an intentional script error
  cannot retain stale declared outputs. Restoring the script builds a working EXE.
- An inventory race found during testing was fixed: compile/PCH/link actions wait
  for custom producers within searched directories before taking their inventory.
  These ordering edges do not turn unused asset contents into compiler inputs.
- `BuildGraphTests`: passed, including the new search-directory ordering and
  output/state file-directory conflict assertions, plus existing cancellation,
  process lifetime, dependency, cache, memory and snapshot checks.
- `Targets.ps1 -Android` and `Hardening.ps1`: passed existing EXE/LIB/DLL and
  Android target tests, warning/RC checks, low-memory no-op and relocated Clang
  cache/depfile reuse. Generated vcxproj include settings were checked as XML/text;
  interactive Visual Studio was not exercised.

Logs: `Tests/Generated/build-steps.log`, `build-steps-graph.log`,
`build-steps-targets.log`, `build-steps-hardening.log`.
Final integration fixture: `Tests/Generated/BuildSteps e47de1339fe8484bb21dc480748e09c7`.
Custom generators are trusted local processes, not sandboxed or remotely cached.
Runtime copies stage files beside build products; they do not insert assets into
APK, remove obsolete staged files, or deploy to devices. APK/device and remote-worker
execution were not part of this validation.

## Android APK declarations — 2026-10-14

- Added `ModuleRules.AndroidAssets` and `AndroidNativeLibraries`, independently
  of `RuntimeDependencies`. Generated files are staged through the action graph.
  The APK-only inputs are connected using an init script, without rewriting the
  user's Gradle build script. Extra native libraries require ARM64 ELF64 shared
  object headers; this is not a full linker/runtime dependency validator.
- `AndroidPackaging.ps1` passed real Debug and Release APK builds with NDK
  29.0.14206865 and the installed Gradle/Android toolchain. ZIP contents include
  recursive assets, a generated payload, an existing user Gradle asset and the
  independently built `libExtra.so`. `apksigner verify` passes both configurations.
- Repeated builds skip all native/generation/staging graph actions. Gradle and
  signature validation still run. A generator input content edit with preserved
  timestamp reaches the signed APK without C++ recompilation. Removing asset
  and native-library declarations removes their APK entries while preserving
  source files and the unrelated user asset.
- Release retains byte-identical unstripped originals in Symbols, includes the
  extra library in its symbols manifest, and leaves the input .so unchanged.
- Negative cases pass: traversal, conflicting destinations, reserved application
  library name, missing input and non-ELF library. Graph tests additionally reject
  an x86-64 ELF header and a non-shared ARM64 ELF header.
- `BuildSteps.ps1 -Android` regression passed. Tool and graph-test Release builds
  have zero warnings/errors; rebuilt `BuildGraphTests` passed. Scheduling tests
  now inject a fixed available-memory value, separate from the dedicated low-memory
  tests: the old phase-ordering assertion was affected by real host RAM pressure.
- APK tests require permission to use the user's Gradle cache/debug signing key.
  The harness uses explicit process capture, closed stdin and a process timeout;
  its tiny fixture builds serially with a 256 MB reserve to leave room for Gradle.
  These are fixture settings, not changes to the tool's default memory policy.

Final fixture: `Tests/Generated/Android Packaging 6ed4b026410c451abce9b83872900053/PackageApp`.
Logs: `android-packaging-verified.log`, `android-packaging-buildsteps.log`,
`android-packaging-graph.log` under `Tests/Generated`.
No device install or runtime loading test was performed. Packaging remains ARM64,
single `:app`, no flavors/split APKs. Old layout staging directories stay under
Intermediate until clean; they are not included in the current APK. This adds assets
and native libraries, not Android res/manifest merging or a package manager.

## Roadmap round — 2026-10-17

Implemented: platform source directories and `ExcludedSources`; `CompileOptions` for targets and
modules (warnings, `/WX`/`-Werror`, C/C++ standard, exceptions, RTTI, floating point, raw arguments),
target `Definitions`/`LinkerArguments`/`StaticRuntime`; keep-going executor (`--stop-on-error`
restores fail-fast); include search keys over used headers (cache format v4); debug information in
every configuration, Development without `NDEBUG`; `TargetDependencies` with `MODULE_API` export
macros; `compile_commands.json`; per-target/configuration locks and `clean --target`; Windows SDK
pinning, toolset-matched `.vcxproj`, escaped preprocessor definitions, boolean CLI flags and
unknown-option warnings, rules assemblies in their own load context.

Verified on Linux (.NET 8.0.131), **without MSVC, Windows SDK, NDK, Gradle or Visual Studio**:

- The tool compiles with zero errors against the NuGet assemblies from `BuildTool/bin`.
- `BuildGraphTests` pass, including new cases: independent actions finish after an unrelated
  failure and every failure is reported; `stopOnFirstError` stops the build; a new `.cpp` or an
  unrelated header in an include directory leaves compile actions up to date; a header that shadows
  a used one rebuilds; removing it restores the correct object from the cache; `__has_include`
  users keep the full inventory; objects are restored from the v4 cache after a clean.
- Separate harnesses with a fake platform: platform directories, exclusion patterns, merged
  options, duplicate raw arguments, target definitions; dependency order, own modules only,
  `CORE_API` import/export/visibility values, import library and external library propagation,
  rejection of cycles/Application dependencies/duplicated root modules; argument quoting; shared
  vs exclusive locks; compile database merge and Unity member entries.

Not verified in this round: real MSVC/clang command lines (`/fp:precise`, `/RTC1`, `/MT`,
`-static-libstdc++`, `-soname`), Release PDB generation, a real DLL dependency with
`__declspec(dllimport)`, APK packaging of dependency `.so` files, Visual Studio Clean/Rebuild per
target, clangd reading the generated database, and Windows file-lock semantics (tested only with
the .NET advisory locks on Linux). Run `Smoke.ps1`, `Targets.ps1 -Android`, `Acceleration.ps1`,
`CompilationModes.ps1 -Android`, `BuildSteps.ps1 -Android` and `AndroidPackaging.ps1` before relying
on these changes. Expect one full rebuild after upgrading: the cache key format changed (v4) and
compiler arguments changed for every configuration.

## Roadmap round 2 — 2026-10-18

Implemented: sanitizers (MSVC ASan with runtime DLL copy; Android ASan/UBSan with runtime + wrap.sh)
and LTO; Android builds on Linux/macOS hosts; Android multi-ABI APKs (arm64-v8a, x86_64); Windows
ARM64; clang-cl; `.asm` (MASM/armasm64) and `.S`/`.s` sources; Gradle values from BuildConfig.json via
`finalizeDsl`; Gradle/signing skipped when inputs are unchanged; physical devices for `run --device`;
Visual Studio ARM64 and Android configurations with per-platform `ExcludedFromBuild`; `test`,
`install`, `package`; vcpkg manifest mode and Conan 2; external include directories.

Verified on Linux (.NET 8.0.131, Ubuntu clang 18, Conan 2.32), without MSVC/NDK/Gradle/Visual Studio:

- Tool builds with zero warnings; `BuildGraphTests` pass (adds Conan graph parsing).
- A generated project built through the CLI for Android on a Linux host with a test NDK layout whose
  `clang++` is the real clang 18 (shared-object links use lld with `-nostdlib` because there is no
  Android sysroot): both ABIs compiled, platform directories and `.asm` skipped, `.S` assembled with
  defines and a depfile, `-fsanitize`/`-flto=thin`/quoted definitions accepted by clang, no-op builds.
- With a stub `gradlew` and a stub `apksigner.jar`: one APK with both ABIs, init script with
  `minSdk/targetSdk/compileSdk/applicationId/versionCode/abiFilters`, Gradle skipped on no-op and run
  again after a source edit, ASan runtime + `wrap.sh` + `useLegacyPackaging` added and removed again,
  a dropped ABI removed from `jniLibs`, Release strip with stable no-op.
- `install`/`package` for Android: APK, `lib/<abi>/libLib.a`, `include/Core.h`; unknown option warning.
- vcpkg (stub executable creating an install tree) and Conan (stub returning a real Conan 2.32 graph of
  a locally created package): installed once, again after a manifest edit, `-isystem` package
  headers, `-L` directories, `-lhello`, `debug/lib/libfmt.a`, generated Android Conan profile.
- Visual Studio generation with stand-in platforms: 9 configurations, per-platform build commands and
  outputs, Android F5 = `run --no-build --wait`, `ExcludedFromBuild` for other-platform files,
  well-formed `.vcxproj`/`.filters`/`.sln`.

Not verified: MSVC (`/fsanitize=address`, ARM64 toolchain, clang-cl flags and depfiles, `ml64`/`armasm64`
command lines, `lib /LTCG`), real NDK linking/sysroot, real Gradle with the `finalizeDsl` init script and
AGP 8.13, real vcpkg and Conan installs for Windows/Android, `test` on Windows, Visual Studio loading
the new configurations.

## Cleanup, limitations and tests — 2026-10-18

Checked in a Linux container (.NET 8; fake NDK clang, `adb` and `gradlew`, no MSVC). Windows-only
parts (`Features.ps1`, `Smoke.ps1`, `Targets.ps1`, MSVC/clang-cl ASan, ARM64) were not run here.

- Build: 0 warnings, 0 errors after removing dead code (`DependencyCache`, `CppIncludeParser`,
  `BuildInfo`, `BuildStatistics`, `WindowsCompiler`, import-library/metadata/documentation fields),
  `System.Runtime.Loader` and the `Microsoft.CodeAnalysis` meta-package (only `CSharp` remains),
  and renaming `GereratePCH` → `GeneratePCH`.
- `BuildGraphTests`: PASS. New `ModelTests`: 8/8 PASS (platform directories/`ExcludedSources`,
  target dependencies and their errors, `MODULE_API` values, option inheritance including `Favor`,
  LTO/sanitizers, `migrate`, Visual Studio generation, target/build locks).
- Found by `ModelTests` and fixed: a `.vcxproj` listed sources of modules owned by a dependency
  target as buildable with the consumer's definitions (`dllimport`); they are now `ExcludedFromBuild`
  there and keep their settings in the owning target's project.
- Legacy project: build warns, `migrate` writes `Source/<Project>.Target.cs`, a second `migrate`
  refuses; `[Obsolete]` fields report CS0618 when rules use them.
- Android `test`: device ABI detected (x86_64), executables built with `-pie`, pushed with
  `libc++_shared.so`; `CoreTests` PASS with `TestArguments`, `FailingTests` FAIL (exit 3), command exit 1.
- Second Android application `Tool`: `Platforms/Android-Tool`, `Tool.apk`, `com.beast.tool`.
- Stale runtime files: a file removed from `RuntimeDependencies` is deleted from `Binaries`.
- Earlier harnesses (platform filtering, include keys, target dependencies, locks/compile_commands,
  Visual Studio generation): all PASS.

## Linux, macOS, C++20 modules, CMake/pkg-config — 2026-10-24

Checked in a Linux container (Ubuntu 24.04, .NET 8, Clang 18 + lld + compiler-rt, GCC 13, CMake,
pkg-config) with real compilers. Not run here: macOS (no Mac), Windows/MSVC (`Features.ps1`,
including the new MSVC module and CMake consumer checks), a real Android NDK build of modules.

- `Tests/Unix.sh`: 9/9 PASS with Clang — shared/static targets with `RUNPATH $ORIGIN` and `SONAME`,
  `Linux/` sources only (`Windows/`, `Android/`, `Mac/` skipped), GCC-style PCH and Unity, `.S`,
  C++20 modules (interface, partition interface, internal partition, implementation unit; imported
  from a dependency target), ThinLTO via lld, no-op and header/module incremental rebuilds, `run`,
  `test` (pass/fail), `install`/`package`, CMake `find_package` with version check, pkg-config,
  ASan+UBSan; then the same project with GCC 13 (PCH via `.gch`, `-fmodules-ts` mapper, `-flto=auto`).
- Fixed while testing: `gcc-ar` rejects the operation inside a response file (now on the command
  line); GCC module depfiles contain extra rules (`X.c++m`, `.PHONY`, `CXX_IMPORTS +=`, order-only),
  so the Makefile depfile reader now parses rule by rule; `compile_commands.json` is written without
  `"`/`+` escapes.
- `ModelTests`: 11/11 PASS (new: Linux/macOS source rules, module scanning and graph errors,
  CMake/pkg-config text for Windows DLLs and Android). `BuildGraphTests` and all earlier harnesses PASS;
  the fake-toolchain Android build/test/second application still PASS.

## GitHub Actions — 2026-10-24

First runs on real machines (https://github.com/Dilyob2004/Beast.BuildTool.v2/actions). All three
jobs green from commit 582af0a.

- Linux (ubuntu-24.04): build, BuildGraphTests, ModelTests, `Tests/Unix.sh --android` (Clang, GCC,
  APK with arm64-v8a + x86_64 from the real NDK) — passed on the first run.
- Windows (windows-latest): Smoke, Targets, CompilationModes, Features (MSVC DLL/LTO/run/test/
  install/package, CMake consumer, ASan, MASM, C++20 modules, clang-cl). Fixed: MSVC rejects an
  internal partition in an .ixx (C7622) — internal partitions/implementation units are now .cpp
  everywhere; clang-cl ASan in Debug uses /MD and drops _DEBUG (no debug-CRT ASan runtime).
- macOS (macos-latest, arm64): Apple clang build, rpath, .dSYM, run/test/install/package, CMake,
  sanitizers, .app bundle. Fixed: Apple clang has no named modules (clear error, `mac.cxx` for
  Homebrew LLVM); build state files moved out of .app bundles (codesign failed on them).
- Test scripts now report failures as GitHub annotations with the end of the log.

## Releases and iOS — 2026-10-25

- Release workflow (manual run): self-contained archives for win-x64, win-arm64, linux-x64,
  linux-arm64, osx-arm64, osx-x64 built; `new`/`build`/`run` smoke tests passed on Windows x64,
  Linux x64, macOS arm64 and macOS x64 (Rosetta 2). The arm64 Windows/Linux archives are built but
  not run.
- iOS Simulator on GitHub's macOS runner: Game.app (Info.plist, libEngine.dylib in Frameworks, ad-hoc
  signature, `codesign --verify`), no-op rebuild, `test` through `simctl spawn` (pass and fail),
  `run` with the program's output — passed. CI summary: macOS 9 groups (skipped: GCC, modules
  without Homebrew LLVM), Linux 10 groups, Windows all steps.
- Not verified: iOS device builds and signing (no device or certificate in CI), `devicectl` run.
