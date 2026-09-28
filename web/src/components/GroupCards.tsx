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
        <div className="grid gap-2 p-3 sm:grid-cols-2 xl:grid-cols-3">
          {groups.map((group) => {
            const key = `${group.downstreamEndpoint}:${group.failureSignature}`
            const active = group.downstreamEndpoint === activeEndpoint

            return (
              <button
                key={key}
                type="button"
                onClick={() => onSelectEndpoint(active ? null : group.downstreamEndpoint)}
                className={`flex flex-col gap-2 rounded border p-3 text-left transition ${
                  active
                    ? 'border-signal-track/60 bg-signal-track/10'
                    : 'border-ink-700/70 bg-ink-850 hover:border-ink-600 hover:bg-ink-800'
                }`}
              >
                <div className="flex items-start justify-between gap-2">
                  <span className="font-mono text-[11px] font-semibold text-ink-100">{group.downstreamEndpoint}</span>
                  {group.healing ? (
                    <span className="shrink-0 rounded border border-signal-done/40 bg-signal-done/10 px-1.5 py-0.5 font-mono text-[9px] tracking-wider text-signal-done">
                      ↻ HEALING
                    </span>
                  ) : (
                    <span className="shrink-0 rounded border border-signal-human/40 bg-signal-human/10 px-1.5 py-0.5 font-mono text-[9px] tracking-wider text-signal-human">
                      ✋ PARKED
                    </span>
                  )}
                </div>

                <div className="font-mono text-[10px] tracking-wide text-ink-400 uppercase">
                  {signatureLabel(group.failureSignature)}
                </div>

                <p className="text-[11px] leading-snug text-ink-200">{group.cause}</p>

                <div className="mt-auto flex items-end justify-between gap-2 border-t border-ink-700/60 pt-2">
                  <div>
                    <div className="tnum font-mono text-lg leading-none font-bold text-ink-100">{group.jobCount}</div>
                    <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">jobs</div>
                  </div>
                  <div className="text-right">
                    <div className="tnum font-mono text-sm leading-none font-semibold text-signal-risk">
                      {money(group.dollarsAtRiskCents)}
                    </div>
                    <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">at risk</div>
                  </div>
                  <div className="text-right">
                    <div
                      className={`tnum font-mono text-sm leading-none font-semibold ${
                        (group.secondsToEarliestDeadline ?? 0) <= 0 ? 'text-signal-breach' : 'text-ink-200'
                      }`}
                    >
                      {group.secondsToEarliestDeadline === null ? '—' : duration(group.secondsToEarliestDeadline)}
                    </div>
                    <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">first breach</div>
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
