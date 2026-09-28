import { useCallback, useMemo, useState } from 'react'
import { api } from './lib/api'
import { needsAttention } from './lib/risk'
import type { DeadLetterGroup, JobSummary, RootCauseGroup, Status } from './lib/types'
import { usePolling, useTicker } from './lib/usePolling'
import { CycleBanner } from './components/CycleBanner'
import { DeadLetterPanel } from './components/DeadLetterPanel'
import { GroupCards } from './components/GroupCards'
import { JobDrawer } from './components/JobDrawer'
import { RiskList } from './components/RiskList'
import { SimulationPanel } from './components/SimulationPanel'
import { ErrorState, LoadingRows } from './components/Primitives'

type Scope = 'attention' | 'all' | 'deadletters'

const POLL_MS = 2000

export default function App() {
  const [scope, setScope] = useState<Scope>('attention')
  const [endpointFilter, setEndpointFilter] = useState<string | null>(null)
  const [selectedJobId, setSelectedJobId] = useState<number | null>(null)

  // Re-render four times a second so every countdown moves smoothly between polls.
  useTicker(250)

  const statusPoll = usePolling<Status>(() => api.status(), POLL_MS)
  const jobsPoll = usePolling<JobSummary[]>(
    useCallback(() => api.jobs(endpointFilter ? { endpoint: endpointFilter } : {}), [endpointFilter]),
    POLL_MS,
  )
  const groupsPoll = usePolling<RootCauseGroup[]>(() => api.groups(), POLL_MS)
  const deadLetterPoll = usePolling<DeadLetterGroup[]>(() => api.deadLetters(), POLL_MS)

  const allJobs = jobsPoll.data ?? []

  const visibleJobs = useMemo(() => {
    switch (scope) {
      case 'attention':
        return allJobs.filter((job) => needsAttention.includes(job.risk))
      case 'deadletters':
        return allJobs.filter((job) => job.state === 'DeadLettered')
      default:
        return allJobs
    }
  }, [allJobs, scope])

  const attentionJobs = useMemo(() => allJobs.filter((job) => needsAttention.includes(job.risk)), [allJobs])

  const refreshAll = useCallback(() => {
    void statusPoll.refresh()
    void jobsPoll.refresh()
    void groupsPoll.refresh()
    void deadLetterPoll.refresh()
  }, [statusPoll, jobsPoll, groupsPoll, deadLetterPoll])

  const scopes: { id: Scope; label: string; count: number }[] = [
    { id: 'attention', label: 'Needs attention', count: attentionJobs.length },
    { id: 'all', label: 'All jobs', count: allJobs.length },
    { id: 'deadletters', label: 'Dead letters', count: allJobs.filter((job) => job.state === 'DeadLettered').length },
  ]

  return (
    <div className="min-h-full bg-ink-950">
      <div className="mx-auto flex max-w-[1800px] flex-col gap-3 p-3 lg:p-4">
        <div className="flex items-baseline justify-between gap-4">
          <div className="flex items-baseline gap-3">
            <h1 className="font-mono text-sm font-bold tracking-[0.2em] text-ink-100 uppercase">CycleGuard</h1>
            <p className="hidden text-[11px] text-ink-400 sm:block">
              Sorted by when it will cost you, not by when it broke.
            </p>
          </div>
          <span className="font-mono text-[10px] tracking-wider text-ink-400 uppercase">
            <span className="mr-1.5 inline-block h-1.5 w-1.5 rounded-full bg-signal-done align-middle cg-pulse" />
            polling every {POLL_MS / 1000}s
          </span>
        </div>

        {statusPoll.error && !statusPoll.data ? (
          <div className="rounded-lg border border-ink-700/70 bg-ink-900">
            <ErrorState message={statusPoll.error} onRetry={() => void statusPoll.refresh()} />
          </div>
        ) : statusPoll.loading && !statusPoll.data ? (
          <div className="rounded-lg border border-ink-700/70 bg-ink-900">
            <LoadingRows rows={4} label="Loading cycle status" />
          </div>
        ) : statusPoll.data ? (
          <CycleBanner status={statusPoll.data} jobs={attentionJobs} fetchedAt={jobsPoll.fetchedAt} />
        ) : null}

        <div className="grid min-h-0 gap-3 xl:grid-cols-[minmax(0,1fr)_360px]">
          <div className="flex min-h-0 flex-col gap-3">
            <GroupCards
              groups={groupsPoll.data ?? []}
              loading={groupsPoll.loading && !groupsPoll.data}
              error={groupsPoll.error}
              onRetry={() => void groupsPoll.refresh()}
              onSelectEndpoint={setEndpointFilter}
              activeEndpoint={endpointFilter}
            />

            <div className="flex flex-wrap gap-1.5">
              {scopes.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  onClick={() => setScope(item.id)}
                  className={`rounded border px-2.5 py-1 font-mono text-[10px] tracking-wider transition ${
                    scope === item.id
                      ? 'border-ink-400 bg-ink-800 text-ink-100'
                      : 'border-ink-700 bg-ink-900 text-ink-300 hover:border-ink-500'
                  }`}
                >
                  {item.label} <span className="tnum text-ink-400">{item.count}</span>
                </button>
              ))}
            </div>

            <RiskList
              jobs={visibleJobs}
              loading={jobsPoll.loading && !jobsPoll.data}
              error={jobsPoll.error}
              onRetry={() => void jobsPoll.refresh()}
              onSelect={setSelectedJobId}
              selectedId={selectedJobId}
              fetchedAt={jobsPoll.fetchedAt}
              timeScale={statusPoll.data?.timeScale ?? 1}
              filterLabel={endpointFilter}
              onClearFilter={() => setEndpointFilter(null)}
            />
          </div>

          <div className="flex min-h-0 flex-col gap-3">
            <SimulationPanel status={statusPoll.data} onChanged={refreshAll} />
            <DeadLetterPanel
              groups={deadLetterPoll.data ?? []}
              loading={deadLetterPoll.loading && !deadLetterPoll.data}
              error={deadLetterPoll.error}
              onRetry={() => void deadLetterPoll.refresh()}
              onSelect={setSelectedJobId}
            />
          </div>
        </div>
      </div>

      {selectedJobId !== null && <JobDrawer jobId={selectedJobId} onClose={() => setSelectedJobId(null)} />}
    </div>
  )
}
