# CycleGuard, explained from zero

This assumes you have never touched .NET, C#, or Entity Framework. It walks through every
concept the codebase uses, then walks through what actually happens when a job runs, end to
end. Read top to bottom once; after that, use it as a reference.

If a word here is unfamiliar later, it is almost certainly defined in section 1 or 2.

---

## 1. The .NET concepts this project uses

| Term | What it actually is | Where in this repo |
| --- | --- | --- |
| **.NET** | The runtime/platform (like "Node.js" is to JavaScript, or "the JVM" is to Java). Version 10 here. | Everything under `src/` and `tests/` |
| **C#** | The programming language (like "JavaScript" or "Python"). Statically typed, compiled. | Every `.cs` file |
| **ASP.NET Core** | The web-server framework, part of .NET. Turns HTTP requests into C# method calls. | `Program.cs`, `Api/EndpointRoutes.cs` |
| **Minimal API** | A lightweight way to define HTTP routes: `app.MapGet("/api/status", ...)` instead of a whole class per route. | `Api/EndpointRoutes.cs` |
| **Entity Framework Core (EF Core)** | The library that maps C# classes to database tables and turns C# queries into SQL. Roughly equivalent to Prisma (Node) or SQLAlchemy (Python). | `Data/CycleGuardDbContext.cs` |
| **SQLite** | The actual database engine: one file on disk (`cycleguard.db`), no server process to run. | The `.db` file created next to the API when it starts |
| **`BackgroundService`** | A .NET base class for "a loop that runs forever in the background, started when the app starts." This is what makes CycleGuard's workers run without anyone calling an endpoint. | `Workers/WorkerPoolService.cs`, `Workers/LeaseReaperService.cs`, `Workers/HealthMonitorService.cs` |
| **Dependency Injection (DI)** | A pattern where classes declare what they need in their constructor, and the framework hands it to them. When you see `public JobQueue(TimeProvider timeProvider, ...)`, .NET is supplying `timeProvider` automatically -- nobody calls `new JobQueue(...)` by hand. | `Program.cs` wires up "when someone asks for a `JobQueue`, here's how to build one" |
| **`record`** | A C# shorthand for "an immutable data bag with fields." `public record JobSummaryDto(long Id, string Type, ...)` is like a TypeScript `interface` plus a constructor, for free. | `Api/Dtos.cs`, most of `Domain/` |
| **`async`/`await`** | C#'s way of writing non-blocking code (identical idea to JavaScript's `async`/`await`). "Wait for this database call to finish, but let other work happen on this thread meanwhile." | Nearly every method in `Queue/`, `Api/`, `Workers/` |
| **NuGet** | .NET's package manager (equivalent to npm). Package versions live in `.csproj` files, not a lockfile. | `<PackageReference>` lines in `src/CycleGuard.Api/CycleGuard.Api.csproj` |
| **xUnit** | The testing framework used here (like Jest for JavaScript, or pytest for Python). `[Fact]` = one test; `[Theory]` + `[InlineData(...)]` = one test run with many inputs. | Every file under `tests/CycleGuard.Tests/` |
| **`TimeProvider`** | An injectable clock. Production code asks `TimeProvider` for "now" instead of calling `DateTime.UtcNow` directly, so tests can fake the clock and jump it forward instantly instead of actually waiting. | `Domain/Clock.cs`; `FakeTimeProvider` in tests |
| **Swagger / OpenAPI** | Auto-generated, browsable API documentation. Visit `http://localhost:5179/swagger` and you get a live page listing every endpoint with a "try it" button. | Wired up in `Program.cs` |
| **`appsettings.json`** | The config file (like a `.env` file, but structured JSON, and version-controlled -- it holds no secrets here). | `src/CycleGuard.Api/appsettings.json` |

### Frontend, in case that's also new

