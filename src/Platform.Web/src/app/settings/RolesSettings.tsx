import { useEffect, useMemo, useState } from 'react';
import { Plus, Trash2, Check, AlertTriangle } from 'lucide-react';
import { useSettingsStore, type RoleConfig } from '@/stores/settingsStore';
// Shared with the pickers and the unrecognised-role marking, so "is this the same role?" is one
// answer everywhere.
import { canonicaliseRoleKey } from '@/lib/roleKey';
import { GroupPicker } from './approverPickers';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * The participant-role vocabulary editor (Settings → Participant Roles). Each role has a key and a
 * label, plus two optional refinements:
 *
 * - **Aliases** — other names producers send for the same role. Jira's "QA" field arrives as `qa` on
 *   tickets whose promotion policy asks for a `qa-owner`; listing `qa` here makes those tickets count
 *   as having a QA owner, label it as one, and replace it when somebody is assigned. Resolved on read,
 *   so it applies to the tickets already on screen.
 * - **Assignee groups** — the person picker for this role only offers members of these directory
 *   groups.
 *
 * Aliases are held as the comma-separated text being typed and split only on save, the same way the
 * environment editor does it: splitting on every keystroke would eat the comma as it is typed.
 */
export function RolesSettings() {
  const { roles, setRoles } = useSettingsStore();
  const [items, setItems] = useState<EditableRole[]>(() => roles.map(toEditable));
  const [saved, setSaved] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setItems(roles.map(toEditable));
  }, [roles]);

  const save = async () => {
    const cleaned: RoleConfig[] = items
      .map((r) => {
        const key = canonicaliseRoleKey(r.key);
        return {
          key,
          displayName: r.displayName.trim(),
          aliases: parseAliases(r.aliasText, key),
          assigneeGroups: r.assigneeGroups,
        };
      })
      .filter((r) => r.key.length > 0);
    setSaving(true);
    setError(null);
    try {
      await setRoles(cleaned);
      setItems(cleaned.map(toEditable));
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const updateItem = (index: number, patch: Partial<EditableRole>) => {
    setItems((prev) => prev.map((item, i) => (i === index ? { ...item, ...patch } : item)));
  };
  const removeItem = (index: number) => setItems((prev) => prev.filter((_, i) => i !== index));
  const addItem = () =>
    setItems((prev) => [...prev, { key: '', displayName: '', aliasText: '', assigneeGroups: [] }]);

  // The same two collisions the server refuses, shown on the row while the admin is still typing.
  const problems = useMemo(() => aliasProblems(items), [items]);

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Participant Roles
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          The roles the platform knows about. This list is what the role filters and assignment
          pickers offer, and the only set someone can be manually assigned to — a role that isn't
          here is flagged as unrecognised wherever it shows up. Ingest still accepts any role a
          producer sends. Keys are canonicalised to lower-kebab-case
          (<code>triggered-by</code>, <code>qa</code>); ones with no entry here fall back to a
          humanised form of the key for display.
        </p>
        <p className="text-[13px] mt-2" style={{ color: 'var(--text-muted)' }}>
          <strong>Also sent as</strong> lists other names producers use for the same role — put{' '}
          <code>qa</code> on <code>qa-owner</code> and a ticket whose Jira QA arrived as{' '}
          <code>qa</code> has its QA owner: it stops reading &ldquo;Needs QA owner&rdquo;, shows the
          person as the QA owner, and assigning someone replaces them. It applies to existing work
          items straight away. A name can&rsquo;t be both an alias and a role of its own, so delete
          the <code>qa</code> role first. <strong>Pick people from</strong> limits the person picker
          for the role to members of those Entra groups; leave it empty to search everyone.
        </p>
      </div>

      <div className="space-y-2">
        {items.map((item, index) => (
          <div
            key={index}
            className="rounded-lg border p-2.5 space-y-2"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <div className="grid grid-cols-[1fr_1fr_32px] gap-2 items-center">
              <input
                type="text"
                value={item.key}
                onChange={(e) => updateItem(index, { key: e.target.value })}
                placeholder="Key, e.g. qa-owner"
                aria-label="Role key"
                className={`min-w-0 font-mono ${inputClass}`}
                style={inputStyle}
              />
              <input
                type="text"
                value={item.displayName}
                onChange={(e) => updateItem(index, { displayName: e.target.value })}
                placeholder="Display name, e.g. QA owner"
                aria-label="Display name"
                className={`min-w-0 ${inputClass}`}
                style={inputStyle}
              />
              <button
                onClick={() => removeItem(index)}
                className="p-1 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
                title="Remove role"
                aria-label={`Remove role ${item.key || 'row'}`}
              >
                <Trash2 size={14} />
              </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-2 pr-0 md:pr-[40px]">
              <label className="space-y-1 min-w-0 block">
                <span className={labelClass} style={labelStyle}>
                  Also sent as
                </span>
                <input
                  type="text"
                  value={item.aliasText}
                  onChange={(e) => updateItem(index, { aliasText: e.target.value })}
                  placeholder="e.g. qa, quality-assurance"
                  spellCheck={false}
                  title="Other names producers send for this role, comma-separated"
                  className={`w-full min-w-0 font-mono ${inputClass}`}
                  style={inputStyle}
                />
              </label>
              <div className="space-y-1 min-w-0">
                <span className={labelClass} style={labelStyle}>
                  Pick people from
                </span>
                <GroupPicker
                  values={item.assigneeGroups}
                  onChange={(next) => updateItem(index, { assigneeGroups: next })}
                />
              </div>
            </div>

            {problems.get(index)?.map((message) => (
              <p
                key={message}
                className="text-[12px] flex items-start gap-1.5"
                style={{ color: 'var(--danger)' }}
              >
                <AlertTriangle size={12} style={{ flexShrink: 0, marginTop: 2 }} />
                <span>{message}</span>
              </p>
            ))}
          </div>
        ))}
      </div>

      <button
        onClick={addItem}
        className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
        style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
      >
        <Plus size={14} />
        Add Role
      </button>

      <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
        <button
          onClick={save}
          disabled={saving}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-60"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} /> Saved
          </span>
        )}
        {error && (
          <span className="text-[13px]" style={{ color: 'var(--danger)' }}>{error}</span>
        )}
      </div>
    </section>
  );
}

