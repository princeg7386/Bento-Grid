import { clock, money } from '../lib/format'
import { simulatedDrift } from '../lib/usePolling'
import { riskStyle } from '../lib/risk'
import type { JobSummary, Status } from '../lib/types'
import { Metric } from './Primitives'

/**
 * The cycle rail is the one visual idea worth remembering: a single track from now to the
 * moment the payment cycle closes, with every job that still needs attention pinned at the
 * point where it runs out of time. Breaches pile up hard against the left edge.
 */
function CycleRail({
  jobs,
  cycleWindowSeconds,
  drift,
  timeScale,
}: {
  jobs: JobSummary[]
  cycleWindowSeconds: number
  drift: number
  timeScale: number
}) {
  const window = Math.max(cycleWindowSeconds, 1)

  return (
    <div className="mt-4">
      <div className="relative h-11 overflow-hidden rounded-xl bg-ink-950">
        {/* quarter gridlines, so a position on the rail reads as a time */}
        {[0.25, 0.5, 0.75].map((fraction) => (
          <div key={fraction} className="absolute inset-y-0 w-px bg-white/[0.06]" style={{ left: `${fraction * 100}%` }} />
        ))}

        {jobs.map((job) => {
          const remaining = job.simulatedSecondsToDeadline - drift
          const fraction = Math.min(Math.max(remaining / window, 0), 1)
          const style = riskStyle(job.risk)
          const breached = remaining <= 0

          return (
            <div
              key={job.id}
              className={`absolute top-1 bottom-1 w-[3px] rounded-full transition-[left] duration-500 ease-linear ${style.rail} ${breached ? 'opacity-100' : 'opacity-70'}`}
              style={{ left: `calc(${fraction * 100}% - 1.5px)` }}
              title={`${style.label} · ${money(job.amountAtStakeCents)} · ${clock(Math.max(remaining, 0))} left`}
            />
          )
        })}

        <div className="pointer-events-none absolute inset-y-0 right-0 w-px bg-signal-risk/70" />
      </div>

      <div className="mt-1.5 flex justify-between text-xs text-ink-400">
        <span>Now</span>
        <span>{jobs.length} jobs needing attention, placed by when they run out of time</span>
        <span>Cycle closes · {timeScale}× time</span>
      </div>
    </div>
  )
}

export function CycleBanner({
  status,
  jobs,
  fetchedAt,
}: {
  status: Status
  jobs: JobSummary[]
  fetchedAt: number
}) {
  const drift = simulatedDrift(fetchedAt, status.timeScale)
  const secondsToClose = Math.max(status.simulatedSecondsToCycleClose - drift, 0)
  const closed = status.scenarioLoaded && secondsToClose <= 0

  const attentionJobs = jobs.filter((job) => job.risk !== 'Done')
  const done = status.byState.Succeeded ?? 0

  return (
    <header className="rounded-2xl border border-ink-800 bg-gradient-to-b from-ink-850 to-ink-900 p-5 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-8">
        <div>
          <p className="text-sm font-medium text-ink-400">{closed ? 'Payment cycle closed' : 'Payment cycle closes in'}</p>
          <p
            className={`tnum mt-1 text-6xl leading-none font-bold tracking-tight ${
              closed ? 'text-signal-breach' : secondsToClose < 1800 ? 'text-signal-risk cg-pulse' : 'text-ink-100'
            }`}
          >
            {status.scenarioLoaded ? clock(secondsToClose) : '--:--:--'}
          </p>
          <p className="mt-2 text-sm text-ink-400">
            {status.scenarioLoaded
              ? `${status.totalJobs} jobs ran overnight · ${done} done · ${status.needingAttention} need attention`
              : 'No scenario loaded. Run "Simulate last night" to populate the cycle.'}
          </p>
        </div>

        <div className="flex flex-col items-end">
          <span className="text-sm font-medium text-ink-400">Dollars at risk</span>
          <span
            className={`tnum text-5xl leading-none font-bold tracking-tight ${
              status.dollarsAtRiskCents > 0 ? 'text-signal-risk' : 'text-signal-done'
            }`}
          >
            {money(status.dollarsAtRiskCents)}
          </span>
          <span className="mt-2 text-sm text-ink-400">
            across {status.needingAttention} job{status.needingAttention === 1 ? '' : 's'}
          </span>
        </div>

        <div
          className="flex flex-col items-end"
          title="Duplicate disbursements the ledger refused to apply twice, plus payments recovered from a worker that crashed mid-job -- money this system already protected, not money still at risk."
        >
          <span className="text-sm font-medium text-ink-400">Value protected</span>
          <span
            className={`tnum text-5xl leading-none font-bold tracking-tight ${
              status.valueProtectedCents > 0 ? 'text-signal-done' : 'text-ink-400'
            }`}
          >
            {money(status.valueProtectedCents)}
          </span>
          <span className="mt-2 text-right text-sm text-ink-400">
            {status.duplicatesPreventedCount} duplicate{status.duplicatesPreventedCount === 1 ? '' : 's'} blocked ·{' '}
            {status.recoveredFromCrashedWorkerCount} recovered from a crashed worker
          </span>
        </div>
      </div>

      <div className="mt-5 grid grid-cols-2 gap-x-6 gap-y-4 border-t border-white/[0.06] pt-4 sm:grid-cols-3 lg:grid-cols-6">
        <Metric
          label="Throughput"
          value={`${status.throughputPerMinute}/min`}
          hint="Jobs completed in the trailing real minute."
        />
        <Metric
          label="Success rate"
          value={`${status.successRatePercent}%`}
          tone={status.successRatePercent >= 95 ? 'good' : status.successRatePercent >= 80 ? 'warn' : 'bad'}
          hint="Succeeded as a share of everything that finished."
        />
        <Metric
          label="Retry rate"
          value={`${status.retryRatePercent}%`}
          tone={status.retryRatePercent > 50 ? 'warn' : 'default'}
          hint="Extra attempts per job that ran at least once."
        />
        <Metric
          label="Dead letters"
          value={String(status.deadLetterCount)}
          tone={status.deadLetterCount > 0 ? 'bad' : 'good'}
          hint="Permanent failures and exhausted retries awaiting a human."
        />
        <Metric
          label="Duplicates prevented"
          value={String(status.duplicatesPreventedCount)}
          tone={status.duplicatesPreventedCount > 0 ? 'good' : 'default'}
          hint={`${money(status.duplicatesPreventedCents)} of disbursements the downstream ledger refused to apply twice.`}
        />
        <Metric label="Workers" value={String(status.workerCount)} hint="Concurrent queue-draining workers." />
      </div>

      {status.activeOutages.length > 0 && (
        <div className="mt-4 flex flex-wrap gap-2">
          {status.activeOutages.map((outage) => (
            <span
              key={outage.endpoint}
              className="inline-flex items-center gap-2 rounded-full border border-signal-breach/40 bg-signal-breach/10 px-3 py-1.5 text-sm font-medium text-signal-breach"
            >
              <span aria-hidden="true">▲</span>
              {outage.endpoint} unreachable · recovers in {clock(outage.secondsRemaining)}
            </span>
          ))}
        </div>
      )}

      {status.scenarioLoaded && (
        <CycleRail
          jobs={attentionJobs}
          cycleWindowSeconds={Math.max(status.simulatedSecondsToCycleClose, 1)}
          drift={drift}
          timeScale={status.timeScale}
        />
      )}
    </header>
  )
}
