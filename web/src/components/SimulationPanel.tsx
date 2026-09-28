import { useState } from 'react'
import { api } from '../lib/api'
import type { ScenarioResult, Status } from '../lib/types'
import { Panel } from './Primitives'

const TIME_SCALES = [1, 10, 60, 120, 600]

export function SimulationPanel({
  status,
  onChanged,
}: {
  status: Status | null
  onChanged: () => void
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<{ tone: 'ok' | 'bad'; text: string } | null>(null)
  const [scenario, setScenario] = useState<ScenarioResult | null>(null)

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label)
    setMessage(null)
    try {
      await action()
      onChanged()
    } catch (caught) {
      setMessage({ tone: 'bad', text: caught instanceof Error ? caught.message : 'That did not work.' })
    } finally {
      setBusy(null)
    }
  }

  return (
    <Panel title="Simulation" subtitle="synthetic data only">
      <div className="space-y-3 p-3">
        <button
          type="button"
          disabled={busy !== null}
          onClick={() =>
            void run('seed', async () => {
              const result = await api.seedLastNight()
              setScenario(result)
              setMessage({
                tone: 'ok',
                text: `Seeded ${result.jobsCreated} jobs: ${result.outageStalledJobs} stalled behind state-b-mmis, ${result.permanentFailures} permanent failures, ${result.duplicateSubmissions} duplicate submissions, 1 abandoned payment job.`,
              })
            })
          }
          className="w-full rounded border border-signal-risk/60 bg-signal-risk/10 px-3 py-2 font-mono text-[11px] font-semibold tracking-wide text-signal-risk transition enabled:hover:bg-signal-risk/20 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {busy === 'seed' ? 'Seeding…' : '▶ Simulate last night'}
        </button>

        <div>
          <p className="mb-1.5 font-mono text-[10px] tracking-[0.12em] text-ink-400 uppercase">
            Time scale {status ? `· ${status.timeScale}×` : ''}
          </p>
          <div className="grid grid-cols-5 gap-1">
            {TIME_SCALES.map((scale) => {
              const active = status?.timeScale === scale
              return (
                <button
                  key={scale}
                  type="button"
                  disabled={busy !== null}
                  onClick={() => void run('scale', async () => void (await api.setTimeScale(scale)))}
                  className={`rounded border px-1 py-1 font-mono text-[10px] transition disabled:opacity-50 ${
                    active
                      ? 'border-signal-track/60 bg-signal-track/15 text-signal-track'
                      : 'border-ink-700 bg-ink-850 text-ink-200 enabled:hover:border-ink-500'
                  }`}
                >
                  {scale}×
                </button>
              )
            })}
          </div>
          <p className="mt-1.5 text-[10px] leading-relaxed text-ink-400">
            Compresses backoff waits. Deadlines already written keep the scale they were created with.
          </p>
        </div>

        <div className="grid gap-1.5 sm:grid-cols-2">
          <button
            type="button"
            disabled={busy !== null}
            onClick={() =>
              void run('kill', async () => {
                const result = await api.killWorker()
                setMessage({
                  tone: 'ok',
                  text: `Killed ${result.worker ?? 'a worker'} mid-job #${result.jobId}. The reaper will hand it to another worker within a second.`,
                })
              })
            }
            className="rounded border border-ink-700 bg-ink-850 px-2 py-1.5 font-mono text-[10px] text-ink-100 transition enabled:hover:border-signal-breach/50 enabled:hover:bg-signal-breach/10 disabled:opacity-50"
          >
            {busy === 'kill' ? 'Killing…' : '✖ Kill a worker mid-job'}
          </button>

          <button
            type="button"
            disabled={busy !== null}
            onClick={() =>
              void run('reset', async () => {
                await api.reset()
                setScenario(null)
                setMessage({ tone: 'ok', text: 'Cleared. Nothing in the queue.' })
              })
            }
            className="rounded border border-ink-700 bg-ink-850 px-2 py-1.5 font-mono text-[10px] text-ink-100 transition enabled:hover:border-ink-500 disabled:opacity-50"
          >
            {busy === 'reset' ? 'Resetting…' : '↺ Reset'}
          </button>
        </div>

        {message && (
          <p
            className={`rounded border px-2 py-1.5 font-mono text-[10px] leading-relaxed ${
              message.tone === 'ok'
                ? 'border-signal-done/30 bg-signal-done/5 text-signal-done'
                : 'border-signal-breach/40 bg-signal-breach/5 text-signal-breach'
            }`}
            role="status"
          >
            {message.text}
          </p>
        )}

        {scenario && (
          <dl className="grid grid-cols-2 gap-x-3 gap-y-1 border-t border-ink-800 pt-2 font-mono text-[10px]">
            <dt className="text-ink-400">state-b-mmis recovers</dt>
            <dd className="tnum text-right text-ink-200">
              {new Date(scenario.outageRecoversAtUtc).toLocaleTimeString()}
            </dd>
            <dt className="text-ink-400">abandoned payment job</dt>
            <dd className="tnum text-right text-ink-200">#{scenario.orphanedPaymentJobId}</dd>
          </dl>
        )}

        <p className="border-t border-ink-800 pt-2 text-[10px] leading-relaxed text-ink-400">
          Every job, member and dollar figure in CycleGuard is invented. Nothing here comes from a real payer,
          provider or person.
        </p>
      </div>
    </Panel>
  )
}
