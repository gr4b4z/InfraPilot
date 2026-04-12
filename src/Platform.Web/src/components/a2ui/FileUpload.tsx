import { useRef, useState } from 'react';
import { Upload, X, FileIcon } from 'lucide-react';
import type { ComponentProps } from './A2UIRenderer';

interface SelectedFile {
  name: string;
  size: number;
  file: File;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function FileUpload({ component, error, onChange, readOnly }: ComponentProps) {
  const [files, setFiles] = useState<SelectedFile[]>([]);
  const [dragOver, setDragOver] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const maxFiles = component.maxFiles ?? 5;
  const maxSizeMb = component.maxSizeMb ?? 10;
  const acceptStr = component.accept?.join(',') || '';

  function addFiles(incoming: FileList | null) {
    if (!incoming) return;
    const next = [...files];
    for (let i = 0; i < incoming.length; i++) {
      if (next.length >= maxFiles) break;
      const f = incoming[i];
      if (f.size > maxSizeMb * 1024 * 1024) continue;
      if (!next.some((e) => e.name === f.name)) {
        next.push({ name: f.name, size: f.size, file: f });
      }
    }
    setFiles(next);
    onChange(next.map((f) => f.name));
  }

  function removeFile(name: string) {
    const next = files.filter((f) => f.name !== name);
    setFiles(next);
    onChange(next.map((f) => f.name));
  }

  return (
    <div>
      <label className="block text-sm font-medium mb-1.5" style={{ color: 'var(--text-primary)' }}>
        {component.label}
        {component.required && <span style={{ color: 'var(--accent)' }}> *</span>}
      </label>

      <div
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => { e.preventDefault(); setDragOver(false); addFiles(e.dataTransfer.files); }}
        onClick={() => !readOnly && inputRef.current?.click()}
        className={`flex flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed p-6 transition-colors ${
          readOnly ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'
        }`}
        style={{
          backgroundColor: dragOver ? 'var(--bg-tertiary)' : 'var(--bg-secondary)',
          borderColor: error ? '#EF4444' : dragOver ? 'var(--accent)' : 'var(--border-color)',
        }}
      >
        <Upload className="w-8 h-8" style={{ color: 'var(--text-muted)' }} />
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
          Drop files here or click to browse
        </p>
        <p className="text-xs" style={{ color: 'var(--text-muted)' }}>
          {component.accept?.length ? `Accepted: ${component.accept.join(', ')}` : 'All file types'}
          {' · '}Max {maxSizeMb} MB{' · '}Up to {maxFiles} file{maxFiles !== 1 ? 's' : ''}
        </p>
        <input
          ref={inputRef}
          type="file"
          accept={acceptStr}
          multiple={maxFiles > 1}
          onChange={(e) => addFiles(e.target.files)}
          className="hidden"
        />
      </div>

      {files.length > 0 && (
        <ul className="mt-2 space-y-1.5">
          {files.map((f) => (
            <li
              key={f.name}
              className="flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm"
              style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-primary)' }}
            >
              <FileIcon className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--text-muted)' }} />
              <span className="flex-1 truncate">{f.name}</span>
              <span className="text-xs flex-shrink-0" style={{ color: 'var(--text-muted)' }}>
                {formatSize(f.size)}
              </span>
              {!readOnly && (
                <button
                  type="button"
                  onClick={(e) => { e.stopPropagation(); removeFile(f.name); }}
                  className="flex-shrink-0 p-0.5 rounded hover:opacity-70 transition-opacity"
                  style={{ color: 'var(--text-muted)' }}
                >
                  <X className="w-3.5 h-3.5" />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {error && <p className="mt-1 text-xs text-red-500">{error}</p>}
    </div>
  );
}
