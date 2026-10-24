# Command executor measurements — 2026-10-13

Measured on the local Windows host (32 logical processors), MSVC 14.51.36231,
Release, configured maximum 8 concurrent actions. The memory admission policy
remained enabled; 8 is a limit, not a promise of 8 active compiler processes.
Wall times include CLI startup, rules loading and graph execution.

## Workloads and checks

- Real Box2D sources from `Prototypes/Source/ThirdParty/box2d`: 45 translation units,
  compiled into a static library. This is a medium real workload, not the whole engine.
- Synthetic STL workload: 256 translation units including a common header, compiled
  into a static library. This tests scaling; it is not a large production application.
- No-op timings are the median of three invocations. No compile/archive actions ran.
- Editing the common header while restoring its timestamp rebuilt exactly its
  compiler-reported consumers: 45/256 respectively. The header bytes are restored
  in `finally`; original prototype files are never edited.
- After `clean`, all 45/256 objects were restored from the compile cache. Existing
  Binaries are retained by `clean`, so an unchanged archive may remain up to date.
- A clean build starts without Beast artifacts/cache, but Windows filesystem and
  compiler caches are **not** flushed. Each run uses a fresh generated project path.

## Findings and implementation

1. The first real-workload run failed during archive creation: `cmd.exe` rejected
   the long list of object paths. Both MSVC lib and Android llvm-ar now receive
   response files, which are declared build inputs. This correction was necessary
   before a complete baseline could be measured.
2. Commands now launch executable files directly, preserving literal arguments and
   draining both output streams. Cancellation kills and waits for the process tree.
3. The executor uses dependency counts and a ready queue rather than repeatedly
   scanning all remaining actions. An oversized ready action cannot hide smaller
   runnable actions behind it.
4. Repeated input hashes and time-macro checks are shared within one invocation.
   The graph re-hashes the observed input snapshot before reporting success;
   generated outputs invalidate their cached input fingerprints. Compiled outputs
   retain their own pre-publication checks. There is no persistent timestamp-only
   fingerprint cache.
5. An experimental parallel-preflight version was rejected: the synthetic header
   rebuild took 86.57 s, with substantial repeated file I/O. Compiler processes
   remain parallel; filesystem preflight is bounded.
6. Trace fields `executionMs` and `bookkeepingMs` distinguish subprocess/worker wait
   from hashing, cache I/O and validation. Summed action times overlap and are not
   interchangeable with wall time.

## Recorded runs

| Workload / run | Clean | No-op median | Header edit | Restore objects |
| --- | ---: | ---: | ---: | ---: |
| Box2D, initial baseline after response-file correction | 3.54 s | 0.75 s | 3.32 s | 2.15 s |
| Box2D, final | 5.78 s | 1.05 s | 4.86 s | 3.08 s |
| Synthetic, initial baseline after response-file correction | 31.02 s | 6.90 s | 38.39 s | 18.69 s |
| Synthetic, final | 61.09 s | 2.10 s | 63.67 s | 9.17 s |

These early-versus-late rows are not evidence of a universal speedup: host conditions
were not controlled, and full-build/startup times changed substantially. A later
control copy isolates the input-check/direct-launch changes under more comparable
conditions: it uses the final ready queue, response files and timing code, but
repeats input hashing/time-macro scanning and launches tools through `cmd.exe`.
It is **not** a byte-identical copy of the initial implementation.

| Synthetic, later comparison | Control (repeated checks + cmd) | Final |
| --- | ---: | ---: |
| Clean | 66.08 s | 61.09 s |
| No-op median | 16.00 s | 2.10 s |
| Header edit (256 consumers) | 73.98 s | 63.67 s |
| Restore objects | 28.67 s | 9.17 s |

The observed no-op improvement in this comparison is approximately 7.6x.
Clean/header times are single observations, not a statistical performance guarantee.

Final raw results are in generated (unversioned) directories:

- `Tests/Generated/Benchmark-Box2D-2ab1087f34124f14a168360462a25365/Benchmark.json`
- `Tests/Generated/Benchmark-Synthetic-38a8bccff91e48d58acfe876026aa064/Benchmark.json`
- Initial baselines: `benchmark-box-baseline.log`, `benchmark-synthetic-baseline.log`.
- Final logs: `benchmark-box-final.log`, `benchmark-synthetic-final.log`.
- Rejected parallel-preflight run: `benchmark-synthetic-after.log`.
- Later control: `benchmark-control.log`; its source copy is `ExecutorControl`.
- Control JSON: `Tests/Generated/Benchmark-Synthetic-a330bc689f8b48a997ed9859017be1d1/Benchmark.json`.

