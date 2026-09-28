# What broke

Real bugs and environment failures hit while building CycleGuard, with what changed. Kept
because the interesting part of a queue is never the happy path.

---

## 1. The machine had no .NET SDK and no Node

**Symptom.** `dotnet --version` and `node --version` both reported "command not found". The
brief assumed both worked.

**Cause.** `~/dotnet-install.sh` had been downloaded but never run and `~/.dotnet` was empty.
`~/.nvm` was a bare git checkout of nvm with zero Node versions installed. Nothing else on the
machine provided either.

**Fix.** Both installed user-locally: the SDK via the existing install script into `~/.dotnet`,
Node 24 LTS via the existing nvm checkout. `run.sh` now adds both locations to `PATH` itself, so
it works whether or not the user's shell profile has been reloaded.

## 2. Four SDK downloads racing each other into one directory

**Symptom.** After seven minutes, `~/.dotnet` was still empty and the install log was zero
bytes. The download appeared hung.

**Cause.** Two `dotnet-install.sh` runs were already in flight from before this session, plus
the two started for it, all writing into `~/.dotnet`. Measured throughput was about 20 KB/s
combined; a single connection to the same URL got 320 KB/s. They were starving each other, and
two concurrent extractions into one directory would have produced a corrupt SDK even if they had
finished.

**Fix.** Killed all four (and their orphaned `curl` children, which survived `pkill` on the
parent script), cleared the temp files, and ran one download.

## 3. A truncated tarball that reported success

**Symptom.**

```
./shared/Microsoft.NETCore.App/10.0.12/libcoreclr.dylib: truncated gzip input
Error: [/Users/user43/.dotnet/host/fxr] does not exist
```

**Cause.** The download ended at 200,303,668 of 230,753,980 bytes. `curl` exited 0 because the
command was piped through `tail`, and the pipeline's exit status is the last command's, not
`curl`'s. The partial file looked like a complete download.

**Fix.** Compared the local size against `Content-Length`, resumed with `curl -C -`, and verified
with `gzip -t` before extracting. **Lesson:** never infer download success from a piped exit code.

## 4. npm silently skipped esbuild's platform binary

**Symptom.**

```
Error: spawnSync .../node_modules/esbuild/bin/esbuild Unknown system error -88
```

errno 88 on macOS is `EBADEXEC` — bad executable.

**Cause.** `node_modules/@esbuild/` was empty: the `darwin-arm64` optional dependency never
installed, so the shim in `esbuild/bin/` had no binary to exec. The install had been competing
for bandwidth with the SDK download and left the tree half-built. As with the tarball, the
`| tail` pipeline reported exit 0.

**Fix.** Deleted `node_modules` and `package-lock.json` and reinstalled cleanly. The platform
package appeared and `esbuild --version` ran.

---

## 5. `$"""` cannot hold a literal `{`

**Symptom.** Build failure on the synthetic stack-trace builder:

```
error CS9006: The interpolated raw string literal does not start with enough '$' characters
to allow this many consecutive opening braces as content.
```

**Cause.** The context block of the fake stack trace prints JSON-ish output:

```csharp
member={{ MemberId: {member.Id}, Name: {member.Name} }}
```

In a single-`$` interpolated raw string, `{{` is ambiguous: the compiler cannot tell an escaped
brace from the start of an interpolation.

**Fix.** Switched that literal to `$$"""`, where interpolation is `{{expr}}` and a literal brace
is a single `{`:

```csharp
member={ MemberId: {{member.Id}}, Name: {{member.Name}} }
```

## 6. The PHI masker corrupted its own output on a second pass

**Symptom.** `MaskingIsIdempotent` failed:

```
Expected: "Name: [NAME-REDACTED], DOB: [DOB-REDACTED]"
Actual:   "Name: [NAME-REDACTED]], DOB: [DOB-REDACTED]"
```

One extra `]` per pass.

**Cause.** The `Name:` value pattern excludes `]` so it stops at a closing bracket:

```
\b(Name|...)\b\s*[:=]\s*(?:"[^"\n]*"|[^,;)\]}\n]+)
```

