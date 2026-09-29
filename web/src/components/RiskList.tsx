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
          <button type="button" onClick={onClearFilter} className="pill pill-ghost px-3 py-1 text-xs">
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
            <tr className="text-xs font-semibold text-ink-400">
              <th className="px-4 py-2.5 font-semibold">Risk</th>
              <th className="px-4 py-2.5 font-semibold">Job</th>
              <th className="px-4 py-2.5 text-right font-semibold">At stake</th>
              <th className="px-4 py-2.5 text-right font-semibold">Deadline</th>
              <th className="px-4 py-2.5 font-semibold">State</th>
              <th className="px-4 py-2.5 text-right font-semibold">Next try</th>
              <th className="px-4 py-2.5 font-semibold">Why</th>
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
                  className={`cursor-pointer border-t border-white/[0.05] transition-colors duration-150 ${
                    selected ? 'bg-ink-800' : 'hover:bg-ink-850'
                  }`}
                >
                  <td className="px-4 py-2.5 align-top">
                    <RiskBadge risk={job.risk} />
                  </td>
                  <td className="px-4 py-2.5 align-top">
                    <div className="flex items-center gap-1.5">
                      <span className="text-sm font-semibold text-ink-100">{jobTypeLabel(job.type)}</span>
                      {job.slaLabel && (
                        <span
                          className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-medium ${
                            job.slaClass === 'Expedited72Hour'
                              ? 'bg-signal-risk/15 text-signal-risk'
                              : 'bg-ink-800 text-ink-300'
                          }`}
                          title="Regulatory SLA for this prior-authorization-style decision."
                        >
                          ⏱ {job.slaLabel}
                        </span>
                      )}
                    </div>
                    <div className="mt-0.5 text-xs text-ink-400">
                      #{job.id} · {job.downstreamEndpoint}
                      {job.requeueCount > 0 && ` · requeued ${job.requeueCount}×`}
                    </div>
                  </td>
                  <td className="tnum px-4 py-2.5 text-right align-top text-sm font-semibold text-ink-100">
                    {money(job.amountAtStakeCents)}
                  </td>
                  <td
                    className={`tnum px-4 py-2.5 text-right align-top text-sm ${
                      remaining <= 0 ? 'text-signal-breach' : remaining < 1800 ? 'text-signal-risk' : 'text-ink-200'
                    }`}
                  >
                    {remaining <= 0 ? `-${clock(Math.abs(remaining))}` : clock(remaining)}
                  </td>
                  <td className="px-4 py-2.5 align-top text-sm text-ink-200">
                    {stateLabel(job.state)}
                    <span className="block text-xs text-ink-400">
                      attempt {job.attempts}/{job.maxAttempts}
                    </span>
                  </td>
                  <td className="tnum px-4 py-2.5 text-right align-top text-sm text-ink-300">
                    {nextAttempt === null ? '—' : duration(nextAttempt)}
                  </td>
                  <td className="px-4 py-2.5 align-top text-sm leading-snug text-ink-300">
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
