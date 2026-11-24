import { useEffect, useMemo, useState } from 'react';
import { AiText, api, Consultant, EmailTemplate, MailConnection, postForm, RenderResult, Requirement, VendorEmail } from './api';
import { useAuth } from './auth';
import { Alert, copyText, Field, FieldErrors, Modal, Spinner, useErrorHandler, useToast } from './ui';

interface Contact { address: string; name?: string | null; vendorId: number; vendor: string; lastContactUtc?: string | null; }
export interface ComposeInitial { to?: string; cc?: string; subject?: string; body?: string; }
interface SigInfo { signature: string; custom: boolean; default: string; source: 'custom' | 'outlook' | 'default'; outlook?: string | null; htmlPreview?: string | null; }

const MAX_BYTES = 3 * 1024 * 1024;
const BLOCKED = /\.(exe|bat|cmd|com|msi|ps1|vbs|js|jar|scr|dll|reg|lnk|hta|iso)$/i;
/** Company names compared loosely: "Kforce Inc." and "KFORCE" match. */
const norm = (s?: string | null) => (s ?? '').toLowerCase().replace(/\b(inc|llc|ltd|corp|corporation|co|technologies|solutions|group)\b\.?/g, '').replace(/[^a-z0-9]/g, '');
const size = (b: number) => (b < 1024 * 1024 ? `${Math.max(1, Math.round(b / 1024))} KB` : `${(b / 1024 / 1024).toFixed(1)} MB`);

