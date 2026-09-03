import type { ReactNode } from 'react'
import { useEffect, useMemo, useState } from 'react'
import {
  Activity,
  ArrowDownRight,
  ArrowRight,
  CalendarClock,
  Check,
  CircleAlert,
  Clock3,
  DatabaseZap,
  FileClock,
  ListChecks,
  Radio,
  Route,
  ShieldCheck,
  Sparkles,
  TerminalSquare,
} from 'lucide-react'
import { BackgroundLines } from './components/background-lines'
import type { DashboardSnapshot, SchedulerEvent, SchedulerPlan, SchedulerStatus } from './types'

const refreshInterval = 3_000

function formatDate(value: string | null | undefined) {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(value))
}

function formatTime(value: string | null | undefined) {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, {
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
  }).format(new Date(value))
}

function formatElapsed(value: string | null | undefined, now: number) {
  if (!value) return ''
  const totalSeconds = Math.max(0, Math.floor((now - new Date(value).getTime()) / 1_000))
  if (totalSeconds < 60) return `${totalSeconds}s`
  const minutes = Math.floor(totalSeconds / 60)
  if (minutes < 60) return `${minutes}m ${totalSeconds % 60}s`
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

function compactId(value: string | null | undefined) {
  if (!value) return '—'
  return value.length > 17 ? `${value.slice(0, 9)}…${value.slice(-5)}` : value
}

function statusTone(status: string) {
  if (status === 'Applied' || status === 'Waiting') return 'good'
  if (status === 'Running' || status === 'Applying' || status === 'Validated') return 'live'
  if (status === 'Failed') return 'bad'
  return 'neutral'
}

function BrandMark() {
  return (
    <div className="brand-mark" aria-label="LISS Technologies">
      <span className="brand-mark__blue" />
      <span className="brand-mark__red" />
      <span className="brand-mark__paper" />
    </div>
  )
}

function StatusPill({ status }: { status: string }) {
  return <span className={`status-pill status-pill--${statusTone(status)}`}><span />{status}</span>
}

function MetricSlab({
  label,
  value,
  detail,
  icon,
  tone,
}: {
  label: string
  value: string | number
  detail: string
  icon: ReactNode
  tone: 'blue' | 'red' | 'olive' | 'gold'
}) {
  return (
    <article className={`metric-slab metric-slab--${tone}`}>
      <div className="metric-slab__top"><span>{label}</span>{icon}</div>
      <strong>{value}</strong>
      <p>{detail}</p>
    </article>
  )
}

function CurrentOperation({ data, now }: { data: DashboardSnapshot; now: number }) {
  const operation = data.currentOperation
  return (
    <article className="operation-card brutal-card">
      <div className="section-tag"><Radio size={15} /> Live operation</div>
      {operation ? (
        <>
          <div className="operation-card__headline">
            <div>
              <span className="micro-label">Stage</span>
              <h2>{operation.stage}</h2>
            </div>
            <div className="operation-card__clock">{formatElapsed(operation.startedAt, now)}<small>elapsed</small></div>
          </div>
          <div className="operation-card__route"><Route size={19} /><span>{operation.route ?? 'Preparing route'}</span></div>
          <div className="operation-card__meta"><span>Plan</span><code title={operation.planId ?? undefined}>{compactId(operation.planId)}</code></div>
        </>
      ) : (
        <div className="operation-card__idle">
          <Check size={32} />
          <div><h2>Chain is idle.</h2><p>Standing by for the next reconciliation window.</p></div>
        </div>
      )}
    </article>
  )
}

function RouteChain({ data }: { data: DashboardSnapshot }) {
  return (
    <article className="chain-card brutal-card">
      <div className="panel-heading">
        <div><span className="micro-label">Fixed order</span><h2>Sync chain</h2></div>
        <span className="counter">{data.routes.length} routes</span>
      </div>
      <div className="route-chain">
        {data.routes.map((route, index) => (
          <div className="route-chain__segment" key={`${route.sourceVendor}-${route.targetVendor}`}>
            <div className="route-node">
              <span className="route-node__number">0{route.order}</span>
              <div><strong>{route.sourceVendor}</strong><small>{route.sourceEntityType}</small></div>
              <ArrowDownRight size={18} />
              <div><strong>{route.targetVendor}</strong><small>{route.targetEntityType}</small></div>
            </div>
            {index < data.routes.length - 1 && <ArrowRight className="route-chain__arrow" size={20} aria-hidden="true" />}
          </div>
        ))}
      </div>
    </article>
  )
}

function PlanCard({ plan }: { plan: SchedulerPlan }) {
  const resultTotal = plan.succeeded + plan.failed + plan.applySkipped
  return (
    <article className="plan-card">
      <div className="plan-card__top">
        <code title={plan.planId}>{compactId(plan.planId)}</code>
        <StatusPill status={plan.status} />
      </div>
      <h3>{plan.route}</h3>
      <div className="plan-card__numbers">
        <div><strong>{plan.total}</strong><span>total</span></div>
        <div><strong>{plan.changed}</strong><span>changed</span></div>
        <div><strong>{plan.unchanged}</strong><span>steady</span></div>
        <div><strong>{resultTotal ? `${plan.succeeded}/${resultTotal}` : '—'}</strong><span>applied</span></div>
      </div>
      <footer><span><Clock3 size={13} /> {formatDate(plan.createdAt)}</span><span>{plan.policySkipped} policy-skipped</span></footer>
    </article>
  )
}

function Plans({ plans }: { plans: SchedulerPlan[] }) {
  return (
    <section className="plans-panel brutal-card">
      <div className="panel-heading">
        <div><span className="micro-label">Latest artifacts</span><h2>Recent plans</h2></div>
        <FileClock size={27} />
      </div>
      <div className="plan-list">
        {plans.length ? plans.map((plan) => <PlanCard key={plan.planId} plan={plan} />) : <EmptyState label="No plans in this process yet." />}
      </div>
    </section>
  )
}

function RunRow({ run }: { run: SchedulerStatus }) {
  return (
    <div className="run-row">
      <StatusPill status={run.state} />
      <div className="run-row__body"><strong>{formatDate(run.lastStartedAt)}</strong><span>{run.succeeded} succeeded · {run.failed} failed · {run.applySkipped} skipped</span></div>
      <span className="run-row__total">{run.total}</span>
    </div>
  )
}

function Runs({ runs }: { runs: SchedulerStatus[] }) {
  return (
    <section className="runs-panel brutal-card">
      <div className="panel-heading panel-heading--compact">
        <div><span className="micro-label">Last 24</span><h2>Run history</h2></div>
        <ListChecks size={25} />
      </div>
      <div className="run-list">{runs.length ? runs.map((run, index) => <RunRow key={`${run.lastStartedAt}-${index}`} run={run} />) : <EmptyState label="No completed runs." />}</div>
    </section>
  )
}

function EventRow({ event }: { event: SchedulerEvent }) {
  const Icon = event.level === 'Error' ? CircleAlert : event.level === 'Warning' ? Activity : TerminalSquare
  return (
    <div className={`event-row event-row--${event.level.toLowerCase()}`}>
      <div className="event-row__icon"><Icon size={15} /></div>
      <time>{formatTime(event.timestamp)}</time>
      <p>{event.message}{event.planId && <code title={event.planId}>{compactId(event.planId)}</code>}</p>
    </div>
  )
}

function Events({ events }: { events: SchedulerEvent[] }) {
  return (
    <section className="events-panel brutal-card">
      <div className="panel-heading panel-heading--compact">
        <div><span className="micro-label">Payload-free</span><h2>Operational log</h2></div>
        <TerminalSquare size={25} />
      </div>
      <div className="event-list">{events.length ? events.map((event, index) => <EventRow key={`${event.timestamp}-${index}`} event={event} />) : <EmptyState label="No scheduler events." />}</div>
    </section>
  )
}

function EmptyState({ label }: { label: string }) {
  return <div className="empty-state"><Sparkles size={19} /><span>{label}</span></div>
}

export default function App() {
  const [data, setData] = useState<DashboardSnapshot | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [now, setNow] = useState(Date.now())

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    const refresh = async () => {
      try {
        const response = await fetch('/dashboard/data', {
          cache: 'no-store',
          headers: { Accept: 'application/json' },
          signal: controller.signal,
        })
        if (!response.ok) throw new Error(`Telemetry request failed (${response.status})`)
        const snapshot = await response.json() as DashboardSnapshot
        if (active) {
          setData(snapshot)
          setError(null)
        }
      } catch (cause) {
        if (active && !controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Telemetry request failed.')
      }
    }
    void refresh()
    const refreshTimer = window.setInterval(() => void refresh(), refreshInterval)
    const clockTimer = window.setInterval(() => setNow(Date.now()), 1_000)
    return () => {
      active = false
      controller.abort()
      window.clearInterval(refreshTimer)
      window.clearInterval(clockTimer)
    }
  }, [])

  const completionRate = useMemo(() => {
    if (!data) return '—'
    const attempted = data.current.succeeded + data.current.failed
    return attempted ? `${Math.round((data.current.succeeded / attempted) * 100)}%` : '—'
  }, [data])

  if (!data) {
    return (
      <div className="boot-screen">
        <BrandMark />
        <span className="boot-screen__kicker">LISS / ENTITYSYNC</span>
        <h1>Opening the<br />control room.</h1>
        <div className="boot-screen__bar"><span /></div>
        {error && <p>{error}</p>}
      </div>
    )
  }

  const current = data.current
  return (
    <div className="app-shell">
      <BackgroundLines>
        <header className="topbar">
          <a className="brand" href="/" aria-label="EntitySync Scheduler home"><BrandMark /><span><strong>LISS</strong><small>technologies</small></span></a>
          <div className="topbar__meta"><StatusPill status={current.state} /><span>Updated {formatTime(data.generatedAt)}</span></div>
        </header>

        <section className="hero">
          <div className="hero__copy">
            <div className="hero__index">CONTROL ROOM <span>//</span> 01</div>
            <h1>ENTITY<br /><em>SYNC</em></h1>
            <p>Four vendors. One guarded chain. Every operation visible.</p>
          </div>
          <div className={`state-stamp state-stamp--${statusTone(current.state)}`}>
            <span>Scheduler</span><strong>{current.state}</strong><small>{current.error ?? 'Systems reporting normally'}</small>
          </div>
        </section>
      </BackgroundLines>

      {error && <div className="disconnect"><CircleAlert size={17} /><span>{error}</span></div>}

      <main>
        <section className="metrics-grid" aria-label="Current scheduler metrics">
          <MetricSlab label="Success rate" value={completionRate} detail={`${current.succeeded} writes landed`} icon={<ShieldCheck size={23} />} tone="blue" />
          <MetricSlab label="Changed" value={current.changed} detail={`${current.unchanged} unchanged`} icon={<DatabaseZap size={23} />} tone="red" />
          <MetricSlab label="Policy skipped" value={current.policySkipped} detail={`${current.applySkipped} apply-skipped`} icon={<ListChecks size={23} />} tone="olive" />
          <MetricSlab label="Next run" value={current.nextRunAt ? formatTime(current.nextRunAt) : 'Manual'} detail={current.nextRunAt ? formatDate(current.nextRunAt) : 'Automatic runs disabled'} icon={<CalendarClock size={23} />} tone="gold" />
        </section>

        <section className="primary-grid"><CurrentOperation data={data} now={now} /><RouteChain data={data} /></section>
        <section className="detail-grid"><Plans plans={data.recentPlans} /><div className="detail-grid__side"><Runs runs={data.recentRuns} /><Events events={data.events} /></div></section>
      </main>

      <footer className="site-footer">
        <span><ShieldCheck size={15} /> Read-only / payload-free / process-local</span>
        <span>React 19 · Vite 8 · Aceternity motion</span>
      </footer>
    </div>
  )
}