Run `Tests/Benchmark.ps1 -Dataset Box2D` or `-Dataset Synthetic -Units 256` to
reproduce the workflow. `-Jobs` selects the action limit; `-ToolPath` selects an
alternate executable. Expect timing variance; use repeated alternating runs on an
otherwise idle machine for performance claims. Windows-only timing here; Android
worker/archive functionality is covered separately by `Acceleration.ps1`.

## Hardening follow-up — 2026-10-14

`-Dataset NativeLibraries` copies real Box2D and ImGui core/demo from the local
prototype into an isolated fixture: 50 C++ translation units, 8 jobs, 32 logical
processors. No synthetic duplication. This broadens coverage but does not constitute
validation of a complete large engine or arbitrary production project.

| Stage | Wall time | Result |
| --- | ---: | --- |
| Clean | 3.865 s | 50 compiles + archive |
| No-op median of 3 | 0.439 s | 51 skipped actions |
| Header content edit, preserved timestamp | 2.025 s | 45 Box2D compiles + archive; 5 ImGui files skipped |
| No-op after edit | 0.441 s | 51 skipped actions |
| Restore after clean | 1.526 s | 50 cache hits; unchanged archive skipped |

Raw report: `Tests/Generated/Benchmark-NativeLibraries-9cfcdb540f6a42ebadcb7e09de3ad7df/Benchmark.json`.
Report includes tool hash; a later diagnostic-only preflight error-state fix is not
part of that measured binary. There is no matched pre-change run for this workload,
so these timings must not be presented as a before/after speedup. Compilation was
local MSVC Release; distributed behavior was tested separately with local workers.

## PCH / Unity comparison — 2026-10-14

Actual Box2D prototype, 45 C++ sources, Release, 8 jobs, Unity batches of 8.
Two independent copies per mode; second pass reverses mode order. No overlapping
test suites or benchmark processes were started during timing. Artifact caching
disabled for all modes. Full/edit columns are means of two wall-time observations;
no-op is the median of six runs. These are small-sample local results, not guarantees.

| Platform | Mode | Full build | One source edit | No-op |
| --- | --- | ---: | ---: | ---: |
| Windows MSVC | Plain | 2.785 s | 0.657 s | 0.403 s |
| Windows MSVC | PCH | 2.906 s | 0.592 s | 0.421 s |
| Windows MSVC | Unity | 1.696 s | 0.630 s | 0.383 s |
| Windows MSVC | PCH + Unity | 1.736 s | 0.593 s | 0.386 s |
| Android NDK ARM64 | Plain | 4.780 s | 2.050 s | 1.730 s |
| Android NDK ARM64 | PCH | 4.756 s | 2.045 s | 1.806 s |
| Android NDK ARM64 | Unity | 3.136 s | 2.073 s | 1.742 s |
| Android NDK ARM64 | PCH + Unity | 3.316 s | 2.067 s | 1.724 s |

Unity reduced full-build time in this workload by about 39% on Windows and 34% on
Android. PCH alone did not materially accelerate the full build; combining both
was not faster than Unity alone. Stable common header choice and parsing/optimization
balance matter. Neither optimization is enabled by default.

The edit appends a comment to `b2_body.cpp` while preserving its timestamp. Exactly
one source/unit or one Unity batch is compiled, with no PCH rebuild. On Android
identical object bytes can also skip archive recreation. Cold means a fresh Beast
output directory, not a flushed OS disk cache. Archive output was built, not an APK;
no Android device/emulator runtime claim is made. Distinct functional fixtures run
Windows executables and test PCH changes, failures and exclusions.

Raw reports (generated/unversioned, including measured tool SHA-256 and per-action
traces):

- `Tests/Generated/Optimizations-Box2D-Windows-e3ff91ef00104d86b6184923c802ca97/Comparison.json`
- `Tests/Generated/Optimizations-Box2D-Android-427421b3a2fc4c3c9b8e7d381dc4fbc6/Comparison.json`

The final binary additionally rejects a compiled `.cpp` named as the PCH header;
that validation-only change is not part of the measured binary. Reproduce with
`Tests/OptimizationBenchmark.ps1 -Dataset Box2D -Platform Windows -Repeats 2`
or `-Platform Android -AndroidSdk <sdk>`. Synthetic STL workload is available as
`-Dataset Synthetic -Units 128` but is not included in this table.
