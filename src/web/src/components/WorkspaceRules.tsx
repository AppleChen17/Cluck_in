import { useEffect, useId, useState } from 'react';
import type { RuleCategory, WorkspaceProfile } from '../models/WorkspaceProfile';
import { workspaceService, type WorkspaceService } from '../services/workspaceService';
import { addRule, hasChanges, removeRule } from './workspaceRulesState';

const categories: { key: RuleCategory; title: string; hint: string; noun: string; blocked: boolean }[] = [
  { key: 'allowedApplications', title: 'Allowed Applications', hint: 'Process names, such as code or WindowsTerminal.', noun: 'application', blocked: false },
  { key: 'allowedWindowKeywords', title: 'Allowed Keywords', hint: 'Words found in a window or browser page title.', noun: 'keyword', blocked: false },
  { key: 'blockedApplications', title: 'Blocked Applications', hint: 'Applications you want to avoid in this workspace.', noun: 'application', blocked: true },
  { key: 'blockedWindowKeywords', title: 'Blocked Keywords', hint: 'Title keywords you want to avoid, such as YouTube.', noun: 'keyword', blocked: true },
];

function RuleList({ workspace, category, disabled, onChange }: {
  workspace: WorkspaceProfile;
  category: typeof categories[number];
  disabled: boolean;
  onChange: (profile: WorkspaceProfile) => void;
}) {
  const id = useId();
  const [input, setInput] = useState('');
  const [message, setMessage] = useState('');
  return (
    <section className={'card rule-card' + (category.blocked ? ' rule-card-blocked' : '')} aria-labelledby={id + '-heading'}>
      <div className="card-heading">
        <h2 id={id + '-heading'}>{category.title}</h2>
        <span className="rule-count">{workspace[category.key].length} rules</span>
      </div>
      <p id={id + '-hint'} className="muted rule-hint">{category.hint}</p>
      {workspace[category.key].length ? (
        <ul className="rule-chips" aria-label={category.title}>
          {workspace[category.key].map(value => (
            <li key={value}>
              <span>{value}</span>
              <button type="button" aria-label={'Remove ' + value + ' from ' + category.title}
                disabled={disabled}
                onClick={() => { onChange(removeRule(workspace, category.key, value)); setMessage(''); }}>
                <span aria-hidden="true">×</span>
              </button>
            </li>
          ))}
        </ul>
      ) : <p className="rule-empty">No {category.title.toLowerCase()}.</p>}
      <form className="rule-add" aria-label={'Add to ' + category.title} onSubmit={event => {
        event.preventDefault();
        const result = addRule(workspace, category.key, input);
        setMessage(result.error ?? '');
        if (result.workspace !== workspace) { onChange(result.workspace); setInput(''); }
      }}>
        <label className="sr-only" htmlFor={id + '-input'}>Add to {category.title}</label>
        <input id={id + '-input'} value={input} disabled={disabled} autoComplete="off"
          placeholder={'Enter ' + category.noun + '…'} aria-describedby={id + '-hint ' + id + '-message'}
          onChange={event => { setInput(event.target.value); setMessage(''); }} />
        <button type="submit" className="rule-button" disabled={disabled || !input.trim()}>Add</button>
      </form>
      <p id={id + '-message'} role="status" className="rule-validation">{message}</p>
    </section>
  );
}

