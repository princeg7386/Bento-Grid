# How to demo CycleGuard

Two ways to demo it: **click through the dashboard** (better for an audience) or **drive it
with curl** (better for proving it works, or over a call with no screen share). Both use the
same seeded scenario, so the numbers match either way.

## 0. Start it

```bash
cd Bento-Grid
./run.sh
```

Wait for both lines:

```
==> API      http://localhost:5179  (Swagger at /swagger)
==> Dashboard http://localhost:5173
```

Open **http://localhost:5173**.

---

## Option A: click-through demo (2-3 minutes)

1. **Click "Simulate last night"** (right-hand panel). This seeds 300 synthetic jobs in one
   shot -- claims, encounter submissions, payment runs -- as if an overnight batch cycle just
   finished. Same 300 jobs every time; the seed is fixed.

2. **Look at the top banner.** Say out loud what it says:
   *"300 jobs ran overnight before the payment cycle closes in [X]. [N] need attention, and
   $[amount] is at risk."* That sentence is the entire pitch: a normal dashboard tells you what
   already failed; this one tells you what is about to cost money.

3. **Point at the list below the banner.** It is sorted by *time to breach*, not by *when it
   failed*. A $95,000 payment 40 minutes from its deadline sits above a $4 encounter submission
   that failed 30 seconds ago.

4. **Point at the root-cause cards.** One reads `state-b-mmis · endpoint outage`, covering ~40
   jobs, tagged **↻ HEALING**. Say: *"One endpoint is down. Forty jobs are stuck behind it. It
   is recovering on its own -- watch the count drop over the next 20-30 seconds."* Click the
   card; the list below filters to just those jobs.

5. **Open a dead-lettered job** (scroll to "Dead letters by cause", click any row). In the
   drawer that opens:
   - Point at **"Plain English"** and **"Suggested action"** -- a human-readable cause, not a
     stack trace.
   - Click **"show raw"** next to the error text. Say: *"The database only ever stores the
     masked version on the left. This raw version -- with a fake name, DOB, SSN -- lives in
     memory only, purely so this demo can show you what got stripped out."*

6. **Requeue a payment job.** Still in the drawer, type an analyst name and a note, click
   **"Requeue this job"**. Then immediately click **"Requeue this job"** again (or open the
   same job in a second browser tab and click there too). The second attempt is refused --
   the job is no longer dead-lettered. Point at **"Duplicates prevented"** in the top banner:
   that counter is *money the system refused to pay twice*, not a UI decoration.

7. **Kill a worker.** Click **"Kill a worker mid-job"** in the simulation panel. Open the job
   it names (or wait ~30s and check the dead-letter/at-risk list). Its attempt history reads
   `attempt 1 <worker> LeaseExpired` then `attempt 2 <another worker> Succeeded` -- one payment,
   recovered automatically, no human involved.

8. **The unattended monitor.** Open a new tab to **http://localhost:5179/api/morning-report**.
   Say: *"Nobody has to open this dashboard at all. A background job checks the queue every 30
   seconds on its own and leaves one verdict here -- `Healthy`, `NeedsAttention`, or `Critical`
   -- for a human, or a pager, to read at 8:45am."*

---

## Option B: curl-only demo (works over a call, no screen needed)

Run these one at a time and read the output out loud.

```bash
# 1. Seed the same 300-job scenario every time
curl -s -X POST http://localhost:5179/api/demo/scenarios/last-night | python3 -m json.tool
```

```bash
# 2. The banner numbers: total jobs, dollars at risk, cycle countdown
curl -s http://localhost:5179/api/status | python3 -m json.tool
```

```bash
# 3. The list, sorted by time-to-breach (not by failure time)
curl -s 'http://localhost:5179/api/jobs?limit=10' | python3 -m json.tool
```

```bash
# 4. Root causes: one endpoint outage explaining ~40 jobs, healing on its own
curl -s http://localhost:5179/api/groups | python3 -m json.tool
```

```bash
# 5. A permanent failure: masked error the database holds
curl -s 'http://localhost:5179/api/deadletters' | python3 -m json.tool
# then, using an id from that output:
curl -s http://localhost:5179/api/jobs/<id> | python3 -c "
import json,sys
d=json.load(sys.stdin)
print('MASKED:', d['attempts'][0]['errorMasked'])
print('RAW   :', d['attempts'][0]['errorRaw'])   # in-memory only, never persisted
"
```

```bash
# 6. Requeue, then requeue again -- second one is refused
curl -s -X POST http://localhost:5179/api/jobs/<id>/requeue \
  -H 'Content-Type: application/json' \
  -d '{"analyst":"Priya S","note":"Corrected the amount upstream."}'

curl -s -X POST http://localhost:5179/api/jobs/<id>/requeue \
  -H 'Content-Type: application/json' \
  -d '{"analyst":"Marcus O","note":"Trying again."}'
# -> HTTP 409, "Job is not dead-lettered, so it cannot be requeued."
```

```bash
# 7. The unattended monitor's verdict -- this is what "check status tomorrow morning" means
curl -s http://localhost:5179/api/morning-report | python3 -m json.tool
```

Expected shape of that last one:

```json
{
  "verdict": "NeedsAttention",
  "headline": "8 dead letters unresolved, 42 jobs at risk.",
  "summary": "300 jobs total, 250 succeeded, 8 dead-lettered. 0 breached, ...",
  "dollarsAtRiskCents": 19969055,
  "topIssues": [ ... up to 5 root causes, worst first ... ]
}
```

`verdict` is always one of `Idle` (nothing seeded yet), `Healthy`, `NeedsAttention`, or
`Critical` (something already missed its deadline) -- see
[docs/risk-model.md](risk-model.md) for exactly which counts drive which verdict.

---

## Reset between demos

```bash
curl -s -X POST http://localhost:5179/api/demo/reset
```

Do this before re-seeding if you are about to demo again -- calling "Simulate last night" a
second time while workers are still actively processing the first batch can log a harmless
"job vanished" message in the API console (see [docs/WHAT_BROKE.md](WHAT_BROKE.md), bug #14).
It self-heals either way, but reset first if you want a clean run.

## If something looks wrong

- **Dashboard shows "REQUEST FAILED" everywhere** -- the API isn't running. Check the terminal
  running `./run.sh` for errors, or hit `http://localhost:5179/api/health` directly.
- **Numbers look frozen** -- the dashboard polls every 2 seconds; give it a moment.
- **Outage never heals** -- if you changed `CycleGuard:Monitor` or `CycleGuard:Demo:TimeScale`
  in `appsettings.json`, put `TimeScale` back to `20`; the outage duration and the retry
  schedule are tuned together (see docs/WHAT_BROKE.md, bug #9).
