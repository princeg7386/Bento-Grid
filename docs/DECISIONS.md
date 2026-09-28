# Decisions

Non-obvious calls made while building CycleGuard, and why. The four architectural ones are
written up as ADRs in the README; this file is the longer tail.

## Environment

### The build started with no .NET SDK and no Node
The machine had neither `dotnet` nor `node` on `PATH`. `~/dotnet-install.sh` had been
downloaded but never run, `~/.dotnet` was empty, and `~/.nvm` was a bare checkout with zero
Node versions installed. Both were installed user-locally (`~/.dotnet` via the existing
install script, Node 24 LTS via the existing nvm checkout). Nothing was installed system-wide
and nothing needed a password. `run.sh` adds both locations to `PATH` so it works without the
user's shell profile being reloaded.

Two stale `dotnet-install.sh` processes were already running against the same `~/.dotnet`
target and were competing for bandwidth (about 20 KB/s combined, versus 320 KB/s on a single
connection). They were killed and replaced with one download, because two concurrent
extractions into one directory would have produced a corrupt SDK.

### The repo in the working directory was not the target repo
The session opened in an unrelated project. `~/Bento-Grid` already existed as an empty clone
with the correct `origin`, so the build happened there. The alternative — `git remote add` on
the unrelated repo, as a literal reading of the brief allowed — would have pushed someone
else's project into this repository.

## Storage and time

### Instants are stored as UTC ticks in `long` columns, not as `DateTime`
The brief allows either. Ticks were chosen because the atomic claim is raw SQL and compares
timestamps in the `WHERE` clause. With `DateTime`, EF Core writes SQLite `TEXT` in a specific
ISO-8601 layout, and any hand-written SQL has to reproduce that layout exactly or the
comparison silently does string ordering on mismatched formats. An integer comparison has no
such failure mode. `DateTimeOffset` is never stored anywhere.

`TimeProvider.GetUtcNow()` returns `DateTimeOffset`, which is unavoidable — it is the API. The
conversion to UTC `DateTime` or ticks happens immediately, in
[`Clock`](../src/CycleGuard.Api/Domain/Clock.cs), and nothing downstream sees a
`DateTimeOffset`.

### Enums are stored as text
`State`, `Type`, `FailureClass` and `Outcome` persist as strings. This is slightly larger on
disk and slightly slower to compare than integers, and it is worth it: the atomic claim reads
`WHERE State IN ('Queued', 'RetryScheduled')` instead of `WHERE State IN (0, 2)`, which means
the most safety-critical statement in the codebase is legible, and so is the database when you
open it with `sqlite3`.

### `synchronous = NORMAL` alongside WAL
Set in the same per-connection interceptor as `busy_timeout`. With WAL, `NORMAL` risks losing
the last transactions only on an OS or hardware crash, not on a process crash, and it is a
large throughput win with eight writers. For synthetic demo data this trade is free. A real
payment system would reconsider.

### Time scale compresses waits, not stored deadlines
Deadlines are written as real instants, computed at seed time from the compressed window. The
time scale divides backoff waits and multiplies displayed countdowns. Both sides of every risk
comparison stay in real time, so the scaling cancels and no arithmetic depends on the current
scale. The consequence, called out in the UI: changing the scale mid-run does not retroactively
move deadlines that were already written.

## Queue semantics

### A lease expiry consumes an attempt
When a worker dies and the reaper hands its job back, `Attempts` stays incremented. Refunding
the attempt was considered and rejected: a job that reliably kills its worker would then retry
forever. The cost is that a crash can eat one of a job's retries, which is visible in the
attempt timeline as a `LeaseExpired` outcome rather than hidden.

### `ClaimedBy` is not cleared on reclaim
The reaper releases a job by nulling `LeaseExpiresTicks`, and leaves `ClaimedBy` as the last
worker that held it. SQLite's `RETURNING` gives back post-update values, so clearing the column
would have destroyed the one piece of information the audit event needs — the name of the
worker that died. Read `ClaimedBy` as "last claimed by"; `LeaseExpiresTicks != null` is what
means "currently held".

