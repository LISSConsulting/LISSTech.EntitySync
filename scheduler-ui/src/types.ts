export type SchedulerStatus = {
  state: string
  lastStartedAt: string | null
  lastCompletedAt: string | null
  nextRunAt: string | null
  planId: string | null
  total: number
  changed: number
  unchanged: number
  policySkipped: number
  succeeded: number
  failed: number
  applySkipped: number
  error: string | null
}

export type SchedulerOperation = {
  stage: string
  route: string | null
  planId: string | null
  startedAt: string
}

export type SchedulerRoute = {
  order: number
  sourceVendor: string
  sourceEntityType: string
  targetVendor: string
  targetEntityType: string
}

export type SchedulerPlan = {
  planId: string
  route: string
  status: string
  createdAt: string
  completedAt: string | null
  total: number
  changed: number
  unchanged: number
  policySkipped: number
  succeeded: number
  failed: number
  applySkipped: number
}

export type SchedulerEvent = {
  timestamp: string
  level: string
  message: string
  planId: string | null
}

export type DashboardSnapshot = {
  generatedAt: string
  current: SchedulerStatus
  currentOperation: SchedulerOperation | null
  routes: SchedulerRoute[]
  recentRuns: SchedulerStatus[]
  recentPlans: SchedulerPlan[]
  events: SchedulerEvent[]
}
