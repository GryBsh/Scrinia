import { useMemo, useCallback } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
} from '@xyflow/react'
import type { Edge } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { Graph, layout } from '@dagrejs/dagre'

import WorkflowNode from './WorkflowNode'
import type { WorkflowNodeType, WorkflowNodeData } from './WorkflowNode'
import type { TaskSummary, PhaseGroup } from '../api/types'

// ── Constants ────────────────────────────────────────────────────────────────

const NODE_WIDTH = 200
const NODE_HEIGHT = 60
const GATE_HEIGHT = 50

const SEED_KEYWORDS = new Set(['researcher', 'auditor', 'planner'])

const nodeTypes = { workflowNode: WorkflowNode }

// ── Props ────────────────────────────────────────────────────────────────────

interface WorkflowGraphProps {
  phases: PhaseGroup[]
  onNodeClick?: (task: TaskSummary) => void
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function classifyNode(task: TaskSummary): 'seed' | 'execution' | 'gate' {
  if (task.gateType && SEED_KEYWORDS.has(task.gateType)) return 'seed'
  if (task.gateType) return 'gate'
  return 'execution'
}

function buildGraph(phases: PhaseGroup[]): {
  nodes: WorkflowNodeType[]
  edges: Edge[]
} {
  // Flatten all tasks across phases
  const allTasks: TaskSummary[] = phases.flatMap((p) => p.tasks)

  // Build a lookup for task names (to resolve * dependencies)
  const taskByName = new Map<string, TaskSummary>()
  const executionTaskNames: string[] = []
  for (const t of allTasks) {
    taskByName.set(t.name, t)
    if (classifyNode(t) === 'execution') {
      executionTaskNames.push(t.name)
    }
  }

  // Create dagre graph
  const g = new Graph({ directed: true })
  g.setGraph({
    rankdir: 'TB',
    nodesep: 60,
    ranksep: 100,
    marginx: 40,
    marginy: 40,
  })

  // Add nodes
  for (const task of allTasks) {
    const kind = classifyNode(task)
    const h = kind === 'gate' ? GATE_HEIGHT : NODE_HEIGHT
    g.setNode(task.name, { width: NODE_WIDTH, height: h })
  }

  // Add edges
  const edgeList: Array<{ source: string; target: string }> = []
  for (const task of allTasks) {
    for (const dep of task.dependsOn) {
      if (dep === '*') {
        // Wildcard: depend on all execution tasks
        for (const execName of executionTaskNames) {
          if (execName !== task.name) {
            edgeList.push({ source: execName, target: task.name })
          }
        }
      } else if (taskByName.has(dep)) {
        edgeList.push({ source: dep, target: task.name })
      }
    }
  }

  for (const { source, target } of edgeList) {
    g.setEdge(source, target)
  }

  // Run dagre layout
  layout(g)

  // Convert to React Flow nodes
  const nodes: WorkflowNodeType[] = allTasks.map((task) => {
    const pos = g.node(task.name)
    const kind = classifyNode(task)
    const h = kind === 'gate' ? GATE_HEIGHT : NODE_HEIGHT
    return {
      id: task.name,
      type: 'workflowNode' as const,
      position: { x: (pos?.x ?? 0) - NODE_WIDTH / 2, y: (pos?.y ?? 0) - h / 2 },
      data: {
        label: shortLabel(task.name),
        status: task.status,
        wave: task.wave,
        nodeKind: kind,
        gateType: task.gateType,
        description: task.description,
      } satisfies WorkflowNodeData,
    }
  })

  // Convert to React Flow edges
  const edges: Edge[] = edgeList.map(({ source, target }, i) => ({
    id: `e-${source}-${target}-${i}`,
    source,
    target,
    animated: isActiveEdge(taskByName.get(target)),
    style: { stroke: edgeColor(taskByName.get(target)) },
  }))

  return { nodes, edges }
}

function shortLabel(name: string): string {
  // Task names may be like "task:g59-72c-01-1-03" — show last segments for readability
  const parts = name.split(':')
  const label = parts.length > 1 ? parts[parts.length - 1] : name
  return label.length > 28 ? label.slice(0, 26) + '...' : label
}

function isActiveEdge(target: TaskSummary | undefined): boolean {
  if (!target) return false
  return target.status === 'active' || target.status === 'running'
}

function edgeColor(target: TaskSummary | undefined): string {
  if (!target) return '#d1d5db'
  if (target.status === 'complete' || target.status === 'done') return '#22c55e'
  if (target.status === 'active' || target.status === 'running') return '#3b82f6'
  if (target.status === 'failed' || target.status === 'error') return '#ef4444'
  return '#d1d5db'
}

// ── Component ────────────────────────────────────────────────────────────────

export default function WorkflowGraph({ phases, onNodeClick }: WorkflowGraphProps) {
  const { nodes: initialNodes, edges: initialEdges } = useMemo(() => buildGraph(phases), [phases])

  const [nodes, , onNodesChange] = useNodesState(initialNodes)
  const [edges, , onEdgesChange] = useEdgesState(initialEdges)

  const handleNodeClick = useCallback(
    (_event: React.MouseEvent, node: WorkflowNodeType) => {
      if (!onNodeClick) return
      const allTasks = phases.flatMap((p) => p.tasks)
      const task = allTasks.find((t) => t.name === node.id)
      if (task) onNodeClick(task)
    },
    [phases, onNodeClick],
  )

  return (
    <div className="w-full h-full">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeClick={handleNodeClick}
        nodeTypes={nodeTypes}
        fitView
        fitViewOptions={{ padding: 0.2 }}
        minZoom={0.2}
        maxZoom={2}
        proOptions={{ hideAttribution: true }}
      >
        <Background gap={20} size={1} color="#e5e7eb" />
        <Controls showInteractive={false} />
        <MiniMap
          nodeStrokeWidth={3}
          nodeColor={(node) => {
            const data = node.data as WorkflowNodeData | undefined
            if (!data) return '#d1d5db'
            if (data.status === 'complete' || data.status === 'done') return '#22c55e'
            if (data.status === 'active' || data.status === 'running') return '#3b82f6'
            if (data.status === 'failed' || data.status === 'error') return '#ef4444'
            return '#d1d5db'
          }}
        />
      </ReactFlow>
    </div>
  )
}
