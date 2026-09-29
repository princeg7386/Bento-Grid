import { useMemo } from 'react'
import { money } from '../lib/format'
import type { JobSummary, Outage, RootCauseGroup } from '../lib/types'
import { EmptyState, LoadingRows, Panel } from './Primitives'

/** The fixed set of downstream systems every job in CycleGuard talks to. */
const ENDPOINTS = ['claims-engine', 'state-a-mmis', 'state-b-mmis', 'ach-gateway'] as const

type Endpoint = (typeof ENDPOINTS)[number]
type NodeStatus = 'down' | 'attention' | 'healing' | 'ok'

interface NodeState {
  endpoint: Endpoint
  status: NodeStatus
  jobCount: number
  dollarsAtRiskCents: number
  secondsRemaining: number | null
}

const STATUS_STYLE: Record<NodeStatus, { ring: string; glow: string; dot: string; label: string; pulse: boolean; speed: number; dots: number }> = {
  down: { ring: '#ff5a5f', glow: 'rgba(255,90,95,0.35)', dot: '#ff5a5f', label: 'Down', pulse: true, speed: 0, dots: 0 },
  attention: { ring: '#ffb020', glow: 'rgba(255,176,32,0.25)', dot: '#ffb020', label: 'Parked', pulse: false, speed: 5.5, dots: 1 },
  healing: { ring: '#34d399', glow: 'rgba(52,211,153,0.3)', dot: '#ffb020', label: 'Healing', pulse: true, speed: 3.2, dots: 2 },
  ok: { ring: '#3c3c38', glow: 'transparent', dot: '#34d399', label: 'OK', pulse: false, speed: 2.2, dots: 3 },
}

/** Hub-and-spoke node positions in the SVG's own coordinate space. */
const HUB = { x: 70, y: 130 }
const NODE_X = 690
const NODE_Y: Record<Endpoint, number> = {
  'claims-engine': 35,
  'state-a-mmis': 95,
  'state-b-mmis': 165,
  'ach-gateway': 225,
}

function pathFor(endpoint: Endpoint): string {
  const y = NODE_Y[endpoint]
  return `M ${HUB.x} ${HUB.y} Q ${(HUB.x + NODE_X) / 2} ${y} ${NODE_X} ${y}`
}

function useNodeStates(jobs: JobSummary[], outages: Outage[], groups: RootCauseGroup[]): NodeState[] {
  return useMemo(
    () =>
      ENDPOINTS.map((endpoint) => {
        const outage = outages.find((o) => o.endpoint === endpoint)
        const endpointGroups = groups.filter((g) => g.downstreamEndpoint === endpoint)
        const endpointJobs = jobs.filter((j) => j.downstreamEndpoint === endpoint && j.risk !== 'Done')

        const status: NodeStatus = outage
          ? 'down'
          : endpointGroups.some((g) => !g.healing)
            ? 'attention'
            : endpointGroups.some((g) => g.healing)
              ? 'healing'
              : 'ok'

        return {
          endpoint,
          status,
          jobCount: endpointJobs.length,
          dollarsAtRiskCents: endpointJobs.reduce((sum, j) => sum + j.amountAtStakeCents, 0),
          secondsRemaining: outage?.secondsRemaining ?? null,
        }
      }),
    [jobs, outages, groups],
  )
}