On a second pass over `Name: [NAME-REDACTED]`, it matched `Name: [NAME-REDACTED` — everything up
to but not including the bracket — and replaced it with the token again, leaving the original
`]` stranded on the end.

**First fix, which did not work.** Added a negative lookahead *after* the whitespace:

```
...\s*(?!\[[A-Z\-]*REDACTED\])(?:...)
```

Still failed identically. `\s*` is greedy but backtracks: the engine retried with `\s*` matching
zero characters, evaluated the lookahead against ` [NAME-REDACTED]` — which begins with a space,
not `[`, so the negative lookahead *passed* — and then matched the space plus the token as the
value.

**Actual fix.** Move the guard to a position that cannot be reached by backtracking, immediately
after the separator, and let it consume the whitespace itself:

```
\b(Name|...)\b\s*[:=](?!\s*\[[A-Z\-]*REDACTED\])\s*(?:...)
```

**Lesson:** a lookahead placed after a variable-width pattern is not a guard. The engine will
backtrack that pattern to whatever width makes the lookahead succeed.

## 7. A per-job-type retry override quietly beat the global one

**Symptom.** The end-to-end HTTP test expected the first backoff to be 2 seconds and got 20.

**Cause.** The test set `CycleGuard:Retry:BaseSeconds` to 2, but `appsettings.json` gives
`PaymentRunDisbursement` its own `BaseSeconds: 20`, and `RetryOptions.PolicyFor` resolves the
per-type override ahead of the global value. The test was asserting against the global curve
while the engine used the payment curve. The precedence was correct; the test was wrong.

**Fix.** The test now overrides
`CycleGuard:Retry:PerJobType:PaymentRunDisbursement:{BaseSeconds,CapSeconds,MaxRetries}`, with a
comment saying why.

## 8. A requeue test that was racing the workers

**Symptom.** `RequeueRequiresAnAnalystAndANoteOverHttp` passed on its own and failed in the full
suite: a second requeue returned `200 OK` where the test expected `409 Conflict`.

**Cause.** Not a product bug. The test borrowed one of the eight seeded **permanent** failures.
After the first requeue the job ran, failed permanently again, and re-entered the dead-letter
table — so a second requeue was *legitimately* valid. Under the CPU contention of the full
parallel suite the window widened enough for it to land. Rewriting it as three concurrent
requeues did not help either, for the same reason: the job keeps coming back, so no count over
time is stable.

**Fix.** The test now builds its own job — one transient failure with `maxAttempts: 1`, so it
dead-letters once and then *succeeds* after being requeued. After the accepted requeue the job is
Queued, Running or Succeeded, and every one of those states refuses a requeue, so the `409` is
deterministic however the race lands. It also now asserts the requeue actually worked
(`requeueCount == 1`, two attempts on record).

**Lesson:** when asserting a concurrency invariant, pick a fixture whose terminal state is
actually terminal. A self-resurrecting job has no stable count to assert.

## 9. The self-healing outage healed nothing

**Symptom.** The first end-to-end run of the demo looked wrong in a way no test had caught. The
`state-b-mmis` outage was supposed to stall about twenty encounter submissions and then let them
recover. Instead:

```
risk        type                   at stake   to deadline  state
Breached    EncounterSubmission    $  787.60           -7  DeadLettered
NeedsHuman  EncounterSubmission    $  440.44           20  DeadLettered
NeedsHuman  EncounterSubmission    $  348.74           46  DeadLettered
...
```

Every stalled job had dead-lettered with `endpoint_outage`. Demo step 3 — "one endpoint explains
these, and it is healing itself" — demonstrated the exact opposite.

**Cause.** Arithmetic, not logic. The outage was seeded at 25 simulated minutes, which at the
then-default 60× scale is 25 real seconds. An encounter submission's backoff curve (45s base,
doubling, 6 attempts) spends its entire retry budget in about 38 real seconds at that scale, and
its **last** attempt lands at t≈23s — two seconds before the outage lifts. Every job exhausted
its retries inside the outage window and dead-lettered, one attempt short.

