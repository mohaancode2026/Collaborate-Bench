import { useCallback, useEffect, useRef, useState } from 'react';
import { api, EmailTemplate, MergeField, RenderResult } from './api';
import { useAuth } from './auth';
import { Alert, Empty, Field, FieldErrors, Modal, Spinner, ago, useConfirm, useErrorHandler, useToast } from './ui';

const GROUPS: [string, string][] = [['contact', 'Vendor contact'], ['vendor', 'Vendor'], ['consultant', 'Consultant'], ['requirement', 'Requirement'], ['me', 'You'], ['other', 'Other']];

export function useMergeFields() {
  const [meta, setMeta] = useState<{ fields: MergeField[]; categories: string[] } | null>(null);
  useEffect(() => { api<{ fields: MergeField[]; categories: string[] }>('/mail/templates/fields').then(setMeta).catch(() => setMeta({ fields: [], categories: ['Other'] })); }, []);
  return meta;
}

/** Templates tab on Vendor Emails: shared team templates and the user's own. */
export function TemplatesPanel() {
  const { can } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const confirm = useConfirm();
  const [list, setList] = useState<EmailTemplate[] | null>(null);
  const [edit, setEdit] = useState<EmailTemplate | 'new' | null>(null);
  const [show, setShow] = useState<'all' | 'shared' | 'mine'>('all');

  const load = useCallback(() => { api<EmailTemplate[]>('/mail/templates').then(setList).catch(e => { setList([]); onError(e); }); }, [onError]);
  useEffect(load, [load]);

  const duplicate = async (t: EmailTemplate) => {
    try { const c = await api<EmailTemplate>(`/mail/templates/${t.id}/duplicate`, { method: 'POST' }); notify('success', 'Copied to your templates', `"${c.name}" is yours to change.`); load(); setEdit(c); }
    catch (e) { onError(e); }
  };
  const remove = async (t: EmailTemplate) => {
    if (!await confirm({ title: `Delete "${t.name}"?`, message: t.shared ? 'Everyone on the team loses this shared template.' : 'This template will be removed.', okLabel: 'Delete', danger: true })) return;
    try { await api(`/mail/templates/${t.id}`, { method: 'DELETE' }); notify('success', 'Template deleted'); load(); } catch (e) { onError(e); }
  };

  const shown = (list ?? []).filter(t => show === 'all' || (show === 'shared' ? t.shared : !t.shared));
  const cats = [...new Set(shown.map(t => t.category))];

  return (
    <div className="card-b">
      <div className="flex between" style={{ marginBottom: 14 }}>
        <div className="tabs">{([['all', 'All'], ['shared', 'Team templates'], ['mine', 'My templates']] as const).map(([k, l]) =>
          <button key={k} className={show === k ? 'on' : ''} onClick={() => setShow(k)}>{l}</button>)}</div>
        <button className="btn btn-sm btn-primary" onClick={() => setEdit('new')}>+ New template</button>
      </div>
      <p className="muted" style={{ fontSize: 12.5, marginBottom: 14 }}>
        Fields like <span className="mono">{'{{contact.firstName}}'}</span> or <span className="mono">{'{{consultant.name}}'}</span> are filled in when you write an email.
        {can('templates.manage') ? ' You can edit team templates.' : ' Team templates are managed by your Team Lead. Duplicate one to make your own version.'}
      </p>
      {list === null ? <Empty><Spinner dark /> Loading templates…</Empty> : shown.length === 0 ? <Empty>No templates here yet.</Empty> : cats.map(cat => (
        <div key={cat} style={{ marginBottom: 16 }}>
          <h4 style={{ fontSize: 12, textTransform: 'uppercase', letterSpacing: '.8px', color: 'var(--muted)', marginBottom: 6 }}>{cat}</h4>
          {shown.filter(t => t.category === cat).map(t => (
            <div key={t.id} className="match" style={{ alignItems: 'flex-start' }}>
              <div className="mi" style={{ minWidth: 0 }}>
                <b>{t.name}</b> {t.shared ? <span className="tag c">{t.builtIn ? 'Built-in · team' : 'Team'}</span> : <span className="tag s">Mine</span>}
                <small style={{ display: 'block' }}>Subject: {t.subject}</small>
                <div className="why" style={{ whiteSpace: 'pre-wrap', maxHeight: 54, overflow: 'hidden' }}>{t.body}</div>
                <small className="muted">{t.useCount ? `Used ${t.useCount} time${t.useCount === 1 ? '' : 's'}${t.lastUsedUtc ? `, last ${ago(t.lastUsedUtc)}` : ''}` : 'Not used yet'}
                  {t.ownerName && t.shared ? ` · by ${t.ownerName}` : ''}{t.needs.length ? ` · needs a ${t.needs.join(' and ')}` : ''}</small>
              </div>
              <div className="flex" style={{ flexWrap: 'nowrap' }}>
                {t.canEdit && <button className="btn btn-sm btn-outline" onClick={() => setEdit(t)}>Edit</button>}
                <button className="btn btn-sm btn-outline" onClick={() => duplicate(t)}>Duplicate</button>
                {t.canEdit && <button className="btn btn-sm" style={{ color: 'var(--bad)' }} onClick={() => remove(t)}>Delete</button>}
              </div>
            </div>))}
        </div>))}
      {edit && <TemplateEditor template={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); load(); }} />}
    </div>
  );
}

