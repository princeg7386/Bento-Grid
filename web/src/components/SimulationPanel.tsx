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
      <div className="space-y-4 p-4">
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
          className="pill pill-solid w-full py-2.5 text-sm"
        >
          <span aria-hidden="true">▶</span>
          {busy === 'seed' ? 'Seeding…' : 'Simulate last night'}
        </button>

        <div>
          <p className="mb-2 text-xs font-medium text-ink-400">Time scale {status ? `· ${status.timeScale}×` : ''}</p>
          <div className="grid grid-cols-5 gap-1.5">
            {TIME_SCALES.map((scale) => {
              const active = status?.timeScale === scale
              return (
                <button
                  key={scale}
                  type="button"
                  disabled={busy !== null}
                  onClick={() => void run('scale', async () => void (await api.setTimeScale(scale)))}
                  className={`pill py-1.5 text-xs ${
                    active ? 'bg-signal-track/15 text-signal-track' : 'pill-ghost enabled:hover:text-ink-100'
                  }`}
                >
                  {scale}×
                </button>
              )
            })}
          </div>
          <p className="mt-2 text-xs leading-relaxed text-ink-400">
            Compresses backoff waits. Deadlines already written keep the scale they were created with.
          </p>
        </div>

        <div className="grid gap-2 sm:grid-cols-2">
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
            className="pill pill-ghost py-2 text-xs enabled:hover:border-signal-breach/50 enabled:hover:text-signal-breach"
          >
            <span aria-hidden="true">✖</span> Kill a worker mid-job
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
            className="pill pill-ghost py-2 text-xs"
          >
            <span aria-hidden="true">↺</span> Reset
          </button>
        </div>

        {message && (
          <p
            className={`rounded-xl px-3 py-2 text-xs leading-relaxed ${
              message.tone === 'ok' ? 'bg-signal-done/10 text-signal-done' : 'bg-signal-breach/10 text-signal-breach'
            }`}
            role="status"
          >
            {message.text}
          </p>
        )}

        {scenario && (
          <dl className="grid grid-cols-2 gap-x-3 gap-y-1.5 border-t border-white/[0.06] pt-3 text-xs">
            <dt className="text-ink-400">state-b-mmis recovers</dt>
            <dd className="tnum text-right text-ink-200">
              {new Date(scenario.outageRecoversAtUtc).toLocaleTimeString()}
            </dd>
            <dt className="text-ink-400">Abandoned payment job</dt>
            <dd className="tnum text-right text-ink-200">#{scenario.orphanedPaymentJobId}</dd>
          </dl>
        )}

        <p className="border-t border-white/[0.06] pt-3 text-xs leading-relaxed text-ink-400">
          Every job, member and dollar figure in CycleGuard is invented. Nothing here comes from a real payer,
          provider or person.
        </p>
      </div>
    </Panel>
  )
}
