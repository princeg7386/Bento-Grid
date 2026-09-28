import { useState } from 'react'
import { api } from '../lib/api'

/**
 * "Check status tomorrow morning" taken literally: reads the monitor's own report text aloud
 * using the browser's built-in speech synthesis. No backend change, no AI narration -- it
 * speaks exactly the deterministic sentence GET /api/morning-report already produced.
 */
export function MorningReportSpeaker() {
  const [state, setState] = useState<'idle' | 'loading' | 'speaking' | 'error'>('idle')

  async function toggle() {
    if (state === 'speaking') {
      window.speechSynthesis.cancel()
      setState('idle')
      return
    }

    if (!('speechSynthesis' in window)) {
      setState('error')
      return
    }

    setState('loading')
    try {
      const report = await api.morningReport()
      const utterance = new SpeechSynthesisUtterance(`${report.headline} ${report.summary}`)
      utterance.rate = 1.02
      utterance.onend = () => setState('idle')
      utterance.onerror = () => setState('idle')

      window.speechSynthesis.cancel() // clear anything queued before speaking the new one
      window.speechSynthesis.speak(utterance)
      setState('speaking')
    } catch {
      setState('error')
    }
  }

  return (
    <button
      type="button"
      onClick={() => void toggle()}
      disabled={state === 'loading'}
      title="Read the unattended monitor's morning report aloud"
      className={`inline-flex items-center gap-1.5 rounded border px-2 py-1 font-mono text-[10px] tracking-wider uppercase transition disabled:opacity-50 ${
        state === 'speaking'
          ? 'border-signal-track/60 bg-signal-track/10 text-signal-track'
          : 'border-ink-700 bg-ink-900 text-ink-300 hover:border-ink-500'
      }`}
    >
      <span aria-hidden="true">{state === 'speaking' ? '⏹' : '🔊'}</span>
      {state === 'loading' ? 'loading…' : state === 'speaking' ? 'stop' : state === 'error' ? 'unavailable' : 'read report'}
    </button>
  )
}
