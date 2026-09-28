import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { clock, duration, jobTypeLabel, moneyExact, signatureLabel, stateLabel } from '../lib/format'
import { usePolling } from '../lib/usePolling'
import type { Attempt, JobDetail } from '../lib/types'
import { ErrorState, LoadingRows, RiskBadge } from './Primitives'

function AttemptRow({ attempt }: { attempt: Attempt }) {
  const failed = attempt.outcome !== 'Succeeded' && attempt.outcome !== null
  const glyph =
    attempt.outcome === 'Succeeded'
      ? '✓'
      : attempt.outcome === 'PermanentFailure'
        ? '■'
        : attempt.outcome === 'LeaseExpired'
          ? '⧗'
          : attempt.outcome === null
            ? '●'
            : '▲'

  return (
    <li className="relative border-l border-ink-700 pb-3 pl-5 last:pb-0">
      <span
        className={`absolute top-0.5 -left-[7px] flex h-3.5 w-3.5 items-center justify-center rounded-full border text-[8px] ${
          attempt.outcome === 'Succeeded'
            ? 'border-signal-done/60 bg-ink-900 text-signal-done'
            : failed
              ? 'border-signal-breach/60 bg-ink-900 text-signal-breach'
              : 'border-ink-600 bg-ink-900 text-ink-300'
        }`}
        aria-hidden="true"
      >
        {glyph}
      </span>

      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
        <span className="font-mono text-[11px] font-semibold text-ink-100">Attempt {attempt.attemptNumber}</span>
        <span className="font-mono text-[10px] text-ink-400">{attempt.workerId}</span>
        <span className="font-mono text-[10px] text-ink-400">
          {new Date(attempt.startedUtc).toLocaleTimeString()}
        </span>
        <span
          className={`font-mono text-[10px] ${
            attempt.outcome === 'Succeeded' ? 'text-signal-done' : failed ? 'text-signal-breach' : 'text-ink-300'
          }`}
        >
          {attempt.outcome ?? 'in flight'}
        </span>
      </div>

      {attempt.failureSignature && (
        <div className="mt-0.5 font-mono text-[10px] tracking-wide text-ink-400 uppercase">
          {signatureLabel(attempt.failureSignature)}
          {attempt.backoffSeconds !== null && ` · backoff ${attempt.backoffSeconds}s`}
        </div>
      )}
    </li>
  )
}

function ErrorComparison({ detail }: { detail: JobDetail }) {
  const [showRaw, setShowRaw] = useState(false)
  const [showStack, setShowStack] = useState(false)

  const lastFailure = [...detail.attempts].reverse().find((attempt) => attempt.errorMasked)
  const masked = lastFailure?.errorMasked ?? detail.lastErrorMasked
  const raw = lastFailure?.errorRaw ?? detail.lastErrorRaw
  const maskedStack = lastFailure?.stackTraceMasked ?? detail.lastStackTraceMasked
  const rawStack = lastFailure?.stackTraceRaw ?? detail.lastStackTraceRaw

  if (!masked) {
    return (
      <p className="font-mono text-[11px] text-ink-400">This job has not produced an error.</p>
    )
  }

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <span className="font-mono text-[10px] tracking-[0.12em] text-ink-400 uppercase">
          {showRaw ? 'Raw (as the endpoint returned it)' : 'Masked (as CycleGuard stores it)'}
        </span>
        {raw && (
          <button
            type="button"
            onClick={() => setShowRaw((value) => !value)}
            className={`rounded border px-2 py-0.5 font-mono text-[10px] transition ${
              showRaw
                ? 'border-signal-breach/60 bg-signal-breach/10 text-signal-breach'
                : 'border-ink-600 text-ink-200 hover:border-ink-400 hover:bg-ink-800'
            }`}
          >
            {showRaw ? '◉ showing raw PHI' : '○ show raw'}
          </button>
        )}
      </div>

      <pre
        className={`overflow-x-auto rounded border p-2.5 font-mono text-[11px] leading-relaxed whitespace-pre-wrap ${
          showRaw ? 'border-signal-breach/40 bg-signal-breach/5 text-ink-100' : 'border-ink-700 bg-ink-950 text-ink-200'
        }`}
      >
        {showRaw ? raw : masked}
      </pre>

      {showRaw && (
        <p className="font-mono text-[10px] leading-relaxed text-signal-risk">
          Synthetic PHI, held in memory only. The database never received this text.
        </p>
      )}

      {(maskedStack || rawStack) && (
        <div>
          <button
            type="button"
            onClick={() => setShowStack((value) => !value)}
            className="font-mono text-[10px] text-ink-300 underline decoration-ink-600 underline-offset-2 hover:text-ink-100"
          >
            {showStack ? '▾ hide stack trace' : '▸ show stack trace'}
          </button>
          {showStack && (
            <pre className="mt-1.5 max-h-52 overflow-auto rounded border border-ink-700 bg-ink-950 p-2.5 font-mono text-[10px] leading-relaxed whitespace-pre text-ink-300">
              {showRaw ? (rawStack ?? maskedStack) : maskedStack}
            </pre>
          )}
        </div>
      )}
    </div>
  )
}

