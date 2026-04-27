import { useState, useEffect, useCallback } from 'react'
import { useParams, Link } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, AlertCircle, Loader2, X } from 'lucide-react'
import { getGoalDetail, streamTaskEvents } from '../api/client'
import type { GoalDetailResponse, TaskSummary } from '../api/types'
import WorkflowGraph from '../components/WorkflowGraph'

const DEFAULT_STORE = 'default'

// ── Status badge (reused from WorkflowsPage pattern) ────────────────────────

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: 'bg-blue-100 text-blue-700',
    complete: 'bg-green-100 text-green-700',
    paused: 'bg-yellow-100 text-yellow-700',
    failed: 'bg-red-100 text-red-700',
    done: 'bg-green-100 text-green-700',
    pending: 'bg-gray-100 text-gray-600',
    blocked: 'bg-orange-100 text-orange-700',
    running: 'bg-blue-100 text-blue-700',
  }
  return (
    <span
      className={`text-xs px-2 py-0.5 rounded-full font-medium ${
        styles[status] ?? 'bg-gray-100 text-gray-600'
      }`}
    >
      {status}
    </span>
  )
}

// ── Progress bar ─────────────────────────────────────────────────────────────

function ProgressBar({ percent }: { percent: number }) {
  const clamped = Math.min(Math.max(percent, 0), 100)
  const barColor =
    clamped >= 100 ? 'bg-green-500' : clamped > 0 ? 'bg-blue-500' : 'bg-gray-300'

  return (
    <div className="flex items-center gap-3">
      <div className="flex-1 h-2.5 bg-gray-200 rounded-full overflow-hidden">
        <div
          className={`h-full rounded-full transition-all duration-500 ${barColor}`}
          style={{ width: `${clamped}%` }}
        />
      </div>
      <span className="text-sm font-medium text-gray-600 w-10 text-right">{percent}%</span>
    </div>
  )
}

// ── Task detail side panel ───────────────────────────────────────────────────

function TaskPanel({ task, onClose }: { task: TaskSummary; onClose: () => void }) {
  return (
    <div className="w-80 bg-white border-l border-gray-200 overflow-y-auto shrink-0">
      <div className="flex items-center justify-between p-4 border-b border-gray-100">
        <h3 className="text-sm font-semibold text-gray-800">Task Details</h3>
        <button
          onClick={onClose}
          className="p-1 rounded hover:bg-gray-100 text-gray-400 hover:text-gray-600 transition-colors"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
      <div className="p-4 space-y-4">
        <div>
          <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Name</label>
          <p className="text-sm text-gray-800 font-mono mt-0.5 break-all">{task.name}</p>
        </div>

        <div>
          <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Status</label>
          <div className="mt-1">
            <StatusBadge status={task.status} />
          </div>
        </div>

        <div>
          <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Wave</label>
          <p className="text-sm text-gray-700 mt-0.5">{task.wave}</p>
        </div>

        {task.gateType && (
          <div>
            <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Gate Type</label>
            <p className="text-sm text-gray-700 mt-0.5">{task.gateType}</p>
          </div>
        )}

        {task.skill && (
          <div>
            <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Skill</label>
            <p className="text-sm text-gray-700 font-mono mt-0.5">{task.skill}</p>
          </div>
        )}

        {task.dependsOn.length > 0 && (
          <div>
            <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">Depends On</label>
            <ul className="mt-1 space-y-1">
              {task.dependsOn.map((dep) => (
                <li key={dep} className="text-xs text-gray-600 font-mono bg-gray-50 px-2 py-1 rounded">
                  {dep}
                </li>
              ))}
            </ul>
          </div>
        )}

        {task.description && (
          <div>
            <label className="text-xs font-medium text-gray-500 uppercase tracking-wide">
              Description
            </label>
            <p className="text-sm text-gray-700 mt-0.5 whitespace-pre-wrap">{task.description}</p>
          </div>
        )}
      </div>
    </div>
  )
}

// ── Main page ────────────────────────────────────────────────────────────────

export default function WorkflowDetailPage() {
  const { goalId } = useParams<{ goalId: string }>()
  const queryClient = useQueryClient()
  const [selectedTask, setSelectedTask] = useState<TaskSummary | null>(null)

  // Fetch goal detail
  const {
    data: goal,
    isLoading,
    error,
  } = useQuery({
    queryKey: ['goalDetail', goalId],
    queryFn: () => getGoalDetail(DEFAULT_STORE, goalId!),
    enabled: !!goalId,
  })

  // SSE subscription for live task status updates
  useEffect(() => {
    if (!goalId) return

    const controller = new AbortController()

    streamTaskEvents(
      DEFAULT_STORE,
      goalId,
      (event) => {
        queryClient.setQueryData<GoalDetailResponse>(['goalDetail', goalId], (old) => {
          if (!old) return old
          return {
            ...old,
            phases: old.phases.map((phase) => ({
              ...phase,
              tasks: phase.tasks.map((task) =>
                task.name === event.taskName ? { ...task, status: event.newStatus } : task,
              ),
            })),
          }
        })
      },
      controller.signal,
    ).catch((err) => {
      // AbortError is expected on cleanup
      if (err instanceof Error && err.name !== 'AbortError') {
        console.warn('SSE connection error:', err.message)
      }
    })

    return () => controller.abort()
  }, [goalId, queryClient])

  const handleNodeClick = useCallback((task: TaskSummary) => {
    setSelectedTask(task)
  }, [])

  // ── Loading / error states ───────────────────────────────────────────────

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="flex items-center gap-2 text-sm text-gray-400">
          <Loader2 className="w-4 h-4 animate-spin" />
          Loading goal detail...
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="p-6 max-w-4xl mx-auto">
        <Link to="/workflows" className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
          <ArrowLeft className="w-4 h-4" />
          Back to Workflows
        </Link>
        <div className="flex items-center gap-2 text-sm text-red-500">
          <AlertCircle className="w-4 h-4" />
          {error instanceof Error ? error.message : 'Failed to load goal detail'}
        </div>
      </div>
    )
  }

  if (!goal) return null

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <div className="shrink-0 border-b border-gray-200 bg-white px-6 py-4">
        <Link to="/workflows" className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-3">
          <ArrowLeft className="w-4 h-4" />
          Back to Workflows
        </Link>

        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-3 mb-1">
              <h2 className="text-lg font-semibold text-gray-900 truncate">{goal.id}</h2>
              <StatusBadge status={goal.status} />
            </div>
            {goal.description && (
              <p className="text-sm text-gray-600 mb-3">{goal.description}</p>
            )}
            <div className="max-w-md">
              <ProgressBar percent={goal.progressPercent} />
            </div>
          </div>

          {goal.workflowRef && (
            <div className="text-xs text-gray-500 shrink-0 pt-1">
              Workflow: <span className="font-mono">{goal.workflowRef}</span>
            </div>
          )}
        </div>
      </div>

      {/* Graph + optional side panel */}
      <div className="flex flex-1 min-h-0">
        <div className="flex-1 min-w-0">
          {goal.phases.length > 0 ? (
            <WorkflowGraph phases={goal.phases} onNodeClick={handleNodeClick} />
          ) : (
            <div className="flex items-center justify-center h-full text-sm text-gray-400">
              No tasks yet — waiting for planner to generate tasks.
            </div>
          )}
        </div>

        {selectedTask && (
          <TaskPanel task={selectedTask} onClose={() => setSelectedTask(null)} />
        )}
      </div>
    </div>
  )
}