| Term | What it is |
| --- | --- |
| **React** | A JavaScript library for building UI out of reusable components. |
| **TypeScript** | JavaScript with types added, checked before the code runs. |
| **Vite** | The build tool/dev server for the frontend (`npm run dev` starts it). Also proxies `/api/*` requests to the .NET backend during development, so the browser only ever talks to one address. |
| **Tailwind CSS** | A CSS framework where you style things with utility class names (`className="text-sm font-bold"`) instead of writing separate `.css` files. |

---

## 2. The problem-domain concepts (specific to this project)

| Term | Meaning |
| --- | --- |
| **Job** | One unit of overnight work: adjudicate a batch of claims, submit an encounter to a state system, or disburse a payment. Lives in the `Jobs` database table. |
| **State** | Where a job is right now: `Queued`, `Running`, `RetryScheduled`, `Succeeded`, `DeadLettered`, or `Cancelled`. A job can only move between certain states -- see `Domain/JobStateMachine.cs`. |
| **Attempt** | One try at running a job. A job that fails twice then succeeds has three rows in the `JobAttempts` table. |
| **Backoff** | The waiting time before retrying a failed job. Doubles each time, up to a cap: 30s, 60s, 120s, ... |
| **Dead-lettered** | The job gave up -- either it failed in a way retrying can never fix (a "permanent" failure), or it ran out of retries. Sits in a holding table until a person looks at it. |
| **Requeue** | An analyst manually sending a dead-lettered job back into the queue for one more try, after fixing whatever was wrong. |
| **Idempotency key** | A unique label on a job that stops it from having its real-world effect (like a payment) applied twice, even if it gets retried or requeued. |
| **Risk level** | CycleGuard's core idea: instead of asking "did this fail?", it asks "will this cost money before its deadline?" Five levels: `Breached`, `NeedsHuman`, `AtRisk`, `OnTrack`, `Done`. Full rules in [risk-model.md](risk-model.md). |
| **PHI masking** | Before any error message is saved or shown, names/SSNs/DOBs/phone numbers/emails are replaced with tags like `[NAME-REDACTED]`. |
| **The demo / simulator** | Everything is synthetic. "Simulate last night" invents 300 fake jobs with fake people, fake failures, and a fake payment deadline -- nothing here talks to a real system. |
| **Time scale** | The demo compresses a multi-hour overnight cycle into a few real minutes, so you don't have to wait 4 hours to see a deadline approach. |

---

## 3. The shape of the codebase

```
src/CycleGuard.Api/
  Domain/       Plain C# logic with no database or web dependency: what a Job is, the
                state-transition rules, the backoff math, the risk rules, PHI masking.
                This is the part you could copy into a different project unchanged.
  Data/         How the C# classes in Domain/ map onto SQLite tables (EF Core).
  Queue/        The one class (JobQueue) that actually changes a job's state in the
                database. Every "claim a job", "mark it succeeded", "requeue it" operation
                goes through here, and here alone.
  Downstream/   Fakes the outside world: a mock claims engine, two mock state systems, a
                mock ACH payment gateway. Decides "did this attempt succeed or fail" for
                the simulator.
  Workers/      The three BackgroundServices that run forever once the app starts:
                WorkerPoolService (processes jobs), LeaseReaperService (recovers jobs
                whose worker died), HealthMonitorService (the unattended monitor).
  Demo/         Builds the deterministic "last night" scenario (300 fake jobs).
  Api/          The HTTP layer: what each endpoint returns, and how raw database rows
                get turned into the JSON the dashboard reads.
  Program.cs    The entry point. Wires every class above together and starts the server.

web/            The React dashboard. Polls the API every 2 seconds and renders it.
tests/          Every test, organized roughly one file per concept above.
```

**The rule that matters most:** nothing outside `Queue/JobQueue.cs` is allowed to change a
job's state directly. Every other file either reads (`Api/`, `Workers/HealthMonitorService.cs`)
or asks `JobQueue` to make a change on its behalf. That is what keeps "two workers grabbing the
same job" or "a payment running twice" impossible rather than merely unlikely.

---

## 4. What actually happens, end to end

### When the app starts (`Program.cs`)

