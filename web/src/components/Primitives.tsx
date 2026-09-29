import type { ReactNode } from 'react'
import { riskStyle } from '../lib/risk'
import type { RiskLevel } from '../lib/types'

export function RiskBadge({ risk, compact = false }: { risk: RiskLevel; compact?: boolean }) {
  const style = riskStyle(risk)
  return (
    <span
      className={`inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-semibold ${style.text} ${style.border} ${style.background}`}
      title={style.label}
    >
      <span aria-hidden="true">{style.glyph}</span>
      {/* The label is visible unless compact, so the screen-reader copy is only needed
          when it is hidden. Rendering both announced it twice. */}
      {compact ? <span className="sr-only">{style.label}</span> : <span>{style.label}</span>}
    </span>
  )
}

export function Panel({
  title,
  subtitle,
  actions,
  children,
  className = '',
}: {
  title: string
  subtitle?: string
  actions?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={`flex min-h-0 flex-col rounded-2xl border border-ink-800 bg-ink-900 ${className}`}>
      <header className="flex items-center justify-between gap-3 px-4 py-3.5">
        <div className="flex items-baseline gap-2.5">
          <h2 className="text-[15px] font-bold tracking-tight text-ink-100">{title}</h2>
          {subtitle && <span className="text-xs text-ink-400">{subtitle}</span>}
        </div>
        {actions}
      </header>
      <div className="min-h-0 flex-1 overflow-auto">{children}</div>
    </section>
  )
}

export function LoadingRows({ rows = 6, label = 'Loading' }: { rows?: number; label?: string }) {
  return (
    <div className="space-y-2 p-4" role="status" aria-live="polite">
      <span className="sr-only">{label}</span>
      {Array.from({ length: rows }).map((_, index) => (
        <div key={index} className="relative h-8 overflow-hidden rounded-xl bg-ink-850">
          <div className="cg-sweep absolute inset-y-0 w-1/4 bg-gradient-to-r from-transparent via-ink-700/60 to-transparent" />
        </div>
      ))}
    </div>
  )
}

export function EmptyState({ title, hint }: { title: string; hint?: string }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-1.5 px-6 py-10 text-center">
      <p className="text-sm font-semibold text-ink-200">{title}</p>
      {hint && <p className="max-w-sm text-xs leading-relaxed text-ink-400">{hint}</p>}
    </div>
  )
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 px-6 py-10 text-center" role="alert">
      <p className="flex items-center gap-1.5 text-sm font-bold text-signal-breach">
        <span aria-hidden="true">▲</span> Request failed
      </p>
      <p className="max-w-md text-xs leading-relaxed text-ink-200">{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="pill pill-ghost px-4 py-1.5 text-xs">
          Retry
        </button>
      )}
    </div>
  )
}

export function Metric({
  label,
  value,
  tone = 'default',
  hint,
}: {
  label: string
  value: string
  tone?: 'default' | 'warn' | 'bad' | 'good'
  hint?: string
}) {
  const toneClass =
    tone === 'bad'
      ? 'text-signal-breach'
      : tone === 'warn'
        ? 'text-signal-risk'
        : tone === 'good'
          ? 'text-signal-done'
          : 'text-ink-100'

  return (
    <div className="flex flex-col gap-0.5" title={hint}>
      <span className="text-xs font-medium text-ink-400">{label}</span>
      <span className={`tnum text-lg font-bold ${toneClass}`}>{value}</span>
    </div>
  )
}