/** Write an email to a vendor and send it from the user's own Outlook (new email, or a reply inside a tracked thread). */
/** templateCategory: start with the best template of that category (e.g. "Follow-up"); consultantId fills its consultant fields. */
export function ComposeDialog({ initial, replyTo, onClose, onSent, templateCategory, consultantId, initialFiles, vendorHint, title, replyToInboxItemId }: {
  initial?: ComposeInitial; replyTo?: VendorEmail; onClose: () => void; onSent?: () => void; templateCategory?: string; consultantId?: number | null;
  /** Reply inside the thread of this AI Inbox email (it's marked done once sent). */
  replyToInboxItemId?: number;
  /** Files attached when the window opens, e.g. a tailored resume. */
  initialFiles?: File[];
  /** A company name (e.g. the job's poster): shows that vendor's contacts, and fills "To" with the latest one when it's empty. */
  vendorHint?: string;
  title?: string;
}) {
  const { scope } = useAuth();
  const notify = useToast();
  const onError = useErrorHandler();
  const [conn, setConn] = useState<MailConnection | null>(null);
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [vendorId, setVendorId] = useState('');
  const [to, setTo] = useState(initial?.to ?? (replyTo ? (replyTo.direction === 'In' ? replyTo.fromAddress : replyTo.toAddresses) : ''));
  const [cc, setCc] = useState(initial?.cc ?? '');
  const [subject, setSubject] = useState(initial?.subject ?? '');
  const [body, setBody] = useState(initial?.body ?? '');
  const [files, setFiles] = useState<File[]>(initialFiles ?? []);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [templates, setTemplates] = useState<EmailTemplate[]>([]);
  const [tplId, setTplId] = useState('');          // '' none, 'ai' AI pitch, otherwise a template id
  const [usedTemplate, setUsedTemplate] = useState<number | null>(null);
  const [cons, setCons] = useState<Consultant[]>([]);
  const [reqs, setReqs] = useState<Requirement[]>([]);
  const [pick, setPick] = useState({ consultantId: '', requirementId: '' });
  const [writing, setWriting] = useState(false);
  // Signature: added at the bottom of the message; editable and saved for next time.
  const [sig, setSig] = useState('');
  const [useSig, setUseSig] = useState(true);
  const [sigEdit, setSigEdit] = useState<string | null>(null);
  const [sigSaving, setSigSaving] = useState(false);
  /** Where the signature comes from: your Outlook (read from your sent mail), one you saved here, or the default. */
  const [sigInfo, setSigInfo] = useState<SigInfo | null>(null);
  /** The message with the signature at the bottom (once). Skipped when the text already ends with the sender's name. */
  const withSig = (text: string, s = sig) => {
    if (!s || !useSig) return text;
    const t = text.trimEnd();
    const nameLine = s.split('\n').map(x => x.trim()).filter(Boolean)[1] ?? '';
    if (t.includes(s.trim()) || (nameLine && t.slice(-120).includes(nameLine))) return text;
    return `${t}${t ? '\n\n' : ''}${s}`;
  };
  const withoutSig = (text: string, s = sig) => (s && text.includes(s) ? text.replace(`\n\n${s}`, '').replace(s, '').trimEnd() : text);

  useEffect(() => {
    api<MailConnection>('/mail/connection').then(setConn).catch(e => { onError(e); onClose(); });
    api<Contact[]>('/mail/contacts').then(list => {
      setContacts(list);
      const hint = norm(vendorHint);
      if (!hint) return;
      const match = list.find(c => { const v = norm(c.vendor); return v.length > 2 && (v.includes(hint) || hint.includes(v)); });
      if (!match) return;
      setVendorId(String(match.vendorId));
      const latest = list.filter(c => c.vendorId === match.vendorId).sort((a, b) => (b.lastContactUtc ?? '').localeCompare(a.lastContactUtc ?? ''))[0];
      if (latest) setTo(t => t.trim() || latest.address);
    }).catch(() => setContacts([]));
    api<EmailTemplate[]>('/mail/templates').then(setTemplates).catch(() => setTemplates([]));
    api<SigInfo>('/mail/signature').then(r => { setSigInfo(r); setSig(r.signature); setBody(b => withSig(b, r.signature)); }).catch(() => setSig(''));
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const toggleSig = (on: boolean) => { setUseSig(on); setBody(b => (on ? (b.includes(sig) ? b : `${b.trimEnd()}${b.trim() ? '\n\n' : ''}${sig}`) : withoutSig(b))); };
  const saveSig = async () => {
    if (sigEdit === null) return;
    setSigSaving(true);
    try {
      const r = await api<SigInfo>('/mail/signature', { method: 'PUT', body: { signature: sigEdit } });
      setBody(b => withSig(withoutSig(b), r.signature));   // swap the old signature for the new one
      setSig(r.signature); setSigInfo(r); setSigEdit(null);
      notify('success', r.source === 'custom' ? 'Signature saved' : r.source === 'outlook' ? 'Using your Outlook signature' : 'Using the default signature',
        r.source === 'custom' ? 'It will be added to your emails from now on.' : undefined);
    } catch (e) { onError(e); }
    finally { setSigSaving(false); }
  };

  /** Re-read the signature from Outlook (after changing it there), or switch from a saved one back to Outlook's. */
  const useOutlookSig = async (refresh: boolean) => {
    setSigSaving(true);
    try {
      const r = refresh ? await api<SigInfo>('/mail/signature/refresh', { method: 'POST' }) : await api<SigInfo>('/mail/signature', { method: 'PUT', body: { signature: '' } });
      setBody(b => withSig(withoutSig(b), r.signature)); setSig(r.signature); setSigInfo(r); setSigEdit(null);
      notify(r.source === 'outlook' ? 'success' : 'info', r.source === 'outlook' ? 'Outlook signature added' : 'No Outlook signature found',
        r.source === 'outlook' ? undefined : "Your recent Outlook emails don't share a signature yet, so the default one is used. You can edit it here.");
    } catch (e) { onError(e); } finally { setSigSaving(false); }
  };

  const selected = templates.find(t => String(t.id) === tplId);
  const needsConsultant = tplId === 'ai' || !!selected?.needs.includes('consultant');
  const needsRequirement = tplId === 'ai' || !!selected?.needs.includes('requirement');
  useEffect(() => {
    if (!(needsConsultant || needsRequirement) || cons.length || reqs.length) return;
    api<Consultant[]>('/consultants', { query: { scope } }).then(c => setCons(c.filter(x => x.status === 'Bench' || x.status === 'Marketing'))).catch(onError);
    api<Requirement[]>('/requirements', { query: { status: 'Open' } }).then(setReqs).catch(onError);
  }, [needsConsultant, needsRequirement, scope, cons.length, reqs.length, onError]);

  const vendors = useMemo(() => [...new Map(contacts.map(c => [c.vendorId, c.vendor])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [contacts]);
  const toList = to.split(/[,;\s]+/).filter(Boolean).map(a => a.toLowerCase());
  const shownContacts = contacts.filter(c => (!vendorId || String(c.vendorId) === vendorId) && !toList.includes(c.address.toLowerCase())).slice(0, 8);
  const total = files.reduce((n, f) => n + f.size, 0);

  const addFiles = (list: FileList | null) => {
    const next = [...files, ...Array.from(list ?? [])].filter((f, i, a) => a.findIndex(x => x.name === f.name && x.size === f.size) === i);
    const bad = next.filter(f => BLOCKED.test(f.name));
    if (bad.length) notify('warning', "That file type can't be sent", bad.map(f => f.name).join(', '));
    const ok = next.filter(f => !BLOCKED.test(f.name)).slice(0, 5);
    if (ok.reduce((n, f) => n + f.size, 0) > MAX_BYTES) notify('warning', 'Attachments too large', 'Keep attachments under 3 MB in total. Outlook rejects bigger emails sent this way.');
    setFiles(ok);
  };

  const applyTemplate = async (id = tplId, p = pick) => {
    const t = templates.find(x => String(x.id) === id);
    if (id !== 'ai' && !t) return;
    const needC = id === 'ai' || !!t?.needs.includes('consultant'), needR = id === 'ai' || !!t?.needs.includes('requirement');
    if ((needC && !p.consultantId) || (needR && !p.requirementId)) {
      notify('warning', `Pick ${needC && needR ? 'a consultant and a requirement' : needC ? 'a consultant' : 'a requirement'}`, 'The template uses their details.'); return;
    }
    if (t) {
      setWriting(true);
      try {
        const r = await api<RenderResult>('/mail/templates/render', { method: 'POST', body: {
          templateId: t.id, consultantId: p.consultantId ? Number(p.consultantId) : undefined,
          requirementId: p.requirementId ? Number(p.requirementId) : undefined, to } });
        if (!replyTo) setSubject(r.subject);
        setBody(withSig(r.body));
        setUsedTemplate(t.id);
        if (r.missing.length) notify('warning', 'Some fields are empty', `${r.missing.map(k => `{{${k}}}`).join(', ')} couldn't be filled in. Edit them before sending.`);
      } catch (e) { onError(e); }
      finally { setWriting(false); }
      return;
    }
    setWriting(true);
    try {
      const pitch = await api<AiText>('/ai/pitch', { method: 'POST', body: { consultantId: Number(p.consultantId), requirementId: Number(p.requirementId) } });
      const c = cons.find(x => String(x.id) === p.consultantId), r = reqs.find(x => String(x.id) === p.requirementId);
      if (!replyTo) setSubject(`Submission: ${c?.name} for ${r?.title}${r?.client ? ` (${r.client})` : ''}`);
      setBody(withSig(pitch.text));
      setUsedTemplate(null);
      if (pitch.notice) notify('info', 'Built-in writer used', pitch.notice);
    } catch (e) { onError(e); }
    finally { setWriting(false); }
  };

  const connect = async () => {
    try { window.location.assign((await api<{ url: string }>('/auth/microsoft/start', { method: 'POST', body: { mode: 'connect' } })).url); }
    catch (e) { onError(e); }
  };

  // Opened from a follow-up reminder: pick the follow-up template that needs the least, and fill it in when nothing is missing.
  const [preselected, setPreselected] = useState(false);
  useEffect(() => {
    if (preselected || !templateCategory || templates.length === 0) return;
    setPreselected(true);
    const unmet = (t: EmailTemplate) => t.needs.filter(n => !(n === 'consultant' && consultantId)).length;
    const best = templates.filter(t => t.category === templateCategory).sort((a, b) => unmet(a) - unmet(b))[0];
    if (!best) return;
    const p = { consultantId: consultantId ? String(consultantId) : '', requirementId: '' };
    setPick(p);
    setTplId(String(best.id));
    if (unmet(best) === 0) applyTemplate(String(best.id), p);
  }, [templates]); // eslint-disable-line react-hooks/exhaustive-deps

  const chooseTemplate = (id: string) => {
    setTplId(id);
    const t = templates.find(x => String(x.id) === id);
    if (t && t.needs.length === 0) applyTemplate(id);   // nothing to pick: fill in right away
  };

  const send = async () => {
    setBusy(true); setErrors({});
    const fd = new FormData();
    if (usedTemplate) fd.set('templateId', String(usedTemplate));
    fd.set('to', to); fd.set('cc', cc); fd.set('subject', subject); fd.set('body', body);
    if (replyTo) fd.set('replyToEmailId', String(replyTo.id));
    else if (replyToInboxItemId) fd.set('replyToInboxItemId', String(replyToInboxItemId));
    files.forEach(f => fd.append('files', f, f.name));
    try {
      const r = await postForm<{ sent: boolean; tracked: boolean }>('/mail/send', fd);
      notify('success', 'Email sent', `Sent from your Outlook${r.tracked ? '. It will show in Vendor Emails in a moment.' : '.'}`);
      if (!r.tracked) notify('info', 'Not a listed vendor', "None of the recipients are in the vendor list, so this email won't be tracked in Vendor Emails.");
      onSent?.(); onClose();
    } catch (e) { onError(e, setErrors); }
    finally { setBusy(false); }
  };

  const ready = conn?.connected && conn.status === 'Active' && conn.canSend;
  const mailto = `mailto:${encodeURIComponent(to)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body.slice(0, 1800))}`;

  return (
    <Modal wide title={replyTo ? `Reply · ${replyTo.subject}` : title ?? 'New email to vendor'} onClose={onClose}
      footer={<>
        {conn && !ready && to && <a className="btn btn-outline" style={{ marginRight: 'auto' }} href={mailto}>Use my mail app instead</a>}
        {/* Approve-then-send: the draft can also be copied into Outlook instead of being sent from here. */}
        <button className="btn btn-outline" style={conn && !ready && to ? undefined : { marginRight: 'auto' }}
          onClick={() => copyText(`To: ${to}${cc ? `\nCc: ${cc}` : ''}\nSubject: ${subject}\n\n${body}`, notify)}>Copy for Outlook</button>
        <button className="btn btn-outline" onClick={onClose}>Cancel</button>
        <button className="btn btn-primary" onClick={send} disabled={busy || !ready || total > MAX_BYTES}>{busy ? <Spinner /> : 'Send from Outlook'}</button></>}>
      {!conn ? <div className="empty"><Spinner dark /> Loading…</div> : <>
        {!conn.configured && <Alert type="info" title="Sending from the portal isn't set up">Your admin needs to connect the app to Microsoft 365 first. You can still use your mail app.</Alert>}
        {conn.configured && !conn.connected && <Alert type="info" title="Connect Outlook to send from the portal">
          Emails go out from your own mailbox and are tracked with the vendor. <button className="btn btn-sm btn-primary" style={{ marginLeft: 8 }} onClick={connect}>Connect Outlook</button></Alert>}
        {conn.connected && (conn.status !== 'Active' || !conn.canSend) && <Alert type="warning" title={conn.status !== 'Active' ? 'Outlook needs to be reconnected' : 'Allow sending once'}>
          {conn.status !== 'Active' ? conn.lastError : 'Sending needs one more Outlook permission. Reconnect and accept "send mail on your behalf".'}
          <button className="btn btn-sm btn-primary" style={{ marginLeft: 8 }} onClick={connect}>Reconnect Outlook</button></Alert>}
        {ready && <p className="muted" style={{ fontSize: 12.5, marginBottom: 10 }}>Sends from <b>{conn.mailbox}</b>. It appears in your Sent Items{replyTo ? ' in the same thread' : ''}.</p>}

        <div className="flex" style={{ marginBottom: 12 }}>
          <select className="inp" style={{ width: 'auto', maxWidth: 280 }} value={tplId} onChange={e => chooseTemplate(e.target.value)} aria-label="Template">
            <option value="">No template</option>
            <option value="ai">✦ AI pitch for a consultant</option>
            {[...new Set(templates.map(t => t.category))].map(cat => (
              <optgroup key={cat} label={cat}>{templates.filter(t => t.category === cat).map(t => <option key={t.id} value={t.id}>{t.name}{t.shared ? '' : ' (mine)'}</option>)}</optgroup>))}
          </select>
          {needsConsultant && <select className="inp" style={{ width: 'auto', maxWidth: 220 }} value={pick.consultantId} onChange={e => setPick({ ...pick, consultantId: e.target.value })} aria-label="Consultant">
            <option value="">Consultant…</option>{cons.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>}
          {needsRequirement && <select className="inp" style={{ width: 'auto', maxWidth: 260 }} value={pick.requirementId} onChange={e => setPick({ ...pick, requirementId: e.target.value })} aria-label="Requirement">
            <option value="">Requirement…</option>{reqs.map(r => <option key={r.id} value={r.id}>{r.title} · {r.client}</option>)}</select>}
          {tplId && (needsConsultant || needsRequirement || tplId === 'ai') && <button className="btn btn-sm btn-outline" onClick={() => applyTemplate()} disabled={writing}>{writing ? <Spinner dark /> : 'Fill in'}</button>}
          {tplId && !needsConsultant && !needsRequirement && tplId !== 'ai' && <button className="btn btn-sm btn-outline" onClick={() => applyTemplate()} disabled={writing} title="Fill in again, e.g. after changing the recipient">{writing ? <Spinner dark /> : 'Refill'}</button>}
        </div>

        <Field label="To *" error={errors.to}><input className="inp" value={to} onChange={e => setTo(e.target.value)} placeholder="recruiter@vendor.com, …" /></Field>
        {!to.trim() && vendorHint && <p className="muted" style={{ fontSize: 12.5, margin: '-6px 0 10px' }}>Add the {vendorHint} recruiter's email. Nothing matched {vendorHint} in your vendor list or the posting.</p>}
        {contacts.length > 0 && <div className="flex" style={{ margin: '-6px 0 12px' }}>
          {vendors.length > 1 && <select className="inp" style={{ width: 'auto', padding: '4px 8px', fontSize: 12.5 }} value={vendorId} onChange={e => setVendorId(e.target.value)} aria-label="Vendor contacts">
            <option value="">All vendors</option>{vendors.map(([id, n]) => <option key={id} value={id}>{n}</option>)}</select>}
          {shownContacts.map(c => <button key={c.address} type="button" className="skchip" title={c.vendor} onClick={() => setTo(t => (t.trim() ? `${t.trim().replace(/[,;]$/, '')}, ` : '') + c.address)}>+ {c.name || c.address}</button>)}
        </div>}
        <Field label="Cc" error={errors.cc}><input className="inp" value={cc} onChange={e => setCc(e.target.value)} /></Field>
        {!replyTo && <Field label="Subject *" error={errors.subject}><input className="inp" value={subject} onChange={e => setSubject(e.target.value)} /></Field>}
        <Field label="Message *" error={errors.body}><textarea className="inp" style={{ minHeight: 200 }} value={body} onChange={e => setBody(e.target.value)} />
          {replyTo && <small className="muted">The original email is quoted below your message.</small>}</Field>
        {sig && <div className="sig-row">
          <label className="check" style={{ margin: 0 }}><input type="checkbox" checked={useSig} onChange={e => toggleSig(e.target.checked)} />
            <span>{sigInfo?.source === 'outlook' ? 'Add my Outlook signature' : 'Add my signature'}</span></label>
          {sigInfo?.source === 'outlook' && <small className="muted">{sigInfo.htmlPreview ? 'Sent with its logos and formatting, as in Outlook.' : 'Same as the one Outlook puts on your emails.'}</small>}
          {sigInfo?.source === 'custom' && sigInfo.outlook && <button type="button" className="btn btn-sm" style={{ color: 'var(--primary)' }} onClick={() => useOutlookSig(false)} disabled={sigSaving}>Use my Outlook signature</button>}
          {sigInfo?.source !== 'custom' && <button type="button" className="btn btn-sm" style={{ color: 'var(--primary)' }} onClick={() => useOutlookSig(true)} disabled={sigSaving}
            title="Changed your signature in Outlook? Read it again">↻ Re-read from Outlook</button>}
          {sigEdit === null && <button type="button" className="btn btn-sm" style={{ color: 'var(--primary)' }} onClick={() => setSigEdit(sig)}>Edit signature</button>}
        </div>}
        {/* What the recipient sees: the Outlook signature with its images. Sandboxed: it's HTML from your own sent mail. */}
        {useSig && sigInfo?.source === 'outlook' && sigInfo.htmlPreview && sigEdit === null && (
          <iframe className="sig-preview" title="Signature preview" sandbox="" srcDoc={`<!doctype html><meta charset="utf-8"><style>body{margin:8px;font:13px Calibri,Arial,sans-serif;color:#111}img{max-width:100%;height:auto}</style>${sigInfo.htmlPreview}`} />)}
        {sigEdit !== null && <div className="sig-edit">
          <Field label="Your signature (saved for all your emails)"><textarea className="inp" style={{ minHeight: 100 }} value={sigEdit} onChange={e => setSigEdit(e.target.value)}
            placeholder={'Thanks & Regards,\nYour name\nTitle | Company\nPhone | Email'} /></Field>
          <div className="flex"><button type="button" className="btn btn-sm btn-primary" onClick={saveSig} disabled={sigSaving}>{sigSaving ? <Spinner /> : 'Save signature'}</button>
            <button type="button" className="btn btn-sm btn-outline" onClick={() => setSigEdit(null)}>Cancel</button>
            <small className="muted">Leave it empty to use your Outlook signature again.</small></div>
        </div>}
        <Field label="Attachments (resume, RTR…)" error={errors.files}>
          <input type="file" multiple onChange={e => { addFiles(e.target.files); e.target.value = ''; }} />
          {files.length > 0 && <div style={{ marginTop: 6 }}>{files.map(f => (
            <span key={f.name + f.size} className="tag s">📎 {f.name} · {size(f.size)} <button type="button" aria-label={`Remove ${f.name}`} style={{ marginLeft: 4 }} onClick={() => setFiles(files.filter(x => x !== f))}>✕</button></span>))}
            <span className="muted" style={{ fontSize: 12, color: total > MAX_BYTES ? 'var(--bad)' : undefined }}> {size(total)} of 3 MB</span></div>}
        </Field>
      </>}
    </Modal>
  );
}
