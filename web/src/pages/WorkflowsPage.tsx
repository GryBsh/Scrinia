import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { GitBranch, Target, AlertCircle, Loader2 } from 'lucide-react'
import { listWorkflows, listGoals } from '../api/client'

const DEFAULT_STORE = 'default'

export default function WorkflowsPage() {
  const {
    data: workflowData,
    isLoading: workflowsLoading,
    error: workflowsError,
  } = useQuery({
    queryKey: ['workflows', DEFAULT_STORE],
    queryFn: () => listWorkflows(DEFAULT_STORE),
  })

  const {
    data: goalData,
    isLoading: goalsLoading,
    error: goalsError,
  } = useQuery({
    queryKey: ['goals', DEFAULT_STORE],
    queryFn: () => listGoals(DEFAULT_STORE),
  })

  return (
    <div className="p-6 max-w-5xl mx-auto">
      <h2 className="text-xl font-semibold mb-6">Workflows</h2>

      {/* Workflows section */}
      <section className="mb-10">
        <h3 className="text-sm font-medium text-gray-500 mb-3">Available Workflows</h3>
        {workflowsLoading && (
          <div className="flex items-center gap-2 text-sm text-gray-400">
            <Loader2 className="w-4 h-4 animate-spin" />
            Loading workflows...
          </div>
        )}
        {workflowsError && (
          <div className="flex items-center gap-2 text-sm text-red-500">
            <AlertCircle className="w-4 h-4" />
            {workflowsError instanceof Error ? workflowsError.message : 'Failed to load workflows'}
          </div>
        )}
        {workflowData && workflowData.workflows.length === 0 && (
          <p className="text-gray-400 text-sm">No workflows found.</p>
        )}
        {workflowData && workflowData.workflows.length > 0 && (
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            {workflowData.workflows.map((wf) => (
              <Link
                key={wf.name}
                to={`/workflows/editor/${encodeURIComponent(wf.name)}`}
                className="bg-white rounded-lg border p-4 hover:border-blue-300 hover:shadow-sm transition-all block"
              >
                <div className="flex items-start justify-between mb-2">
                  <div className="flex items-center gap-2">
                    <div className="p-2 bg-purple-50 rounded-lg">
                      <GitBranch className="w-5 h-5 text-purple-600" />
                    </div>
                    <h4 className="font-medium">{wf.name}</h4>
                  </div>
                  <span
                    className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                      wf.isBuiltIn
                        ? 'bg-gray-100 text-gray-600'
                        : 'bg-blue-100 text-blue-700'
                    }`}
                  >
                    {wf.isBuiltIn ? 'Built-in' : 'Custom'}
                  </span>
                </div>
                <div className="flex gap-4 text-xs text-gray-500 mt-3">
                  <span>{wf.seedActivityCount} seed{wf.seedActivityCount !== 1 ? 's' : ''}</span>
                  <span>{wf.gateActivityCount} gate{wf.gateActivityCount !== 1 ? 's' : ''}</span>
                </div>
              </Link>
            ))}
          </div>
        )}
      </section>

      {/* Goals section */}
      <section>
        <h3 className="text-sm font-medium text-gray-500 mb-3">Goals</h3>
        {goalsLoading && (
          <div className="flex items-center gap-2 text-sm text-gray-400">
            <Loader2 className="w-4 h-4 animate-spin" />
            Loading goals...
          </div>
        )}
        {goalsError && (
          <div className="flex items-center gap-2 text-sm text-red-500">
            <AlertCircle className="w-4 h-4" />
            {goalsError instanceof Error ? goalsError.message : 'Failed to load goals'}
          </div>
        )}
        {goalData && goalData.goals.length === 0 && (
          <p className="text-gray-400 text-sm">No goals found.</p>
        )}
        {goalData && goalData.goals.length > 0 && (
          <div className="bg-white rounded-lg border shadow-sm overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b bg-gray-50">
                  <th className="text-left px-4 py-3 font-medium text-gray-500">ID</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Description</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Status</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Progress</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Workflow</th>
                </tr>
              </thead>
              <tbody>
                {goalData.goals.map((goal) => (
                  <tr key={goal.id} className="border-b last:border-b-0 hover:bg-gray-50 transition-colors">
                    <td className="px-4 py-3">
                      <Link
                        to={`/workflows/${encodeURIComponent(goal.id)}`}
                        className="font-mono text-xs text-blue-600 hover:underline"
                      >
                        {goal.id}
                      </Link>
                    </td>
                    <td className="px-4 py-3 text-gray-700">{goal.description}</td>
                    <td className="px-4 py-3">
                      <GoalStatusBadge status={goal.status} />
                    </td>
                    <td className="px-4 py-3 w-36">
                      <div className="flex items-center gap-2">
                        <div className="flex-1 h-2 bg-gray-200 rounded-full overflow-hidden">
                          <div
                            className={`h-full rounded-full transition-all ${
                              goal.progressPercent >= 100
                                ? 'bg-green-500'
                                : goal.progressPercent > 0
                                  ? 'bg-blue-500'
                                  : 'bg-gray-300'
                            }`}
                            style={{ width: `${Math.min(goal.progressPercent, 100)}%` }}
                          />
                        </div>
                        <span className="text-xs text-gray-500 w-8 text-right">
                          {goal.progressPercent}%
                        </span>
                      </div>
                    </td>
                    <td className="px-4 py-3">
                      {goal.workflowRef ? (
                        <span className="flex items-center gap-1 text-xs text-gray-600">
                          <Target className="w-3 h-3" />
                          {goal.workflowRef}
                        </span>
                      ) : (
                        <span className="text-xs text-gray-400">-</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  )
}

function GoalStatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    active: 'bg-blue-100 text-blue-700',
    complete: 'bg-green-100 text-green-700',
    paused: 'bg-yellow-100 text-yellow-700',
    failed: 'bg-red-100 text-red-700',
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
