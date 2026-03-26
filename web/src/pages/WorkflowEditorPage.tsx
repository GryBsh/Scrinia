import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, Link, useBlocker } from 'react-router'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Save, RotateCcw, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react'
import Editor, { type OnMount } from '@monaco-editor/react'
import * as yaml from 'js-yaml'
import { getWorkflow, updateWorkflow } from '../api/client'

const DEFAULT_STORE = 'default'

interface ParsedActivity {
  name: string
  type: string
  role: string
  tag?: string
  dependsOn: string[]
  skill?: string
  wave?: number
}

interface ParsedWorkflow {
  name?: string
  activities: ParsedActivity[]
  errors: string[]
}

function parseDeps(val: unknown): string[] {
  return Array.isArray(val) ? val.map(String) : []
}

function parseWorkflowYaml(content: string): ParsedWorkflow {
  const result: ParsedWorkflow = {
    activities: [],
    errors: [],
  }

  if (!content.trim()) {
    result.errors.push('Empty YAML content')
    return result
  }

  try {
    const doc = yaml.load(content) as Record<string, unknown> | null
    if (!doc || typeof doc !== 'object') {
      result.errors.push('YAML must be a mapping at the top level')
      return result
    }

    result.name = doc.name as string | undefined

    // v2 unified format
    const activities = doc.activities
    if (Array.isArray(activities)) {
      for (const act of activities) {
        if (act && typeof act === 'object') {
          const a = act as Record<string, unknown>
          result.activities.push({
            name: String(a.id ?? a.name ?? '(unnamed)'),
            type: String(a.type ?? 'agent'),
            role: String(a.role ?? 'seed'),
            tag: a.tag ? String(a.tag) : (a.gateType ? String(a.gateType) : undefined),
            dependsOn: parseDeps(a.dependsOn ?? a.depends_on),
            skill: a.skill ? String(a.skill) : undefined,
            wave: typeof a.wave === 'number' ? a.wave : undefined,
          })
        }
      }
    }

    // v1 backward compat: seedActivities + gateActivities
    if (!Array.isArray(activities)) {
      const seeds = doc.seedActivities ?? doc.seed_activities
      if (Array.isArray(seeds)) {
        for (const act of seeds) {
          if (act && typeof act === 'object') {
            const a = act as Record<string, unknown>
            result.activities.push({
              name: String(a.id ?? a.name ?? '(unnamed)'),
              type: String(a.type ?? 'agent'),
              role: 'seed',
              tag: a.tag ? String(a.tag) : (a.gateType ? String(a.gateType) : undefined),
              dependsOn: parseDeps(a.dependsOn ?? a.depends_on),
              skill: a.skill ? String(a.skill) : undefined,
              wave: typeof a.wave === 'number' ? a.wave : undefined,
            })
          }
        }
      }
      const gates = doc.gateActivities ?? doc.gate_activities
      if (Array.isArray(gates)) {
        for (const act of gates) {
          if (act && typeof act === 'object') {
            const a = act as Record<string, unknown>
            result.activities.push({
              name: String(a.id ?? a.name ?? '(unnamed)'),
              type: String(a.type ?? 'agent'),
              role: 'post-plan',
              tag: a.tag ? String(a.tag) : (a.gateType ? String(a.gateType) : undefined),
              dependsOn: parseDeps(a.dependsOn ?? a.depends_on),
              skill: a.skill ? String(a.skill) : undefined,
              wave: typeof a.wave === 'number' ? a.wave : undefined,
            })
          }
        }
      }
    }
  } catch (e) {
    result.errors.push(e instanceof Error ? e.message : 'Failed to parse YAML')
  }

  return result
}

