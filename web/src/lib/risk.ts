import type { RiskLevel } from './types'

interface RiskStyle {
  /** A glyph as well as a word, so risk never depends on colour alone. */
  glyph: string
  label: string
  text: string
  border: string
  background: string
  rail: string
  order: number
}

export const riskStyles: Record<RiskLevel, RiskStyle> = {
  Breached: {
    glyph: '▲',
    label: 'BREACHED',
    text: 'text-signal-breach',
    border: 'border-signal-breach/50',
    background: 'bg-signal-breach/10',
    rail: 'bg-signal-breach',
    order: 0,
  },
  NeedsHuman: {
    glyph: '✋',
    label: 'NEEDS HUMAN',
    text: 'text-signal-human',
    border: 'border-signal-human/50',
    background: 'bg-signal-human/10',
    rail: 'bg-signal-human',
    order: 1,
  },
  AtRisk: {
    glyph: '◆',
    label: 'AT RISK',
    text: 'text-signal-risk',
    border: 'border-signal-risk/50',
    background: 'bg-signal-risk/10',
    rail: 'bg-signal-risk',
    order: 2,
  },
  OnTrack: {
    glyph: '›',
    label: 'ON TRACK',
    text: 'text-signal-track',
    border: 'border-signal-track/40',
    background: 'bg-signal-track/10',
    rail: 'bg-signal-track',
    order: 3,
  },
  Done: {
    glyph: '✓',
    label: 'DONE',
    text: 'text-signal-done',
    border: 'border-signal-done/40',
    background: 'bg-signal-done/10',
    rail: 'bg-signal-done',
    order: 4,
  },
}

export function riskStyle(risk: RiskLevel): RiskStyle {
  return riskStyles[risk] ?? riskStyles.OnTrack
}

export const needsAttention: RiskLevel[] = ['Breached', 'NeedsHuman', 'AtRisk']