export default function WorkspaceRules({ service = workspaceService }: { service?: WorkspaceService }) {
  const [saved, setSaved] = useState<WorkspaceProfile[]>([]);
  const [drafts, setDrafts] = useState<Record<string, WorkspaceProfile>>({});
  const [selectedId, setSelectedId] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [reload, setReload] = useState(0);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError('');
    service.getWorkspaces().then(profiles => {
      if (!active) return;
      setSaved(profiles);
      setDrafts({});
      setSelectedId(profiles[0]?.id ?? '');
    }).catch(reason => {
      if (active) setError(reason instanceof Error ? reason.message : 'Unable to load workspaces.');
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [service, reload]);

  const selected = saved.find(profile => profile.id === selectedId);
  const draft = selected ? drafts[selectedId] ?? selected : undefined;
  const dirty = !!selected && !!draft && hasChanges(selected, draft);
  const dirtyCount = saved.filter(profile => drafts[profile.id] && hasChanges(profile, drafts[profile.id])).length;

  useEffect(() => {
    if (!dirtyCount) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirtyCount]);

  function edit(profile: WorkspaceProfile) {
    setDrafts(current => ({ ...current, [profile.id]: profile }));
    setNotice('');
    setError('');
  }

  async function save() {
    if (!draft || !dirty || saving) return;
    const submitted = draft;
    setSaving(true);
    setError('');
    setNotice('');
    try {
      const updated = await service.updateWorkspace(submitted.id, submitted);
      setSaved(current => current.map(profile => profile.id === updated.id ? updated : profile));
      setDrafts(current => { const next = { ...current }; delete next[updated.id]; return next; });
      setRevision(value => value + 1);
      setNotice(updated.name + ' rules saved for this demo session.');
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to save. Your changes are still here.');
    } finally { setSaving(false); }
  }

  return (
    <main className="dashboard workspace-rules">
      <header className="page-header">
        <div className="brand">
          <span className="brand-icon" aria-hidden="true">🐔</span>
          <div><p className="eyebrow">MAKE ROOM FOR WHAT MATTERS</p><h1>Workspace Rules</h1></div>
        </div>
        <span className="demo-badge">Local demo · No backend connection</span>
      </header>
      <p className="intro">Choose what belongs in your workspace. Edit the rules, then save when you’re ready.</p>
      {loading ? <p role="status" className="card">Loading workspaces…</p> : !selected || !draft ? (
        <section className="card">
          <p role={error ? 'alert' : 'status'}>{error || 'No workspaces are available.'}</p>
          <button type="button" className="rule-button" onClick={() => setReload(value => value + 1)}>Retry</button>
        </section>
      ) : (
        <>
          <section className="card workspace-toolbar" aria-label="Workspace selection">
            <div>
              <label htmlFor="workspace-selector" className="label">Workspace</label>
              <select id="workspace-selector" value={selectedId} disabled={saving} onChange={event => {
                setSelectedId(event.target.value); setError(''); setNotice('');
              }}>
                {saved.map(profile => (
                  <option key={profile.id} value={profile.id}>
                    {profile.name}{drafts[profile.id] && hasChanges(profile, drafts[profile.id]) ? ' · Unsaved' : ''}
                  </option>
                ))}
              </select>
            </div>
            <p className="muted">Drafts stay with each workspace when you switch.<br />
              Saved demo rules reset when you reload this page.</p>
          </section>
          <div className="rules-grid">
            {categories.map(category => (
              <RuleList key={selectedId + category.key + revision} workspace={draft} category={category}
                disabled={saving} onChange={edit} />
            ))}
          </div>
          <p className="muted rule-policy-note">Keywords match window or page titles, not URLs. Block rules take priority in the Desktop Agent.
            These demo edits do not change your Windows app.</p>
          {/* Future: a separate Temporary Task Allowlist section, with its own lifecycle.
              Do not merge AI-generated temporary rules into these permanent profiles. */}
          <section className="card rules-save-bar" aria-label="Save workspace rules">
            <div>
              <p className={dirty ? 'unsaved-label' : 'saved-label'} role="status">
                {saving ? 'Saving…' : dirty ? 'Unsaved changes' : 'No unsaved changes'}
              </p>
              <p className="muted">{dirtyCount > 1 ? dirtyCount + ' workspaces have unsaved changes.' : 'Save applies to ' + selected.name + ' only.'}</p>
            </div>
            <div className="rule-actions">
              <button type="button" className="rule-button" disabled={!dirty || saving} onClick={() => {
                setDrafts(current => { const next = { ...current }; delete next[selectedId]; return next; });
                setRevision(value => value + 1); setError(''); setNotice('Restored the last saved ' + selected.name + ' rules.');
              }}>Reset</button>
              <button type="button" className="rule-button primary" disabled={!dirty || saving} onClick={() => void save()}>
                {saving ? 'Saving…' : 'Save Changes'}
              </button>
            </div>
          </section>
          <p className="rule-notice" role="status">{notice}</p>
          {error && <p className="rule-error" role="alert">{error}</p>}
        </>
      )}
      <footer>Workspace policy editor · Focus evaluation stays in the C# Desktop Agent</footer>
    </main>
  );
}
