import type {
  DeadLetterGroup,
  JobDetail,
  JobSummary,
  MorningReport,
  RootCauseGroup,
  ScenarioResult,
  Status,
} from './types'

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    })
  } catch {
    // A dead API is the common case during a demo, so say so plainly.
    throw new ApiError('Cannot reach the CycleGuard API. Is the backend running on port 5179?', 0)
  }

  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`
    let explained = false
    try {
      const body = (await response.json()) as { error?: string }
      if (body?.error) {
        detail = body.error
        explained = true
      }
    } catch {
      // Not every error response carries JSON.
    }

    // The dev server proxies /api, so a backend that is not listening surfaces here as a
    // bodyless 5xx rather than a failed fetch. "500 Internal Server Error" sends people
    // hunting through API logs that do not exist.
    if (!explained && response.status >= 500) {
      detail = `The API answered ${response.status} with no detail. If the backend is not running, start it with ./run.sh or on port 5179.`
    }

    throw new ApiError(detail, response.status)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export interface JobFilters {
  state?: string
  risk?: string
  endpoint?: string
  limit?: number
}

export const api = {
  status: () => request<Status>('/api/status'),

  jobs: (filters: JobFilters = {}) => {
    const params = new URLSearchParams()
    if (filters.state) params.set('state', filters.state)
    if (filters.risk) params.set('risk', filters.risk)
    if (filters.endpoint) params.set('endpoint', filters.endpoint)
    if (filters.limit) params.set('limit', String(filters.limit))
    const query = params.toString()
    return request<JobSummary[]>(`/api/jobs${query ? `?${query}` : ''}`)
  },

  job: (id: number) => request<JobDetail>(`/api/jobs/${id}`),

  groups: () => request<RootCauseGroup[]>('/api/groups'),

  deadLetters: () => request<DeadLetterGroup[]>('/api/deadletters'),

  morningReport: () => request<MorningReport>('/api/morning-report'),

  requeue: (id: number, analyst: string, note: string) =>
    request<{ requeued: boolean }>(`/api/jobs/${id}/requeue`, {
      method: 'POST',
      body: JSON.stringify({ analyst, note }),
    }),

  seedLastNight: () => request<ScenarioResult>('/api/demo/scenarios/last-night', { method: 'POST' }),

  reset: () => request<{ reset: boolean }>('/api/demo/reset', { method: 'POST' }),

  setTimeScale: (timeScale: number) =>
    request<{ timeScale: number }>('/api/demo/timescale', {
      method: 'POST',
      body: JSON.stringify({ timeScale }),
    }),

  killWorker: () =>
    request<{ killed: boolean; jobId: number | null; worker: string | null }>('/api/demo/kill-worker', {
      method: 'POST',
    }),
}