Nothing in the test suite caught this because every test sets `TimeScale = 1` deliberately, to
keep backoff arithmetic literal. The bug lived entirely in the relationship between two
configuration values that no test compared.

**Fix.** Three changes, and one thing deliberately not changed:

- Outage shortened to **8 simulated minutes**, so it lifts while attempts remain.
- Encounter submissions given a seventh attempt (`MaxRetries` 5 → 6) for headroom.
- Default `TimeScale` lowered from **60× to 20×**. At 60× the entire story — outage, recovery,
  dead letters, duplicates — was over within about ten real seconds, far too fast for a human to
  watch. At 20× the cycle window is twelve real minutes, the outage is visible for 24 seconds,
  and the stalled jobs recover on attempt 5 with two attempts to spare.
- The *seeded* stall count stayed at 20, but the root-cause card legitimately shows about forty
  jobs, because ordinary encounter submissions also target `state-b-mmis`. The README's demo
  script was corrected rather than the data.

Verified by watching it: 42 jobs `RetryScheduled` behind the endpoint with `healing=true` at
t+5s, and 292 of 300 succeeded with only the 8 permanent failures dead-lettered by t+95s.

**Lesson:** a demo's believability can depend on two config values agreeing with each other, and
a test suite that pins one of them to a constant will never notice. The seeder now carries the
arithmetic in a comment next to the value.

## 10. `kill-worker` usually loses its own race

**Symptom.** `POST /api/demo/kill-worker` reported killing `worker-4` on job #307, and the job
then showed `attempts=1, state=Succeeded, outcome=Succeeded` — no lease expiry, no recovery.

**Cause.** Not a bug. Jobs finish in about 15 milliseconds, so between the endpoint expiring the
lease and the reaper's next pass (up to a second later), the original worker had already
committed. Its conditional completion `WHERE State = 'Running' AND ClaimedBy = @worker` still
matched, because the reaper had not run yet, so the commit was legitimate.

**Resolution.** Left as is, because the behaviour is correct — and it is the seeded orphan, not
this endpoint, that makes the recovery story deterministic. That job is planted `Running` with an
already-lapsed lease, so the reaper always finds it:

```
attempt 1  worker-3   LeaseExpired     lease_expired
attempt 2  worker-2   Succeeded
```

The README now says the endpoint only lands while the queue is genuinely busy, and points at the
seeded orphan as the reliable demonstration.

## 11. The masker ate an analyst's note

**Symptom.** Found by using the dashboard rather than by a test. A requeue was submitted through
the drawer with the note *"Member id corrected in the source record."* The audit trail recorded:

```
Requeued  Priya S  Requeued from dead-letter. Note: Member id: [MEMBER-ID-REDACTED]
```

The note was destroyed. An audit trail that cannot be read is not an audit trail.

**Cause.** The labelled-member rule accepted any token after the label:

```
\b(MemberId|Member[ _]?ID|member_id|MBR|MID|MEM)\b\s*[:=#\-]?\s*[A-Za-z0-9][A-Za-z0-9\-]{4,}
```

Both the separator and the whitespace are optional, so `Member id corrected` parsed as label
`Member id` followed by value `corrected` — nine characters, comfortably over the five-character
minimum. The rule had no way to tell an identifier from the next English word.

**Fix.** Require a digit somewhere in the value, via a lookahead that does not consume anything:

```
...\s*[:=#\-]?\s*(?=[A-Za-z0-9\-]*\d)[A-Za-z0-9][A-Za-z0-9\-]{4,}
```

Every member identifier in this domain contains digits; ordinary prose does not. All existing
cases still mask (`MemberId: MBR-4471902`, `member_id = M004471902`, `MID#88117402`), and three
regression cases now pin the prose behaviour.

**Lesson:** masking is applied to operator-authored text as well as machine-authored text, and
the two have very different shapes. Over-masking is the safer failure, but it is still a failure
— and it was only visible by driving the actual UI, not the API.

## 12. `./run.sh` killed both servers a second after starting them

