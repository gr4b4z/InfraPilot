import { useEffect, useState } from 'react';
import { Plus, Trash2, Check, RotateCcw } from 'lucide-react';
import {
  useSettingsStore,
  DEFAULT_ACTIVITY_TEMPLATE,
  type ActivityTemplateLine,
} from '@/stores/settingsStore';

export function ActivityTemplateSettings() {
  const { activityTemplate, setActivityTemplate } = useSettingsStore();
  const [lines, setLines] = useState<ActivityTemplateLine[]>(activityTemplate);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    setLines(activityTemplate);
  }, [activityTemplate]);

  const save = () => {
    const cleaned = lines.filter((l) => l.template.trim() !== '');
    setActivityTemplate(cleaned);
    setLines(cleaned);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  const updateLine = (index: number, field: keyof ActivityTemplateLine, value: string) => {
    setLines((prev) => prev.map((item, i) => (i === index ? { ...item, [field]: value } : item)));
  };
  const removeLine = (index: number) => setLines((prev) => prev.filter((_, i) => i !== index));
  const addLine = () => setLines((prev) => [...prev, { template: '', style: 'muted' }]);
  const resetToDefault = () => setLines(DEFAULT_ACTIVITY_TEMPLATE);

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Activity Card Template
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Configure which fields appear on deployment activity cards. Each line is a template with
          placeholders. Lines where all placeholders are empty are hidden.
        </p>
      </div>

      {/* Placeholder reference */}
      <div
        className="rounded-lg p-3 text-[12px] space-y-1"
        style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}
      >
        <div className="font-medium" style={{ color: 'var(--text-secondary)' }}>
          Available placeholders:
        </div>
        <div className="grid grid-cols-2 gap-x-6 gap-y-0.5">
          <span>
            <code className="text-[11px]">{'{label:workItemTitle}'}</code> — Jira issue title
          </span>
          <span>
            <code className="text-[11px]">{'{label:prTitle}'}</code> — PR title
          </span>
          <span>
            <code className="text-[11px]">{'{label:workItemStatus}'}</code> — Jira status
          </span>
          <span>
            <code className="text-[11px]">{'{ref:work-item:key}'}</code> — Jira ticket key
          </span>
          <span>
            <code className="text-[11px]">{'{participant:PR Author}'}</code> — PR author name
          </span>
          <span>
            <code className="text-[11px]">{'{participant:PR Reviewer}'}</code> — PR reviewer
          </span>
          <span>
            <code className="text-[11px]">{'{participant:QA}'}</code> — QA person
          </span>
          <span>
            <code className="text-[11px]">{'{time}'}</code> — relative time
          </span>
          <span>
            <code className="text-[11px]">{'{service}'}</code>,{' '}
            <code className="text-[11px]">{'{environment}'}</code>,{' '}
            <code className="text-[11px]">{'{version}'}</code>,{' '}
            <code className="text-[11px]">{'{source}'}</code>
          </span>
          <span>
            <code className="text-[11px]">{'{ref:pull-request:url}'}</code>,{' '}
            <code className="text-[11px]">{'{ref:repository:revision}'}</code>
          </span>
        </div>
      </div>

      <div className="space-y-1.5">
        <div
          className="grid grid-cols-[1fr_120px_32px] gap-2 px-1 text-[11px] font-medium uppercase tracking-wider"
          style={{ color: 'var(--text-muted)' }}
        >
          <span>Template</span>
          <span>Style</span>
          <span />
        </div>

        {lines.map((line, index) => (
          <div key={index} className="grid grid-cols-[1fr_120px_32px] gap-2 items-center rounded-lg p-1.5">
            <input
              type="text"
              value={line.template}
              onChange={(e) => updateLine(index, 'template', e.target.value)}
              placeholder="e.g. {ref:work-item:key} — {label:workItemTitle}"
              className="px-2.5 py-1.5 rounded-lg border text-[13px] font-mono outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            />
            <select
              value={line.style}
              onChange={(e) => updateLine(index, 'style', e.target.value)}
              className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
            >
              <option value="primary">Primary</option>
              <option value="secondary">Secondary</option>
              <option value="muted">Muted</option>
            </select>
            <button
              onClick={() => removeLine(index)}
              className="p-1 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              <Trash2 size={14} />
            </button>
          </div>
        ))}
      </div>

      <div className="flex items-center gap-2">
        <button
          onClick={addLine}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
        >
          <Plus size={14} />
          Add Line
        </button>
        <button
          onClick={resetToDefault}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          <RotateCcw size={13} />
          Reset to Default
        </button>
      </div>

      <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
        <button
          onClick={save}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          Save
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} /> Saved
          </span>
        )}
      </div>
    </section>
  );
}