1. .NET reads `appsettings.json` into a `CycleGuardOptions` object.
2. It opens (or creates) `cycleguard.db` and turns on a SQLite mode called WAL (Write-Ahead
   Logging) -- this is what lets several workers write to the same file at once without
   locking each other out.
3. It starts three background loops (`WorkerPoolService`, `LeaseReaperService`,
   `HealthMonitorService`) and the HTTP server, and starts listening for requests.

### When you POST `/api/demo/scenarios/last-night`

1. `ScenarioSimulator` deletes any existing jobs, then builds 300 fake `Job` rows in memory
   using a fixed random seed (so it's the same 300 jobs every time) and inserts them all at
   once. Every job starts in the `Queued` state.

### The worker loop, continuously (`WorkerPoolService`)

Four workers, each running this loop independently and forever:

1. **Claim**: ask the database, in one SQL statement, "give me the queued job with the
   nearest deadline, and mark it `Running` while you're at it." One statement means two
   workers physically cannot both get the same job -- there's no gap in time between "check"
   and "take" for a race to slip into.
2. **Run**: hand the job to `JobExecutor`, which asks the mock downstream system "did this
   succeed?"
3. **Route the result**:
   - Succeeded -> record the money moved (with a safety check against double-payment) and
     mark the job `Succeeded`.
   - Failed, and retries remain, and the failure is the "retryable" kind -> schedule a retry
     with backoff, mark `RetryScheduled`.
   - Failed, and either retries are exhausted or the failure is the "never going to work"
     kind -> mark `DeadLettered`.
4. Go back to step 1.

### The lease reaper, once a second (`LeaseReaperService`)

Every claimed job gets a 30-second "lease" -- a promise that whoever claimed it is still
working on it. Once a second, this checks for any `Running` job whose lease expired (its
worker crashed, or was killed) and puts it back in the queue for a different worker to pick
up. The attempt history is never lost.

### The health monitor, every 30 real seconds (`HealthMonitorService`)

This is the piece built to answer "how do I know it's fine without checking myself":

1. Reads the same status/groups data the dashboard reads.
2. Runs it through a fixed set of rules (`MorningReportEvaluator`) to get one of four
   verdicts: `Idle` (nothing seeded), `Healthy`, `NeedsAttention`, or `Critical`.
3. Saves that verdict where `GET /api/morning-report` can hand it back instantly.

Nobody has to trigger this. It runs whether or not anyone is looking at the dashboard, on a
real-world clock (not the demo's compressed one), which is the actual answer to "we don't want
a manual user checking this."

### When you open the dashboard

The React app polls `/api/status`, `/api/jobs`, `/api/groups`, and `/api/deadletters` every
2 seconds and re-renders. It never computes anything itself -- every number on screen came
from the API a moment ago.

---

## 5. Running and changing things

- **Start everything**: `./run.sh` (see [DEMO_SCRIPT.md](DEMO_SCRIPT.md) for what to click).
- **Run the tests**: `dotnet test` from the repo root (needs `.NET SDK on PATH` -- see
  README "Prerequisites").
- **Change a number** (retry count, time scale, monitor interval): edit
  `src/CycleGuard.Api/appsettings.json`, restart the API.
- **Add a new API field**: add it to the relevant `record` in `Api/Dtos.cs`, then set it
  wherever that record is constructed (usually `Api/JobReadService.cs`).
- **Understand a failure**: `docs/WHAT_BROKE.md` logs every real bug hit while building this,
  in plain English, with the fix.

## 6. Where to look when something breaks

1. The API's own console output (wherever `./run.sh` or `dotnet run` is running) -- .NET
   prints warnings and errors there in real time.
2. `GET /api/morning-report` -- is the verdict what you expect?
3. `dotnet test` -- if a test fails, the assertion message says exactly what was expected
   versus what happened; that is almost always enough to find the file.
4. `docs/WHAT_BROKE.md` -- there is a good chance whatever you just hit has already happened
   once and is written down with its fix.