### Requeue grants exactly one more attempt
`MaxAttempts = Attempts + 1`. Without this, a job that dead-lettered on exhausted retries would
be instantly dead-lettered again by the same check. One attempt matches what a requeue means in
practice: a human fixed something and wants one more try.

### Job idempotency key and downstream idempotency key are separate columns
`IdempotencyKey` is unique per job, as the brief requires. `DownstreamIdempotencyKey` is what
the effect ledger enforces, and defaults to the same value. They are separate so the simulator
can point two genuinely distinct jobs at one downstream key and show a real duplicate being
refused — which is impossible if the only key available is unique per job.

### The effect ledger covers every endpoint, not only payments
The `UNIQUE` ledger records an effect for all three job types, and the "duplicate disbursements
prevented" counter filters to the ACH gateway. A duplicate encounter submission is also worth
refusing, and one mechanism is easier to reason about than two.

### Completion is conditional on still holding the lease
`UPDATE ... WHERE Id = @id AND State = 'Running' AND ClaimedBy = @worker`. If the reaper took
the job while an attempt was in flight, the update matches zero rows and the transaction rolls
back rather than resurrecting a job another worker now owns.

## PHI handling

### Raw error text is held in memory, never persisted
The rule is that nothing unmasked reaches the database, and the demo still has to show raw
versus masked side by side. Both are satisfied by
[`RawErrorVault`](../src/CycleGuard.Api/Downstream/RawErrorVault.cs): a bounded (2,000 entry)
in-process store keyed by attempt id, gated behind `Demo:ExposeRawErrors`. The database only
ever receives masked text. Restarting the API loses the raw side of the comparison, which is
the correct trade and is stated in the README's limitations.

### Bare ISO dates are not masked; labelled ones are
`DOB: 1974-03-02` is masked. A bare `2026-03-14T02:41:09Z` is not, because in this codebase
bare ISO-8601 is overwhelmingly a log timestamp, and masking those would make errors
undiagnosable. US-format dates (`03/14/2026`) are always masked. Tested both ways.

### Phone patterns require a separator
`602-555-0134` and `(602) 555-0134` are masked; a bare run of ten or more digits is not,
because claim and batch identifiers are long digit strings and masking them would destroy the
diagnostic value of the message. Tested with `8005551234567`.

### Synthetic PHI is drawn from reserved ranges
Fake SSNs use the 900 area, which the SSA has never issued. Phones use the 555 exchange.
Emails use `example.org`. The values have to *look* like PHI for the masking demo to mean
anything, and must never be usable.

## API and UI

### Swagger UI over the built-in OpenAPI document
.NET 10 ships `Microsoft.AspNetCore.OpenApi` for document generation, so the only extra
dependency is `Swashbuckle.AspNetCore.SwaggerUI` for the UI itself, pointed at
`/openapi/v1.json`. Fewer moving parts than the full Swashbuckle generator.

### The dashboard polls; there is no push
Two-second polling, as specified. At a few hundred rows a human reading a countdown cannot
tell the difference, and countdowns are interpolated locally between polls so the clock moves
smoothly rather than stepping every two seconds.

### `POST /api/demo/kill-worker` exists in addition to the seeded orphan
The seeded scenario plants one payment job with an already-lapsed lease, which is
deterministic and always demonstrable. The endpoint expires the lease on a job that is running
*right now*, which is the more convincing live demo. Both paths exercise the same reaper.

### Risk needs a glyph as well as a colour
Every risk level renders an icon and a word (`▲ BREACHED`, `✋ NEEDS HUMAN`, `◆ AT RISK`), never
colour alone, so the dashboard survives being read by someone with colour vision deficiency or
projected through a bad HDMI cable at 8:45am.

### The cycle rail
The one memorable visual: a single track from now to cycle close with every job needing
attention pinned at the point it runs out of time. Breaches pile against the left edge. It
makes "one outage explains twenty of these" visible as a cluster rather than as twenty rows.
