export type RiskLevel = 'Breached' | 'NeedsHuman' | 'AtRisk' | 'OnTrack' | 'Done'

export type JobState = 'Queued' | 'Running' | 'RetryScheduled' | 'Succeeded' | 'DeadLettered' | 'Cancelled'

export interface JobSummary {
  id: number
  type: string
  state: JobState
  risk: RiskLevel
  riskReasonCode: string
  riskReason: string
  secondsToDeadline: number
  simulatedSecondsToDeadline: number
  deadlineUtc: string
  amountAtStakeCents: number
  attempts: number
  maxAttempts: number
  downstreamEndpoint: string
  secondsToNextAttempt: number | null
  simulatedSecondsToNextAttempt: number | null
  failureSignature: string | null
  failureClass: string | null
  cause: string | null
  suggestedAction: string | null
  idempotencyKey: string
  requeueCount: number
  deadLetterResolved: boolean
  isSynthetic: boolean
}

export interface Attempt {
  id: number
  attemptNumber: number
  workerId: string
  startedUtc: string
  finishedUtc: string | null
  outcome: string | null
  failureSignature: string | null
  failureClass: string | null
  errorMasked: string | null
  stackTraceMasked: string | null
  backoffSeconds: number | null
  nextAttemptUtc: string | null
  errorRaw: string | null
  stackTraceRaw: string | null
}

export interface BackoffSlot {
  attemptNumber: number
  simulatedDelaySeconds: number
  realDelaySeconds: number
  projectedRunUtc: string | null
  afterDeadline: boolean
}

export interface AuditEvent {
  id: number
  atUtc: string
  actor: string
  eventType: string
  details: string
}

export interface JobDetail {
  summary: JobSummary
  payload: string
  attempts: Attempt[]
  upcomingBackoff: BackoffSlot[]
  events: AuditEvent[]
  lastErrorMasked: string | null
  lastStackTraceMasked: string | null
  lastErrorRaw: string | null
  lastStackTraceRaw: string | null
  rawErrorsAvailable: boolean
}

export interface Outage {
  endpoint: string
  recoversAtUtc: string
  secondsRemaining: number
}

export interface Status {
  scenarioLoaded: boolean
  cycleCloseUtc: string | null
  secondsToCycleClose: number
  simulatedSecondsToCycleClose: number
  timeScale: number
  totalJobs: number
  byState: Record<string, number>
  byRisk: Record<string, number>
  needingAttention: number
  dollarsAtRiskCents: number
  dollarsCompletedCents: number
  deadLetterCount: number
  duplicatesPreventedCount: number
  duplicatesPreventedCents: number
  throughputPerMinute: number
  successRatePercent: number
  retryRatePercent: number
  workerCount: number
  activeOutages: Outage[]
}

export interface RootCauseGroup {
  downstreamEndpoint: string
  failureSignature: string
  failureClass: string
  cause: string
  suggestedAction: string
  jobCount: number
  dollarsAtRiskCents: number
  earliestDeadlineUtc: string | null
  secondsToEarliestDeadline: number | null
  breachedCount: number
  needsHumanCount: number
  healing: boolean
}

export interface DeadLetterGroup {
  failureSignature: string
  cause: string
  suggestedAction: string
  failureClass: string
  jobCount: number
  dollarsAtRiskCents: number
  jobs: JobSummary[]
}

export interface ScenarioResult {
  jobsCreated: number
  cycleCloseUtc: string
  timeScale: number
  permanentFailures: number
  outageStalledJobs: number
  intermittentGatewayJobs: number
  duplicateSubmissions: number
  orphanedPaymentJobId: number
  outageRecoversAtUtc: string
}
