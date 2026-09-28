import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from './api'

export interface PollingState<T> {
  data: T | null
  error: string | null
  /** True only for the very first load, so refreshes never flash a skeleton. */
  loading: boolean
  /** Wall-clock ms when `data` was captured, used to interpolate countdowns between polls. */
  fetchedAt: number
  refresh: () => Promise<void>
}

/**
 * Polls an endpoint on an interval. Deliberately polling rather than websockets: the data is
 * a few hundred rows and a 2 second refresh is indistinguishable from live for a human
 * reading a countdown.
 */
export function usePolling<T>(fetcher: () => Promise<T>, intervalMs = 2000, enabled = true): PollingState<T> {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [fetchedAt, setFetchedAt] = useState(() => Date.now())

  const fetcherRef = useRef(fetcher)
  fetcherRef.current = fetcher

  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  const refresh = useCallback(async () => {
    try {
      const next = await fetcherRef.current()
      if (!mountedRef.current) return
      setData(next)
      setFetchedAt(Date.now())
      setError(null)
    } catch (caught) {
      if (!mountedRef.current) return
      setError(caught instanceof ApiError ? caught.message : 'Something went wrong loading this view.')
    } finally {
      if (mountedRef.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!enabled) {
      setLoading(false)
      return
    }

    void refresh()
    const handle = window.setInterval(() => void refresh(), intervalMs)
    return () => window.clearInterval(handle)
  }, [refresh, intervalMs, enabled])

  return { data, error, loading, fetchedAt, refresh }
}

/** Re-renders on an interval so countdowns tick between polls. */
export function useTicker(intervalMs = 250): number {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    const handle = window.setInterval(() => setTick((value) => value + 1), intervalMs)
    return () => window.clearInterval(handle)
  }, [intervalMs])
  return tick
}

/**
 * How much simulated time has passed since a poll. Countdowns subtract this so the clock
 * moves smoothly instead of stepping once every two seconds.
 */
export function simulatedDrift(fetchedAt: number, timeScale: number): number {
  return ((Date.now() - fetchedAt) / 1000) * timeScale
}