function RequeueForm({ jobId, onDone }: { jobId: number; onDone: () => void }) {
  const [analyst, setAnalyst] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ tone: 'ok' | 'bad'; text: string } | null>(null)

  const canSubmit = analyst.trim().length > 0 && note.trim().length > 0 && !busy

  async function submit() {
    setBusy(true)
    setMessage(null)
    try {
      await api.requeue(jobId, analyst.trim(), note.trim())
      setMessage({ tone: 'ok', text: 'Requeued. The idempotency key is unchanged, so it cannot pay twice.' })
      setNote('')
      onDone()
    } catch (caught) {
      setMessage({ tone: 'bad', text: caught instanceof Error ? caught.message : 'Requeue failed.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-2 rounded border border-ink-700 bg-ink-850 p-3">
      <p className="font-mono text-[10px] tracking-[0.12em] text-ink-400 uppercase">Requeue from dead letter</p>
      <div className="grid gap-2 sm:grid-cols-2">
        <label className="block">
          <span className="sr-only">Analyst name</span>
          <input
            value={analyst}
            onChange={(event) => setAnalyst(event.target.value)}
            placeholder="Analyst name (required)"
            className="w-full rounded border border-ink-600 bg-ink-950 px-2 py-1.5 font-mono text-[11px] text-ink-100 placeholder:text-ink-400 focus:border-signal-track focus:outline-none"
          />
        </label>
        <label className="block">
          <span className="sr-only">Note</span>
          <input
            value={note}
            onChange={(event) => setNote(event.target.value)}
            placeholder="What changed? (required)"
            className="w-full rounded border border-ink-600 bg-ink-950 px-2 py-1.5 font-mono text-[11px] text-ink-100 placeholder:text-ink-400 focus:border-signal-track focus:outline-none"
          />
        </label>
      </div>

      <button
        type="button"
        disabled={!canSubmit}
        onClick={() => void submit()}
        className="w-full rounded border border-signal-track/60 bg-signal-track/10 px-3 py-1.5 font-mono text-[11px] font-semibold text-signal-track transition enabled:hover:bg-signal-track/20 disabled:cursor-not-allowed disabled:border-ink-700 disabled:bg-ink-900 disabled:text-ink-400"
      >
        {busy ? 'Requeueing…' : 'Requeue this job'}
      </button>

      {message && (
        <p
          className={`font-mono text-[10px] leading-relaxed ${
            message.tone === 'ok' ? 'text-signal-done' : 'text-signal-breach'
          }`}
          role="status"
        >
          {message.text}
        </p>
      )}
    </div>
  )
}

export function JobDrawer({ jobId, onClose }: { jobId: number; onClose: () => void }) {
  const { data, loading, error, refresh } = usePolling<JobDetail>(() => api.job(jobId), 2000)

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <aside
      className="fixed inset-y-0 right-0 z-40 flex w-full max-w-xl flex-col border-l border-ink-700 bg-ink-900 shadow-2xl"
      role="dialog"
      aria-label="Job detail"
    >
      <header className="flex items-start justify-between gap-3 border-b border-ink-700 px-4 py-3">
        <div className="min-w-0">
          {data ? (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <RiskBadge risk={data.summary.risk} />
                <h2 className="truncate text-sm font-semibold text-ink-100">{jobTypeLabel(data.summary.type)}</h2>
              </div>
              <p className="mt-0.5 font-mono text-[10px] text-ink-400">
                #{data.summary.id} · {data.summary.downstreamEndpoint} · {stateLabel(data.summary.state)}
              </p>
            </>
          ) : (
            <h2 className="font-mono text-sm text-ink-300">Job #{jobId}</h2>
          )}
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded border border-ink-600 px-2 py-0.5 font-mono text-[11px] text-ink-200 transition hover:border-ink-400 hover:bg-ink-800"
          aria-label="Close job detail"
        >
          esc ✕
        </button>
      </header>

      <div className="min-h-0 flex-1 overflow-auto p-4">
        {error ? (
          <ErrorState message={error} onRetry={() => void refresh()} />
        ) : loading || !data ? (
          <LoadingRows rows={7} label="Loading job detail" />
        ) : (
          <div className="space-y-5">
            <div className="grid grid-cols-2 gap-3 rounded border border-ink-700 bg-ink-850 p-3 sm:grid-cols-4">
              <div>
                <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">At stake</div>
                <div className="tnum font-mono text-sm font-semibold text-ink-100">
                  {moneyExact(data.summary.amountAtStakeCents)}
                </div>
              </div>
              <div>
                <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">Time to breach</div>
                <div
                  className={`tnum font-mono text-sm font-semibold ${
                    data.summary.simulatedSecondsToDeadline <= 0 ? 'text-signal-breach' : 'text-ink-100'
                  }`}
                >
                  {clock(data.summary.simulatedSecondsToDeadline)}
                </div>
              </div>
              <div>
                <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">Attempts</div>
                <div className="tnum font-mono text-sm font-semibold text-ink-100">
                  {data.summary.attempts}/{data.summary.maxAttempts}
                </div>
              </div>
              <div>
                <div className="font-mono text-[9px] tracking-wider text-ink-400 uppercase">Requeues</div>
                <div className="tnum font-mono text-sm font-semibold text-ink-100">{data.summary.requeueCount}</div>
              </div>
            </div>

            {data.summary.cause && (
              <div className="rounded border border-signal-risk/30 bg-signal-risk/5 p-3">
                <p className="font-mono text-[10px] tracking-[0.12em] text-signal-risk uppercase">Plain English</p>
                <p className="mt-1 text-xs leading-relaxed text-ink-100">{data.summary.cause}</p>
                <p className="mt-2 font-mono text-[10px] tracking-[0.12em] text-ink-400 uppercase">Suggested action</p>
                <p className="mt-1 text-xs leading-relaxed text-ink-200">{data.summary.suggestedAction}</p>
              </div>
            )}

            <section>
              <h3 className="mb-2 font-mono text-[10px] tracking-[0.14em] text-ink-300 uppercase">Attempt timeline</h3>
              <ol className="ml-1.5">
                {data.attempts.map((attempt) => (
                  <AttemptRow key={attempt.id} attempt={attempt} />
                ))}
                {data.attempts.length === 0 && (
                  <li className="font-mono text-[11px] text-ink-400">Not attempted yet.</li>
                )}
              </ol>
            </section>

            {data.upcomingBackoff.length > 0 && (
              <section>
                <h3 className="mb-2 font-mono text-[10px] tracking-[0.14em] text-ink-300 uppercase">
                  Upcoming backoff
                </h3>
                <ul className="space-y-1">
                  {data.upcomingBackoff.map((slot) => (
                    <li
                      key={slot.attemptNumber}
                      className={`flex items-center justify-between rounded border px-2.5 py-1.5 font-mono text-[11px] ${
                        slot.afterDeadline
                          ? 'border-signal-breach/40 bg-signal-breach/5 text-signal-breach'
                          : 'border-ink-700 bg-ink-850 text-ink-200'
                      }`}
                    >
                      <span>attempt {slot.attemptNumber}</span>
                      <span className="tnum">+{duration(slot.simulatedDelaySeconds)} cycle time</span>
                      <span className="tnum text-ink-400">{slot.realDelaySeconds}s real</span>
                      {slot.afterDeadline && <span>▲ after deadline</span>}
                    </li>
                  ))}
                </ul>
              </section>
            )}

            <section>
              <h3 className="mb-2 font-mono text-[10px] tracking-[0.14em] text-ink-300 uppercase">
                Failure text
              </h3>
              <ErrorComparison detail={data} />
            </section>

            {data.summary.state === 'DeadLettered' && (
              <RequeueForm jobId={data.summary.id} onDone={() => void refresh()} />
            )}

            <section>
              <h3 className="mb-2 font-mono text-[10px] tracking-[0.14em] text-ink-300 uppercase">Audit trail</h3>
              <ul className="space-y-1">
                {data.events.map((event) => (
                  <li key={event.id} className="flex gap-2 font-mono text-[10px] leading-relaxed">
                    <span className="tnum shrink-0 text-ink-400">
                      {new Date(event.atUtc).toLocaleTimeString()}
                    </span>
                    <span className="shrink-0 text-ink-200">{event.eventType}</span>
                    <span className="shrink-0 text-ink-400">{event.actor}</span>
                    <span className="min-w-0 text-ink-300">{event.details}</span>
                  </li>
                ))}
              </ul>
            </section>

            <details className="rounded border border-ink-700 bg-ink-950 p-2.5">
              <summary className="cursor-pointer font-mono text-[10px] tracking-wider text-ink-400 uppercase">
                Payload · idempotency key {data.summary.idempotencyKey}
              </summary>
              <pre className="mt-2 overflow-x-auto font-mono text-[10px] leading-relaxed whitespace-pre-wrap text-ink-300">
                {JSON.stringify(JSON.parse(data.payload), null, 2)}
              </pre>
            </details>
          </div>
        )}
      </div>
    </aside>
  )
}
