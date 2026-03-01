export default function MemoryContent({ content }: { content: string }) {
  return (
    <pre className="bg-gray-900 text-gray-100 p-4 rounded-lg overflow-x-auto text-sm font-mono whitespace-pre-wrap leading-relaxed max-h-[600px] overflow-y-auto">
      {content}
    </pre>
  )
}