export default function WorkflowEditorPage() {
  const { name } = useParams<{ name: string }>()
  const decodedName = name ? decodeURIComponent(name) : undefined
  const queryClient = useQueryClient()
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)

  const [yamlContent, setYamlContent] = useState('')
  const [savedContent, setSavedContent] = useState('')
  const [dirty, setDirty] = useState(false)
  const [parsed, setParsed] = useState<ParsedWorkflow>({ activities: [], errors: [] })
  const [serverErrors, setServerErrors] = useState<string[]>([])
  const [saveSuccess, setSaveSuccess] = useState(false)

  // Fetch existing workflow
  const { data, isLoading, error: fetchError } = useQuery({
    queryKey: ['workflow', DEFAULT_STORE, decodedName],
    queryFn: () => getWorkflow(DEFAULT_STORE, decodedName!),
    enabled: !!decodedName,
  })

  // Initialize editor content from fetched data
  useEffect(() => {
    if (data?.yamlContent != null) {
      setYamlContent(data.yamlContent)
      setSavedContent(data.yamlContent)
      setDirty(false)
      setParsed(parseWorkflowYaml(data.yamlContent))
    }
  }, [data])

  // Parse YAML on content change
  const handleEditorChange = useCallback((value: string | undefined) => {
    const v = value ?? ''
    setYamlContent(v)
    setDirty(v !== savedContent)
    setParsed(parseWorkflowYaml(v))
    setServerErrors([])
    setSaveSuccess(false)
  }, [savedContent])

  // Save mutation
  const saveMutation = useMutation({
    mutationFn: () => {
      if (!decodedName) throw new Error('Cannot save without a workflow name')
      return updateWorkflow(DEFAULT_STORE, decodedName, yamlContent)
    },
    onSuccess: () => {
      setSavedContent(yamlContent)
      setDirty(false)
      setServerErrors([])
      setSaveSuccess(true)
      setTimeout(() => setSaveSuccess(false), 3000)
      queryClient.invalidateQueries({ queryKey: ['workflow', DEFAULT_STORE, decodedName] })
      queryClient.invalidateQueries({ queryKey: ['workflows'] })
    },
    onError: (err: Error) => {
      setServerErrors([err.message])
    },
  })

  // Reset to last saved
  const handleReset = useCallback(() => {
    setYamlContent(savedContent)
    setDirty(false)
    setParsed(parseWorkflowYaml(savedContent))
    setServerErrors([])
    setSaveSuccess(false)
  }, [savedContent])

  // Warn before navigation if dirty
  const blocker = useBlocker(dirty)

  useEffect(() => {
    if (blocker.state === 'blocked') {
      const leave = window.confirm('You have unsaved changes. Leave anyway?')
      if (leave) {
        blocker.proceed()
      } else {
        blocker.reset()
      }
    }
  }, [blocker])

  // Also warn on browser close / refresh
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (dirty) {
        e.preventDefault()
      }
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const handleEditorMount: OnMount = (editor) => {
    editorRef.current = editor
  }

  const allActivities = parsed.activities
  const seedActivities = allActivities.filter((a) => a.role === 'seed')
  const postPlanActivities = allActivities.filter((a) => a.role === 'post-plan')
  const allErrors = [...parsed.errors, ...serverErrors]

  if (isLoading) {
    return (
      <div className="p-6 flex items-center gap-2 text-gray-500">
        <Loader2 className="w-5 h-5 animate-spin" />
        Loading workflow...
      </div>
    )
  }

  if (fetchError && decodedName) {
    return (
      <div className="p-6 max-w-4xl mx-auto">
        <Link to="/workflows" className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
          <ArrowLeft className="w-4 h-4" />
          Back to Workflows
        </Link>
        <div className="flex items-center gap-2 text-red-500">
          <AlertCircle className="w-5 h-5" />
          {fetchError instanceof Error ? fetchError.message : 'Failed to load workflow'}
        </div>
      </div>
    )
  }

  return (
    <div className="flex flex-col h-full">
      {/* Toolbar */}
      <div className="flex items-center gap-4 px-4 py-3 border-b bg-white shrink-0">
        <Link to="/workflows" className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700">
          <ArrowLeft className="w-4 h-4" />
          Back
        </Link>

        <div className="h-5 w-px bg-gray-200" />

        <div className="flex items-center gap-2 min-w-0">
          <span className="text-sm font-medium text-gray-700 truncate">
            {decodedName ?? 'New Workflow'}
          </span>
          {dirty && (
            <span className="text-xs text-amber-600 bg-amber-50 px-1.5 py-0.5 rounded font-medium shrink-0">
              Unsaved
            </span>
          )}
        </div>

        <div className="flex-1" />

        {saveSuccess && (
          <span className="flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="w-4 h-4" />
            Saved
          </span>
        )}

        <button
          onClick={handleReset}
          disabled={!dirty}
          className="flex items-center gap-1.5 px-3 py-1.5 text-sm border rounded-md hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
        >
          <RotateCcw className="w-3.5 h-3.5" />
          Reset
        </button>

        <button
          onClick={() => saveMutation.mutate()}
          disabled={!dirty || !decodedName || saveMutation.isPending}
          className="flex items-center gap-1.5 px-3 py-1.5 text-sm bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
        >
          {saveMutation.isPending ? (
            <Loader2 className="w-3.5 h-3.5 animate-spin" />
          ) : (
            <Save className="w-3.5 h-3.5" />
          )}
          Save
        </button>
      </div>

      {/* Main split pane */}
      <div className="flex flex-1 min-h-0 flex-col md:flex-row">
        {/* Editor pane — collapses to make room for preview */}
        <div className="flex-1 min-h-0 min-w-0 border-r border-gray-200">
          <Editor
            height="100%"
            language="yaml"
            theme="vs-dark"
            value={yamlContent}
            onChange={handleEditorChange}
            onMount={handleEditorMount}
            options={{
              minimap: { enabled: false },
              fontSize: 14,
              wordWrap: 'on',
              scrollBeyondLastLine: false,
              tabSize: 2,
              renderLineHighlight: 'all',
              padding: { top: 12 },
            }}
            loading={
              <div className="flex items-center justify-center h-full bg-gray-900 text-gray-400">
                <Loader2 className="w-5 h-5 animate-spin mr-2" />
                Loading editor...
              </div>
            }
          />
        </div>

        {/* Preview pane — minimum width, takes priority */}
        <div className="min-h-0 overflow-auto bg-gray-50 w-80 md:w-96 shrink-0">
          <div className="p-4 space-y-4">
            <h3 className="text-sm font-medium text-gray-500 uppercase tracking-wide">Preview</h3>

            {/* Validation errors */}
            {allErrors.length > 0 && (
              <div className="bg-red-50 border border-red-200 rounded-md p-3">
                <h4 className="text-sm font-medium text-red-700 mb-1">Validation Errors</h4>
                <ul className="space-y-1">
                  {allErrors.map((err, i) => (
                    <li key={i} className="text-sm text-red-600 flex items-start gap-1.5">
                      <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                      {err}
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {/* Summary */}
            {parsed.errors.length === 0 && yamlContent.trim() && (
              <>
                <div className="bg-white rounded-md border p-3">
                  <h4 className="text-sm font-medium text-gray-700 mb-2">Summary</h4>
                  <div className="grid grid-cols-2 gap-2 text-sm">
                    {parsed.name && (
                      <div className="col-span-2">
                        <span className="text-gray-500">Name:</span>{' '}
                        <span className="font-mono text-gray-800">{parsed.name}</span>
                      </div>
                    )}
                    <div>
                      <span className="text-gray-500">Seed:</span>{' '}
                      <span className="font-semibold text-gray-800">{seedActivities.length}</span>
                    </div>
                    <div>
                      <span className="text-gray-500">Post-plan:</span>{' '}
                      <span className="font-semibold text-gray-800">{postPlanActivities.length}</span>
                    </div>
                    <div className="col-span-2">
                      <span className="text-gray-500">Total:</span>{' '}
                      <span className="font-semibold text-gray-800">{allActivities.length}</span>
                    </div>
                  </div>
                </div>

                {/* Activities list */}
                {allActivities.length > 0 && (
                  <div className="bg-white rounded-md border p-3">
                    <h4 className="text-sm font-medium text-gray-700 mb-2">Activities</h4>
                    <div className="space-y-2">
                      {allActivities.map((act) => {
                        const badgeColor = act.type === 'spawner'
                          ? 'bg-purple-100 text-purple-700'
                          : act.type === 'system'
                          ? 'bg-gray-100 text-gray-700'
                          : act.role === 'post-plan'
                          ? 'bg-amber-100 text-amber-700'
                          : 'bg-blue-100 text-blue-700'
                        return (
                          <ActivityRow key={act.name} activity={act} badge={act.type} badgeColor={badgeColor} />
                        )
                      })}
                    </div>
                  </div>
                )}

                {/* Dependency graph summary */}
                {allActivities.some((a) => a.dependsOn.length > 0) && (
                  <div className="bg-white rounded-md border p-3">
                    <h4 className="text-sm font-medium text-gray-700 mb-2">Dependency Graph</h4>
                    <div className="space-y-1 text-sm font-mono">
                      {allActivities
                        .filter((a) => a.dependsOn.length > 0)
                        .map((act) => (
                          <div key={act.name} className="text-gray-600">
                            <span className="text-gray-800">{act.name}</span>
                            <span className="text-gray-400"> &larr; </span>
                            {act.dependsOn.join(', ')}
                          </div>
                        ))}
                    </div>
                  </div>
                )}
              </>
            )}

            {/* Empty state */}
            {!yamlContent.trim() && parsed.errors.length === 0 && (
              <div className="text-sm text-gray-400 text-center py-8">
                Start typing YAML to see a preview
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function ActivityRow({
  activity,
  badge,
  badgeColor,
}: {
  activity: ParsedActivity
  badge: string
  badgeColor: string
}) {
  return (
    <div className="flex items-start gap-2 text-sm">
      <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${badgeColor}`}>
        {badge}
      </span>
      <div className="min-w-0">
        <span className="font-mono text-gray-800">{activity.name}</span>
        {activity.skill && (
          <span className="ml-2 text-xs text-gray-500">skill: {activity.skill}</span>
        )}
        {activity.wave != null && (
          <span className="ml-2 text-xs text-gray-500">wave: {activity.wave}</span>
        )}
        {activity.dependsOn.length > 0 && (
          <div className="text-xs text-gray-400 mt-0.5">
            depends on: {activity.dependsOn.join(', ')}
          </div>
        )}
      </div>
    </div>
  )
}
