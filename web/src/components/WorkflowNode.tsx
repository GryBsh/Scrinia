import { memo } from 'react'
import { Handle, Position } from '@xyflow/react'
import type { NodeProps, Node } from '@xyflow/react'

// ── Data shape carried by every node ─────────────────────────────────────────

export interface WorkflowNodeData extends Record<string, unknown> {
  label: string
  status: string
  wave: number
  nodeKind: 'seed' | 'execution' | 'gate'
  gateType: string | null
  description: string | null
}

export type WorkflowNodeType = Node<WorkflowNodeData, 'workflowNode'>

// ── Status helpers ───────────────────────────────────────────────────────────

function statusDot(status: string) {
  if (status === 'complete' || status === 'done')
    return <span className="inline-block w-2.5 h-2.5 rounded-full bg-green-500 shrink-0" />
  if (status === 'active' || status === 'running')
    return <span className="inline-block w-2.5 h-2.5 rounded-full bg-blue-500 animate-pulse shrink-0" />
  if (status === 'failed' || status === 'error')
    return <span className="inline-block w-2.5 h-2.5 rounded-full bg-red-500 shrink-0" />
  // pending / blocked / unknown
  return <span className="inline-block w-2.5 h-2.5 rounded-full bg-gray-300 shrink-0" />
}

// ── Seed node (researcher, auditor, planner) ─────────────────────────────────

function SeedNode({ label, status, wave }: { label: string; status: string; wave: number }) {
  return (
    <div className="px-4 py-3 rounded-xl bg-indigo-50 border-2 border-indigo-300 shadow-sm min-w-[160px] max-w-[220px]">
      <div className="flex items-center gap-2 mb-1">
        {statusDot(status)}
        <span className="text-sm font-semibold text-indigo-800 truncate">{label}</span>
      </div>
      <div className="text-xs text-indigo-500">wave {wave}</div>
    </div>
  )
}

// ── Execution node (user tasks) ──────────────────────────────────────────────

function ExecutionNode({ label, status, wave }: { label: string; status: string; wave: number }) {
  const borderColor =
    status === 'complete' || status === 'done'
      ? 'border-l-green-500'
      : status === 'active' || status === 'running'
        ? 'border-l-yellow-400'
        : 'border-l-gray-300'

  return (
    <div
      className={`px-4 py-3 rounded-lg bg-white border border-gray-200 shadow-sm border-l-4 ${borderColor} min-w-[160px] max-w-[220px]`}
    >
      <div className="flex items-center gap-2 mb-1">
        {statusDot(status)}
        <span className="text-sm font-medium text-gray-800 truncate">{label}</span>
      </div>
      <div className="text-xs text-gray-400">wave {wave}</div>
    </div>
  )
}

// ── Gate node (qa, self-reflector, evolutionary, etc.) ───────────────────────

function GateNode({ label, status }: { label: string; status: string }) {
  const borderClass =
    status === 'failed' || status === 'error'
      ? 'border-red-400'
      : status === 'complete' || status === 'done'
        ? 'border-green-400'
        : 'border-amber-300'

  return (
    <div
      className={`px-4 py-3 bg-amber-50 border-2 ${borderClass} shadow-sm min-w-[140px] max-w-[200px]`}
      style={{ clipPath: 'polygon(12px 0%, calc(100% - 12px) 0%, 100% 50%, calc(100% - 12px) 100%, 12px 100%, 0% 50%)' }}
    >
      <div className="flex items-center gap-2 justify-center">
        {statusDot(status)}
        <span className="text-xs font-semibold text-amber-800 truncate">{label}</span>
      </div>
    </div>
  )
}

// ── Composite custom node ────────────────────────────────────────────────────

function WorkflowNode({ data }: NodeProps<WorkflowNodeType>) {
  const { label, status, wave, nodeKind, gateType } = data

  return (
    <>
      <Handle type="target" position={Position.Top} className="!bg-gray-400 !w-2 !h-2" />

      {nodeKind === 'seed' && <SeedNode label={label} status={status} wave={wave} />}
      {nodeKind === 'execution' && <ExecutionNode label={label} status={status} wave={wave} />}
      {nodeKind === 'gate' && <GateNode label={gateType ?? label} status={status} />}

      <Handle type="source" position={Position.Bottom} className="!bg-gray-400 !w-2 !h-2" />
    </>
  )
}

export default memo(WorkflowNode)
