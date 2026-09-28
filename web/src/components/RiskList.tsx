import { clock, duration, jobTypeLabel, money, stateLabel } from '../lib/format'
import { simulatedDrift } from '../lib/usePolling'
import type { JobSummary } from '../lib/types'
import { EmptyState, ErrorState, LoadingRows, Panel, RiskBadge } from './Primitives'

export function RiskList({
  jobs,
  loading,
  error,
  onRetry,
  onSelect,
  selectedId,
  fetchedAt,
  timeScale,
  filterLabel,
  onClearFilter,
}: {
  jobs: JobSummary[]
  loading: boolean
  error: string | null
  onRetry: () => void
  onSelect: (id: number) => void
  selectedId: number | null
  fetchedAt: number
  timeScale: number
  filterLabel: string | null
  onClearFilter: () => void
}) {
  const drift = simulatedDrift(fetchedAt, timeScale)

  return (
    <Panel
      title="At risk, soonest first"
      subtitle={`${jobs.length} shown`}
      className="min-h-0"
      actions={
        filterLabel ? (
          <button
            type="button"
            onClick={onClearFilter}
            className="rounded border border-ink-600 px-2 py-0.5 font-mono text-[10px] text-ink-200 transition hover:border-ink-400 hover:bg-ink-800"
          >
            {filterLabel} ✕
          </button>
        ) : null
      }
    >
      {error ? (
        <ErrorState message={error} onRetry={onRetry} />
      ) : loading ? (
        <LoadingRows rows={8} label="Loading jobs" />
      ) : jobs.length === 0 ? (
        <EmptyState
          title="Nothing needs attention"
          hint="Either the cycle is clean or no scenario is loaded. Run “Simulate last night” from the panel on the right."
        />
      ) : (
        <table className="w-full border-collapse text-left">
          <thead className="sticky top-0 z-10 bg-ink-850">
            <tr className="font-mono text-[10px] tracking-[0.1em] text-ink-400 uppercase">
              <th className="px-3 py-2 font-medium">Risk</th>
              <th className="px-3 py-2 font-medium">Job</th>
              <th className="px-3 py-2 text-right font-medium">At stake</th>
              <th className="px-3 py-2 text-right font-medium">Deadline</th>
              <th className="px-3 py-2 font-medium">State</th>
              <th className="px-3 py-2 text-right font-medium">Next try</th>
              <th className="px-3 py-2 font-medium">Why</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map((job) => {
              const remaining = job.simulatedSecondsToDeadline - drift
              const nextAttempt =
                job.simulatedSecondsToNextAttempt === null ? null : job.simulatedSecondsToNextAttempt - drift
              const selected = job.id === selectedId

              return (
                <tr
                  key={job.id}
                  onClick={() => onSelect(job.id)}
                  className={`cursor-pointer border-t border-ink-800 transition ${
                    selected ? 'bg-ink-800' : 'hover:bg-ink-850'
                  }`}
                >
                  <td className="px-3 py-2 align-top">
                    <RiskBadge risk={job.risk} />
                  </td>
                  <td className="px-3 py-2 align-top">
                    <div className="text-xs font-medium text-ink-100">{jobTypeLabel(job.type)}</div>
                    <div className="font-mono text-[10px] text-ink-400">
                      #{job.id} · {job.downstreamEndpoint}
                      {job.requeueCount > 0 && ` · requeued ${job.requeueCount}×`}
                    </div>
                  </td>
                  <td className="tnum px-3 py-2 text-right align-top font-mono text-xs font-semibold text-ink-100">
                    {money(job.amountAtStakeCents)}
                  </td>
                  <td
                    className={`tnum px-3 py-2 text-right align-top font-mono text-xs ${
                      remaining <= 0 ? 'text-signal-breach' : remaining < 1800 ? 'text-signal-risk' : 'text-ink-200'
                    }`}
                  >
                    {remaining <= 0 ? `-${clock(Math.abs(remaining))}` : clock(remaining)}
                  </td>
                  <td className="px-3 py-2 align-top font-mono text-[11px] text-ink-200">
                    {stateLabel(job.state)}
                    <span className="block text-[10px] text-ink-400">
                      attempt {job.attempts}/{job.maxAttempts}
                    </span>
                  </td>
                  <td className="tnum px-3 py-2 text-right align-top font-mono text-[11px] text-ink-300">
                    {nextAttempt === null ? '—' : duration(nextAttempt)}
                  </td>
                  <td className="px-3 py-2 align-top text-[11px] leading-snug text-ink-300">
                    {job.cause ?? job.riskReason}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </Panel>
  )
}
