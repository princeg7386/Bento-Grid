import { duration, money, signatureLabel } from '../lib/format'
import type { RootCauseGroup } from '../lib/types'
import { EmptyState, ErrorState, LoadingRows, Panel } from './Primitives'

export function GroupCards({
  groups,
  loading,
  error,
  onRetry,
  onSelectEndpoint,
  activeEndpoint,
}: {
  groups: RootCauseGroup[]
  loading: boolean
  error: string | null
  onRetry: () => void
  onSelectEndpoint: (endpoint: string | null) => void
  activeEndpoint: string | null
}) {
  return (
    <Panel title="Root causes" subtitle={`${groups.length} group${groups.length === 1 ? '' : 's'}`}>
      {error ? (
        <ErrorState message={error} onRetry={onRetry} />
      ) : loading ? (
        <LoadingRows rows={3} label="Loading root causes" />
      ) : groups.length === 0 ? (
        <EmptyState title="No failures to group" hint="Root causes appear once jobs start failing." />
      ) : (
        <div className="grid gap-3 p-4 sm:grid-cols-2 xl:grid-cols-3">
          {groups.map((group) => {
            const key = `${group.downstreamEndpoint}:${group.failureSignature}`
            const active = group.downstreamEndpoint === activeEndpoint

            return (
              <button
                key={key}
                type="button"
                onClick={() => onSelectEndpoint(active ? null : group.downstreamEndpoint)}
                className={`flex flex-col gap-2.5 rounded-xl p-4 text-left transition-all duration-150 hover:-translate-y-0.5 ${
                  active ? 'bg-signal-track/15 ring-1 ring-signal-track/50' : 'bg-ink-850 hover:bg-ink-800'
                }`}
              >
                <div className="flex items-start justify-between gap-2">
                  <span className="text-sm font-bold text-ink-100">{group.downstreamEndpoint}</span>
                  {group.healing ? (
                    <span className="shrink-0 rounded-full bg-signal-done/15 px-2 py-0.5 text-[11px] font-semibold text-signal-done">
                      ↻ Healing
                    </span>
                  ) : (
                    <span className="shrink-0 rounded-full bg-signal-human/15 px-2 py-0.5 text-[11px] font-semibold text-signal-human">
                      ✋ Parked
                    </span>
                  )}
                </div>

                <div className="text-xs font-medium text-ink-400">{signatureLabel(group.failureSignature)}</div>

                <p className="text-xs leading-snug text-ink-200">{group.cause}</p>

                <div className="mt-auto flex items-end justify-between gap-2 border-t border-white/[0.06] pt-2.5">
                  <div>
                    <div className="tnum text-xl leading-none font-bold text-ink-100">{group.jobCount}</div>
                    <div className="mt-0.5 text-[11px] text-ink-400">Jobs</div>
                  </div>
                  <div className="text-right">
                    <div className="tnum text-base leading-none font-bold text-signal-risk">
                      {money(group.dollarsAtRiskCents)}
                    </div>
                    <div className="mt-0.5 text-[11px] text-ink-400">At risk</div>
                  </div>
                  <div className="text-right">
                    <div
                      className={`tnum text-base leading-none font-bold ${
                        (group.secondsToEarliestDeadline ?? 0) <= 0 ? 'text-signal-breach' : 'text-ink-200'
                      }`}
                    >
                      {group.secondsToEarliestDeadline === null ? '—' : duration(group.secondsToEarliestDeadline)}
                    </div>
                    <div className="mt-0.5 text-[11px] text-ink-400">First breach</div>
                  </div>
                </div>
              </button>
            )
          })}
        </div>
      )}
    </Panel>
  )
}