/** A role row as the editor holds it: aliases as the text being typed. */
interface EditableRole {
  key: string;
  displayName: string;
  aliasText: string;
  assigneeGroups: { id: string; name: string }[];
}

function toEditable(role: RoleConfig): EditableRole {
  return {
    key: role.key,
    displayName: role.displayName,
    aliasText: (role.aliases ?? []).join(', '),
    assigneeGroups: [...(role.assigneeGroups ?? [])],
  };
}

/**
 * The alias text as the list to save: canonicalised (the form every comparison uses), deduped, and
 * without the role's own key. The server applies the same cleaning; doing it here too means the
 * list the admin is left looking at is the list that was saved.
 */
function parseAliases(text: string, key: string): string[] {
  const out: string[] = [];
  for (const raw of text.split(',')) {
    const alias = canonicaliseRoleKey(raw);
    if (!alias || alias === key || out.includes(alias)) continue;
    out.push(alias);
  }
  return out;
}

/**
 * Per-row messages for the alias collisions the server rejects: an alias that is another row's key,
 * and an alias claimed by two rows. Keyed by row index.
 */
function aliasProblems(items: EditableRole[]): Map<number, string[]> {
  const problems = new Map<number, string[]>();
  const add = (index: number, message: string) =>
    problems.set(index, [...(problems.get(index) ?? []), message]);

  const keyRows = new Map<string, number>();
  items.forEach((item, i) => {
    const key = canonicaliseRoleKey(item.key);
    if (key && !keyRows.has(key)) keyRows.set(key, i);
  });

  const aliasOwner = new Map<string, number>();
  items.forEach((item, i) => {
    const key = canonicaliseRoleKey(item.key);
    if (!key) return;
    for (const alias of parseAliases(item.aliasText, key)) {
      if (keyRows.has(alias)) {
        add(i, `“${alias}” is also a role of its own — delete that role to make it another name for “${key}”.`);
        continue;
      }
      const owner = aliasOwner.get(alias);
      if (owner !== undefined && owner !== i) {
        const ownerKey = canonicaliseRoleKey(items[owner].key);
        add(i, `“${alias}” is already an alias of “${ownerKey}” — an alias can belong to one role only.`);
        continue;
      }
      aliasOwner.set(alias, i);
    }
  });
  return problems;
}