function TemplateEditor({ template, onClose, onSaved }: { template: EmailTemplate | null; onClose: () => void; onSaved: () => void }) {
  const { can } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const meta = useMergeFields();
  const [d, setD] = useState({ name: template?.name ?? '', category: template?.category ?? 'Submission', subject: template?.subject ?? '', body: template?.body ?? '', shared: template?.shared ?? false });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState<RenderResult | null>(null);
  const subjectRef = useRef<HTMLInputElement>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);
  const lastFocus = useRef<'subject' | 'body'>('body');

  // Live preview with example values (no real records needed).
  useEffect(() => {
    const t = setTimeout(() => {
      if (!d.subject && !d.body) { setPreview(null); return; }
      api<RenderResult>('/mail/templates/render', { method: 'POST', body: { subject: d.subject, body: d.body, sample: true } }).then(setPreview).catch(() => {});
    }, 350);
    return () => clearTimeout(t);
  }, [d.subject, d.body]);

  const insert = (key: string) => {
    const token = `{{${key}}}`;
    const el = lastFocus.current === 'subject' ? subjectRef.current : bodyRef.current;
    const field = lastFocus.current;
    const value = d[field];
    const start = el?.selectionStart ?? value.length, end = el?.selectionEnd ?? value.length;
    setD({ ...d, [field]: value.slice(0, start) + token + value.slice(end) });
    requestAnimationFrame(() => { el?.focus(); el?.setSelectionRange(start + token.length, start + token.length); });
  };

  const save = async () => {
    setBusy(true); setErrors({});
    try {
      if (template) await api(`/mail/templates/${template.id}`, { method: 'PUT', body: d });
      else await api('/mail/templates', { method: 'POST', body: d });
      notify('success', template ? 'Template saved' : 'Template created', d.shared ? 'Everyone on the team can use it.' : 'Only you can see it.');
      onSaved();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  return (
    <Modal wide title={template ? `Edit template · ${template.name}` : 'New email template'} onClose={onClose}
      footer={<><button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : 'Save template'}</button></>}>
      <div className="row3">
        <Field label="Name *" error={errors.name} className="span2"><input className="inp" value={d.name} onChange={e => setD({ ...d, name: e.target.value })} placeholder="e.g. Java submission to prime vendor" autoFocus /></Field>
        <Field label="Category *" error={errors.category}>
          <select className="inp" value={d.category} onChange={e => setD({ ...d, category: e.target.value })}>{(meta?.categories ?? [d.category]).map(c => <option key={c}>{c}</option>)}</select></Field>
      </div>
      {can('templates.manage') && !template?.builtIn && (
        <label className="check" style={{ borderBottom: 0, paddingTop: 0 }}><input type="checkbox" checked={d.shared} onChange={e => setD({ ...d, shared: e.target.checked })} />
          <span>Team template: everyone can use it, and only Team Leads and above can change it</span></label>)}
      <div className="tpl-grid">
        <div>
          <Field label="Subject *" error={errors.subject}>
            <input ref={subjectRef} className="inp" value={d.subject} onFocus={() => { lastFocus.current = 'subject'; }} onChange={e => setD({ ...d, subject: e.target.value })} /></Field>
          <Field label="Email *" error={errors.body}>
            <textarea ref={bodyRef} className="inp" style={{ minHeight: 260 }} value={d.body} onFocus={() => { lastFocus.current = 'body'; }} onChange={e => setD({ ...d, body: e.target.value })} /></Field>
        </div>
        <aside className="tpl-fields">
          <b style={{ fontSize: 13 }}>Insert a field</b>
          <p className="muted" style={{ fontSize: 12, margin: '2px 0 8px' }}>Click to add it where your cursor is.</p>
          {GROUPS.map(([g, label]) => {
            const fs = (meta?.fields ?? []).filter(f => f.group === g);
            return fs.length ? <div key={g} style={{ marginBottom: 8 }}><small className="muted" style={{ fontWeight: 600 }}>{label}</small>
              <div>{fs.map(f => <button key={f.key} type="button" className="skchip" style={{ margin: '3px 4px 0 0' }} title={`${f.label} · e.g. ${f.example}`} onClick={() => insert(f.key)}>{f.key.split('.').pop()}</button>)}</div></div> : null;
          })}
          <p className="muted" style={{ fontSize: 11.5, marginTop: 8 }}>Pay rates aren't available as fields, so they can't reach a vendor by accident.</p>
        </aside>
      </div>
      {preview && <>
        {preview.unknown.length > 0 && <Alert type="warning" title="Unknown fields">{preview.unknown.map(k => `{{${k}}}`).join(', ')} won't be filled in. Use the field list.</Alert>}
        <h4 style={{ fontSize: 13, margin: '6px 0' }}>Preview with example values</h4>
        <div className="jd"><b>{preview.subject}</b>{'\n\n'}{preview.body}</div>
      </>}
    </Modal>
  );
}