export function SystemMap({
  jobs,
  outages,
  groups,
  loading,
}: {
  jobs: JobSummary[]
  outages: Outage[]
  groups: RootCauseGroup[]
  loading: boolean
}) {
  const nodes = useNodeStates(jobs, outages, groups)
  const anyScenario = jobs.length > 0

  return (
    <Panel title="System map" subtitle="live traffic to every downstream endpoint">
      {loading && !anyScenario ? (
        <LoadingRows rows={3} label="Loading system map" />
      ) : !anyScenario ? (
        <EmptyState title="Nothing flowing yet" hint="Run &ldquo;Simulate last night&rdquo; to see live traffic." />
      ) : (
        <div className="p-4">
          <svg viewBox="0 0 900 260" className="h-auto w-full" role="img" aria-label="Live map of job traffic to every downstream system">
            {/* paths, drawn first so nodes and dots sit on top */}
            {ENDPOINTS.map((endpoint) => (
              <path key={`path-${endpoint}`} d={pathFor(endpoint)} fill="none" stroke="#2a2a27" strokeWidth={2} />
            ))}

            {/* flowing traffic dots -- native SVG animation, no JS loop needed */}
            {nodes.map((node) => {
              const style = STATUS_STYLE[node.status]
              const d = pathFor(node.endpoint)
              return Array.from({ length: style.dots }).map((_, index) => (
                <circle key={`dot-${node.endpoint}-${index}`} r={4} fill={style.dot}>
                  <animateMotion
                    dur={`${style.speed}s`}
                    begin={`${(index * style.speed) / Math.max(style.dots, 1)}s`}
                    repeatCount="indefinite"
                    path={d}
                  />
                </circle>
              ))
            })}

            {/* the queue itself */}
            <circle cx={HUB.x} cy={HUB.y} r={30} fill="#1a1a18" stroke="#3c3c38" strokeWidth={2} />
            <circle cx={HUB.x} cy={HUB.y} r={38} fill="none" stroke="#3c3c38" strokeWidth={1.5} opacity={0.4}>
              <animate attributeName="r" values="30;42;30" dur="2.4s" repeatCount="indefinite" />
              <animate attributeName="opacity" values="0.5;0;0.5" dur="2.4s" repeatCount="indefinite" />
            </circle>
            <text x={HUB.x} y={HUB.y - 4} textAnchor="middle" fill="#ededea" fontSize="11" fontWeight="700">
              Queue
            </text>
            <text x={HUB.x} y={HUB.y + 11} textAnchor="middle" fill="#9d9d97" fontSize="9">
              4 workers
            </text>

            {/* endpoint nodes */}
            {nodes.map((node) => {
              const style = STATUS_STYLE[node.status]
              const y = NODE_Y[node.endpoint]
              return (
                <g key={node.endpoint}>
                  {style.pulse && (
                    <circle cx={NODE_X} cy={y} r={26} fill="none" stroke={style.ring} strokeWidth={2} opacity={0.5}>
                      <animate attributeName="r" values="26;40;26" dur="1.6s" repeatCount="indefinite" />
                      <animate attributeName="opacity" values="0.55;0;0.55" dur="1.6s" repeatCount="indefinite" />
                    </circle>
                  )}
                  <circle cx={NODE_X} cy={y} r={26} fill="#1a1a18" stroke={style.ring} strokeWidth={2.5} />
                  <text x={NODE_X} y={y + 4} textAnchor="middle" fill={style.ring} fontSize="14" fontWeight="700">
                    {node.status === 'down' ? '▲' : node.status === 'attention' ? '✋' : node.status === 'healing' ? '↻' : '✓'}
                  </text>
                  <text x={NODE_X + 40} y={y - 8} fill="#ededea" fontSize="11" fontWeight="700">
                    {node.endpoint}
                  </text>
                  <text x={NODE_X + 40} y={y + 5} fill="#9d9d97" fontSize="10">
                    {node.status === 'down'
                      ? `Down · back in ${node.secondsRemaining}s`
                      : node.jobCount === 0
                        ? 'Nothing needs attention'
                        : `${node.jobCount} job${node.jobCount === 1 ? '' : 's'} · ${money(node.dollarsAtRiskCents)}`}
                  </text>
                  <text
                    x={NODE_X + 40}
                    y={y + 18}
                    fill={style.ring === '#3c3c38' ? '#757570' : style.ring}
                    fontSize="9"
                    fontWeight="600"
                  >
                    {style.label}
                  </text>
                </g>
              )
            })}
          </svg>
          <p className="mt-1 text-center text-xs text-ink-400">
            Dots show traffic direction and pace, not individual jobs — speed and colour track each endpoint's status.
          </p>
        </div>
      )}
    </Panel>
  )
}