**Symptom.** The single-command entry point — the first thing anyone runs — started the API and
the dashboard and then immediately tore both down:

```
==> API      http://localhost:5179  (Swagger at /swagger)
==> Dashboard http://localhost:5173
./run.sh: line 60: wait: -n: invalid option
wait: usage: wait [n]
==> Stopping CycleGuard
```

Vite came up, then spent its short life logging `ECONNREFUSED` against an API that had been
killed before it finished binding.

**Cause.** The script ended with `wait -n`, which blocks until *any* child exits. That option
arrived in bash 4.3. macOS still ships **bash 3.2.57** as `/bin/bash`, and `#!/usr/bin/env bash`
finds exactly that unless a newer bash is installed and earlier on `PATH`. The unrecognised
option made `wait` fail, `set -e` aborted the script, and the `EXIT` trap dutifully killed both
children.

The irony is that the trap worked perfectly. It was cleaning up a process group that had no
reason to be cleaned up.

**Fix.** A portable supervisor loop that polls both PIDs, which behaves the same on bash 3.2 as
on bash 5:

```bash
while true; do
  for pid in "${pids[@]}"; do
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "==> A CycleGuard process exited; shutting the other one down."
      exit 1
    fi
  done
  sleep 1
done
```

The script also now waits for `/api/health` to answer before printing "open the dashboard", so
the first thing a new user sees is the console rather than four "request failed" panels.

**Lesson:** the one script everybody runs is the one least likely to be tested, and macOS's
thirteen-year-old default bash is not a hypothetical. Worth running `bash -n` and an actual
launch before claiming a one-command setup works.

## 13. A proxied dead backend reports as a bodyless 500

**Symptom.** With the API stopped, every dashboard panel showed `▲ REQUEST FAILED · 500 Internal
Server Error` — technically true and completely unhelpful, since it points at an API that is not
running and therefore has no logs to read.

**Cause.** The browser never talks to the API directly in development; Vite proxies `/api`. A
refused upstream connection becomes a proxy-generated `500` with an empty body, so the client's
`fetch` resolves normally and the "cannot reach the API" branch — which only fires when `fetch`
itself throws — was never reached.

**Fix.** When a `5xx` arrives with no parseable `error` field, the client now says so and names
the likely cause: *"The API answered 500 with no detail. If the backend is not running, start it
with ./run.sh or on port 5179."*

## 14. A concurrent reset raced a live worker and crashed with a foreign-key error

**Symptom.** Found while manually verifying the new health monitor: seeding the demo twice in
close succession against a running API produced

```
fail: Microsoft.EntityFrameworkCore.Update[10000]
      Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'FOREIGN KEY constraint failed'.
fail: CycleGuard.Api.Workers.WorkerPoolService[0]
      Worker worker-2 hit an unhandled error; continuing.
```

**Cause.** `ScenarioSimulator.ResetAsync` deletes every `Jobs`/`JobAttempts` row to start a
fresh scenario. If a worker had already claimed a job from the *previous* scenario and was
mid-flight, `JobQueue.RecordAttemptStartAsync` tried to insert a `JobAttempt` row pointing at a
`Job` that Reset had just deleted -- a straightforward foreign-key violation. The worker's own
catch-all kept the app alive (by design, so one bad job never takes a worker out of service),
but it logged a full EF Core exception for something that isn't actually a data problem: a
demo action deleted a row a live worker was still holding.

**Fix.** `RecordAttemptStartAsync` now checks whether the job still exists before inserting,
and returns `null` instead of letting the insert throw. `JobExecutor` treats a `null` attempt
as "this job vanished mid-flight, abandon it" and logs one clean informational line instead of
an exception. Pinned by `AJobDeletedAfterClaimIsAbandonedRatherThanCrashingTheWorker`, which
deletes a claimed job's row directly (simulating the race) and asserts the executor does not
throw.

**Lesson:** "reset while something is running" is not a hypothetical in a system with a Reset
button and background workers -- an operator can trigger it by clicking twice, and the fix
belongs at the point where a row's disappearance is discovered, not by trying to prevent the
race from ever happening.
