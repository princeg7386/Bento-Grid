/** Cents to a compact dollar string. Money is never floated, only formatted. */
export function money(cents: number): string {
  const dollars = cents / 100
  if (Math.abs(dollars) >= 1_000_000) return `$${(dollars / 1_000_000).toFixed(2)}M`
  if (Math.abs(dollars) >= 1_000) return `$${(dollars / 1_000).toFixed(1)}k`
  return `$${dollars.toFixed(2)}`
}

export function moneyExact(cents: number): string {
  return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' })
}

/** hh:mm:ss for a countdown. Negative renders with a leading minus, never wrapping around. */
export function clock(seconds: number): string {
  const negative = seconds < 0
  const total = Math.floor(Math.abs(seconds))
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const secs = total % 60
  const body = `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
  return negative ? `-${body}` : body
}

/** Short human duration, for dense table cells. */
export function duration(seconds: number): string {
  const negative = seconds < 0
  const total = Math.floor(Math.abs(seconds))
  let text: string
  if (total >= 3600) text = `${Math.floor(total / 3600)}h ${Math.floor((total % 3600) / 60)}m`
  else if (total >= 60) text = `${Math.floor(total / 60)}m ${total % 60}s`
  else text = `${total}s`
  return negative ? `${text} ago` : text
}

export function jobTypeLabel(type: string): string {
  switch (type) {
    case 'ClaimsBatchAdjudication':
      return 'Claims adjudication'
    case 'EncounterSubmission':
      return 'Encounter submission'
    case 'PaymentRunDisbursement':
      return 'Payment disbursement'
    default:
      return type
  }
}

export function stateLabel(state: string): string {
  switch (state) {
    case 'RetryScheduled':
      return 'Retry scheduled'
    case 'DeadLettered':
      return 'Dead-lettered'
    default:
      return state
  }
}

export function signatureLabel(signature: string): string {
  return signature.replace(/_/g, ' ')
}
