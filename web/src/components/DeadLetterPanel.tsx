import { jobTypeLabel, money, signatureLabel } from '../lib/format'
import type { DeadLetterGroup } from '../lib/types'
import { EmptyState, ErrorState, LoadingRows, Panel, RiskBadge } from './Primitives'

export function DeadLetterPanel({
  groups,
  loading,
  error,
  onRetry,
  onSelect,
}: {
  groups: DeadLetterGroup[]
  loading: boolean
  error: string | null
  onRetry: () => void
  onSelect: (id: number) => void
}) {
  const total = groups.reduce((sum, group) => sum + group.jobCount, 0)

  return (
    <Panel title="Dead letters by cause" subtitle={total === 0 ? undefined : `${total} job${total === 1 ? '' : 's'}`}>
      {error ? (
        <ErrorState message={error} onRetry={onRetry} />
      ) : loading ? (
        <LoadingRows rows={3} label="Loading dead letters" />
      ) : groups.length === 0 ? (
        <EmptyState title="No dead letters" hint="Permanent failures and exhausted retries land here." />
      ) : (
        <div className="divide-y divide-ink-800">
          {groups.map((group) => (
            <div key={group.failureSignature} className="p-3">
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <span className="font-mono text-[11px] font-semibold tracking-wide text-ink-100 uppercase">
                  {signatureLabel(group.failureSignature)}
                </span>
                <span className="flex items-center gap-2 font-mono text-[10px]">
                  <span
                    className={
                      group.failureClass === 'Permanent' ? 'text-signal-human' : 'text-signal-risk'
                    }
                  >
                    {group.failureClass === 'Permanent' ? '■ permanent' : '▲ transient'}
                  </span>
                  <span className="tnum text-signal-risk">{money(group.dollarsAtRiskCents)}</span>
                  <span className="tnum text-ink-400">
                    {group.jobCount} job{group.jobCount === 1 ? '' : 's'}
                  </span>
                </span>
              </div>

              <p className="mt-1 text-[11px] leading-snug text-ink-200">{group.cause}</p>
              <p className="mt-0.5 text-[11px] leading-snug text-ink-400">→ {group.suggestedAction}</p>

              <ul className="mt-2 space-y-1">
                {group.jobs.slice(0, 6).map((job) => (
                  <li key={job.id}>
                    <button
                      type="button"
                      onClick={() => onSelect(job.id)}
                      className="flex w-full items-center gap-2 rounded border border-ink-800 bg-ink-850 px-2 py-1 text-left transition hover:border-ink-600 hover:bg-ink-800"
                    >
                      <RiskBadge risk={job.risk} compact />
                      <span className="min-w-0 flex-1 truncate font-mono text-[10px] text-ink-200">
                        #{job.id} {jobTypeLabel(job.type)}
                      </span>
                      <span className="tnum shrink-0 font-mono text-[10px] text-ink-300">
                        {money(job.amountAtStakeCents)}
                      </span>
                    </button>
                  </li>
                ))}
                {group.jobs.length > 6 && (
                  <li className="pl-1 font-mono text-[10px] text-ink-400">
                    + {group.jobs.length - 6} more in this group
                  </li>
                )}
              </ul>
            </div>
          ))}
        </div>
      )}
    </Panel>
  )
}
