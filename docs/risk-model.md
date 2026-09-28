# The risk model

CycleGuard does not rank work by when it broke. It ranks work by when it will cost money.
Risk is **computed on read**, never stored: a job that was `OnTrack` ten seconds ago can be
`AtRisk` now without a single column changing, because the only thing that moved was the clock.

The implementation is [`RiskEvaluator`](../src/CycleGuard.Api/Domain/RiskEvaluator.cs) and every
rule below has a matching test in
[`RiskModelTests`](../tests/CycleGuard.Tests/RiskModelTests.cs).

## The five levels

| Level | Meaning | Who acts |
| --- | --- | --- |
| `Breached` | Past its deadline and not succeeded. The money is already exposed. | A human, now |
| `NeedsHuman` | Dead-lettered and unresolved. Retrying cannot fix it. | A human, before the deadline |
| `AtRisk` | Still inside its deadline, but the arithmetic says it may not make it. | Watch, be ready |
| `OnTrack` | Inside its deadline with real slack. | Nobody |
| `Done` | Succeeded, or cancelled by an operator. | Nobody |

`Breached`, `NeedsHuman` and `AtRisk` are the three that count toward **dollars at risk** and
the **needs attention** tally in the banner.

## Evaluation order

The rules are checked in this order and the first match wins. Order is the whole design: a
job can satisfy several rules at once, and the one that matters is the most urgent.

1. **`State == Succeeded` → `Done`** (`succeeded`)
   It landed. A passed deadline is irrelevant once the work is done.

2. **`State == Cancelled` → `Done`** (`cancelled`)
   An operator already decided this should not run, so a passed deadline is not a breach.
   This is a deliberate interpretation, not an oversight — see "Deviations" below.

3. **`now > DeadlineUtc` → `Breached`** (`past_deadline`)
   Past the regulatory or cycle deadline without succeeding.

4. **`State == DeadLettered && !DeadLetterResolved` → `NeedsHuman`** (`dead_lettered`)
   Parked. No amount of waiting changes the outcome.

5. **`AtRisk`**, if any of these hold:
   - a. `secondsToDeadline <= atRiskThreshold` (`deadline_near`)
     Not much slack left. The threshold is `Risk:AtRiskThresholdMinutes`, default **45
     simulated minutes**.
   - b. `NextAttemptUtc > DeadlineUtc` (`next_attempt_after_deadline`)
     The retry is already scheduled for after the deadline, so it cannot land in time.
   - c. `Attempts > 0 && remainingBackoff > secondsToDeadline` (`backoff_exceeds_slack`)
     Even if every remaining attempt succeeds, the waits between them overshoot the deadline.
     `remainingBackoff` is the un-jittered sum of the delays still ahead.

6. **Otherwise → `OnTrack`** (`on_track`)

Rule 5c is skipped for a job with zero attempts, because a job that has not failed yet should
not be penalised for backoff it may never need.

## A note on time scale

The at-risk threshold is expressed in the **simulated** minutes an analyst reads on the
dashboard, not in real seconds. At the default time scale of 60×, a 45 simulated-minute
threshold is 45 real seconds of slack:

```
atRiskThresholdRealSeconds = thresholdMinutes * 60 / timeScale
```

Deadlines themselves are stored as real instants, computed at seed time from the compressed
window. Both sides of every comparison are therefore in real time, and the scaling cancels.

---

## Worked example 1: backoff cannot fit before the deadline

A payment disbursement worth **$44,129.00**.

| Fact | Value |
| --- | --- |
| Policy | base 30s, cap 900s, 3 retries (so 4 attempts total) |
| Attempts made | 1 (failed, transient) |
| Next attempt | in 30s |
| Time to deadline | **120s** |
| Threshold | 60s |

Walking the rules:

1. Not succeeded → continue.
2. Not cancelled → continue.
3. `now <= deadline` (120s remain) → not `Breached`.
4. Not dead-lettered → continue.
5. a. `120s <= 60s`? No.
   b. Next attempt is at +30s, deadline is at +120s. `30 <= 120`, so not this rule either.
   c. Remaining backoff after attempt 1 of 4 is `30 + 60 + 120 = 210s`.
      `210 > 120` → **`AtRisk`**, reason `backoff_exceeds_slack`.

The job looks fine by every simple measure — it is inside its deadline, its next retry is
soon, it has retries left — and it is still doomed. Ranking by failure time hides this
completely. This is the case CycleGuard exists to surface.

Test: `WorkedExampleOne_BackoffCannotFitBeforeDeadline`.

## Worked example 2: plenty of slack

An encounter submission worth **$412.00**.

| Fact | Value |
| --- | --- |
| Attempts made | 0 |
| Next attempt | immediately |
| Time to deadline | **4 hours** |
| Threshold | 45 simulated minutes |

1. Not succeeded, 2. not cancelled, 3. not past deadline, 4. not dead-lettered.
5. a. `14400s <= 2700s`? No.
   b. No next-attempt time in the future beyond the deadline.
   c. `Attempts == 0`, so the remaining-backoff rule does not apply.
6. → **`OnTrack`**.

It sorts to the bottom of the list and nobody looks at it. That is the correct outcome, and
the reason the list is readable at 8:45am.

Test: `WorkedExampleTwo_PlentyOfSlackIsOnTrack`.

---

## Time to breach

`secondsToDeadline = (DeadlineTicks - nowTicks) / ticksPerSecond`, which goes negative once
breached. The API returns both the real value and `simulatedSecondsToDeadline`
(`real * timeScale`), and the dashboard counts down the simulated one so the narrative reads
in cycle time. The default sort is ascending `secondsToDeadline`, with `Done` jobs pushed to
the bottom regardless of their deadline.

## Dollars at risk

```
dollarsAtRisk = Σ AmountAtStakeCents where risk ∈ { Breached, NeedsHuman, AtRisk }
```

Money is stored and summed as integer cents. Nothing about money is ever a float.

## Deviations worth knowing about

- **Cancelled is `Done`, not `Breached`.** A literal reading of "past deadline and not
  succeeded" would mark every cancelled job as breached forever, which would bury the real
  breaches. An operator cancelling a job is a decision, not a failure.
- **A resolved dead letter stops being `NeedsHuman`.** Requeueing sets `DeadLetterResolved`,
  so the job re-enters the normal risk path instead of counting as parked twice.
- **`Breached` outranks `NeedsHuman`.** A dead-lettered job that is also past its deadline
  shows as `Breached`, because the deadline is the thing with consequences.
- **Risk is evaluated in memory** over at most 5,000 jobs per request. The demo runs 300. A
  real deployment would push this into SQL.
