import { useCallback, useEffect, useRef, useState } from 'react';
import { Activity, api, TeamMember } from '../api';
import { useAuth } from '../auth';
import { Paged, Pager } from '../pager';
import { roleLabel } from '../roles';
import { Alert, Avatar, Empty, Field, FieldErrors, Modal, Spinner, ago, copyText, useConfirm, useErrorHandler, useToast } from '../ui';

interface Matrix { roles: { id: number; name: string; scope: string }[]; permissions: { code: string; label: string }[]; grants: Record<string, string[]>; }

export default function Team() {
  const { can } = useAuth();
  const [tab, setTab] = useState<'org' | 'perm' | 'audit'>('org');
  const [edit, setEdit] = useState<TeamMember | 'new' | null>(null);
  const [reload, setReload] = useState(0);
  return (
    <>
      <div className="ph"><div><h1>Team &amp; Access</h1><p>Reporting lines, what each role can do, and the audit trail.</p></div>
        <div className="flex"><div className="tabs">
          <button className={tab === 'org' ? 'on' : ''} onClick={() => setTab('org')}>Hierarchy</button>
          <button className={tab === 'perm' ? 'on' : ''} onClick={() => setTab('perm')}>Roles &amp; permissions</button>
          {can('audit.view') && <button className={tab === 'audit' ? 'on' : ''} onClick={() => setTab('audit')}>Audit log</button>}</div>
          {can('team.manage') && <button className="btn btn-dark" onClick={() => setEdit('new')}>+ Add user</button>}</div></div>
      {tab === 'org' ? <Org key={reload} onEdit={setEdit} /> : tab === 'perm' ? <Perms /> : <Audit />}
      {edit && <UserDialog user={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); setReload(r => r + 1); }} />}
    </>
  );
}

function Org({ onEdit }: { onEdit: (u: TeamMember) => void }) {
  const { user, can } = useAuth();
  const onError = useErrorHandler();
  const notify = useToast();
  const [users, setUsers] = useState<TeamMember[] | null>(null);
  const [feed, setFeed] = useState<Activity[]>([]);
  const [note, setNote] = useState('');
  const [noteErr, setNoteErr] = useState<FieldErrors>({});
  const load = useCallback(() => {
    api<TeamMember[]>('/users').then(setUsers).catch(e => { setUsers([]); onError(e); });
    api<Activity[]>('/activity').then(setFeed).catch(() => {});
  }, [onError]);
  useEffect(load, [load]);
  if (!users) return <Empty><Spinner dark /> Loading…</Empty>;

  const ids = new Set(users.map(u => u.id));
  const roots = users.filter(u => !u.managerId || !ids.has(u.managerId));
  const Node = ({ u }: { u: TeamMember }) => {
    const kids = users.filter(x => x.managerId === u.id);
    return (
      <li><div className={`tnode${u.active ? '' : ' off'}${u.id === user?.id ? ' me' : ''}`}><Avatar name={u.name} size={36} />
        <div style={{ flex: 1, minWidth: 0 }}><b>{u.name}{u.id === user?.id && <span className="tag s"> You</span>}{!u.active && <span className="tag r"> Deactivated</span>}{u.locked && <span className="tag y"> Locked</span>}</b>
          <small>{roleLabel(u.role)}{u.team ? ` · ${u.team}` : ''} · {u.email}{u.lastLoginUtc ? ` · last sign-in ${ago(u.lastLoginUtc)}` : ''}</small>
          <div style={{ marginTop: 4 }}><span className="tag">{u.consultants} consultants</span><span className="tag c">{u.activeSubmissions} active subs</span><span className="tag g">{u.placed} placed</span></div></div>
        {can('team.manage') && u.id !== user?.id && !(u.role === 'Admin' && user?.role !== 'Admin') && <button className="btn btn-sm btn-outline" onClick={() => onEdit(u)}>Manage</button>}
      </div>{kids.length > 0 && <ul>{kids.map(k => <Node key={k.id} u={k} />)}</ul>}</li>
    );
  };
  const post = async () => {
    setNoteErr({});
    try { await api('/activity/note', { method: 'POST', body: { text: note } }); setNote(''); notify('success', 'Note posted'); load(); }
    catch (e) { onError(e, setNoteErr); }
  };
  return (
    <div className="grid g21">
      <div className="card"><div className="card-h"><h3>Organization</h3><span className="muted" style={{ fontSize: 13 }}>{user?.dataScope === 'OWN' ? 'You see only yourself' : user?.dataScope === 'TEAM' ? 'You see your team' : 'You see the whole organization'}</span></div>
        <div className="card-b"><ul className="tree">{roots.map(r => <Node key={r.id} u={r} />)}</ul></div></div>
      <div className="card"><div className="card-h"><h3>Team notes &amp; activity</h3></div><div className="card-b">
        <div className="flex" style={{ flexWrap: 'nowrap', alignItems: 'flex-start' }}><Field error={noteErr.text} className="grow"><input className="inp" value={note} onChange={e => setNote(e.target.value)} onKeyDown={e => e.key === 'Enter' && post()} placeholder="Post a note to your team…" /></Field>
          <button className="btn btn-primary btn-sm" onClick={post}>Post</button></div>
        <div style={{ maxHeight: 440, overflow: 'auto' }}>{feed.map(a => <div className="feed-i" key={a.id}><Avatar name={a.userName} /><div><b>{a.userName}</b> {a.detail}<small>{ago(a.createdUtc)}</small></div></div>)}</div>
      </div></div>
    </div>
  );
}

function Perms() {
  const { user } = useAuth();
  const onError = useErrorHandler();
  const [m, setM] = useState<Matrix | null>(null);
  useEffect(() => { api<Matrix>('/users/roles').then(setM).catch(onError); }, [onError]);
  if (!m) return <Empty><Spinner dark /> Loading…</Empty>;
  const scopeLabel: Record<string, string> = { ALL: 'All teams', TEAM: 'Own + team', OWN: 'Own only' };
  return (
    <div className="card"><div className="card-h"><h3>Permission matrix</h3><span className="muted" style={{ fontSize: 13 }}>Your role: <b>{roleLabel(user?.role)}</b></span></div>
      <div className="tw"><table className="matrix"><thead><tr><th>Permission</th>{m.roles.map(r => <th key={r.id} className={r.name === user?.role ? 'cur' : ''}>{roleLabel(r.name)}</th>)}</tr></thead><tbody>
        {m.permissions.map(p => <tr key={p.code}><td>{p.label[0].toUpperCase() + p.label.slice(1)}<br /><small className="muted mono">{p.code}</small></td>
          {m.roles.map(r => <td key={r.id} className={r.name === user?.role ? 'cur' : ''} style={{ textAlign: 'center' }}>{m.grants[r.name]?.includes(p.code) ? <span className="yes">✓</span> : <span className="no">—</span>}</td>)}</tr>)}
        <tr><td><b>Data visibility (row-level security)</b></td>{m.roles.map(r => <td key={r.id} className={r.name === user?.role ? 'cur' : ''} style={{ textAlign: 'center' }}>{scopeLabel[r.scope]}</td>)}</tr>
      </tbody></table></div></div>
  );
}

/** The audit trail, one page at a time from the server (the log grows with every action, so it's never loaded whole). */
function Audit() {
  const onError = useErrorHandler();
  const [f, setF] = useState<'' | 'Denied' | 'Success'>('');
  const [qInput, setQInput] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(50);
  const [data, setData] = useState<Paged<Activity> | null>(null);
  const [busy, setBusy] = useState(false);
  const seq = useRef(0);
  useEffect(() => { const t = setTimeout(() => { setQ(qInput.trim()); setPage(1); }, 350); return () => clearTimeout(t); }, [qInput]);
  useEffect(() => {
    const mine = ++seq.current;
    setBusy(true);
    api<Paged<Activity>>('/activity/audit/page', { query: { outcome: f || undefined, q: q || undefined, page, pageSize: size } })
      .then(r => { if (mine === seq.current) setData(r); })
      .catch(e => { if (mine === seq.current) { onError(e); setData(d => d ?? { items: [], total: 0, page: 1, pageSize: size }); } })
      .finally(() => { if (mine === seq.current) setBusy(false); });
  }, [f, q, page, size, onError]);
  const rows = data?.items ?? [];
  return (
    <div className="card"><div className="card-h"><h3>Audit log</h3>
      <div className="flex" style={{ gap: 8 }}>
        <div className="tabs">{([['', 'All events'], ['Denied', 'Denied access'], ['Success', 'Successful']] as const).map(([k, t]) =>
          <button key={k} className={f === k ? 'on' : ''} onClick={() => { setF(k); setPage(1); }}>{t}</button>)}</div>
        <input className="inp" type="search" placeholder="Search user, action, detail…" aria-label="Search the audit log" style={{ width: 240 }} value={qInput} onChange={e => setQInput(e.target.value)} />
      </div></div>
      {!data ? <Empty><Spinner dark /></Empty> : <>
        <div className="tw" style={{ opacity: busy ? 0.6 : 1, transition: 'opacity .15s' }}><table><thead><tr><th className="slno">Sl No</th><th>When</th><th>User</th><th>Role</th><th>Action</th><th>Detail</th></tr></thead><tbody>
          {rows.map((a, i) => <tr key={a.id} className={a.outcome !== 'Success' ? 'deny' : ''}><td className="slno">{(data.page - 1) * data.pageSize + i + 1}</td><td style={{ whiteSpace: 'nowrap' }}>{new Date(a.createdUtc).toLocaleString()}</td><td>{a.userName}</td><td>{roleLabel(a.role)}</td>
            <td><span className={`tag ${a.outcome === 'Success' ? 's' : 'r'}`}>{a.action}</span></td><td>{a.detail}</td></tr>)}
        </tbody></table>{rows.length === 0 && <Empty>{q ? `Nothing matches “${q}”.` : 'No events.'}</Empty>}</div>
        <Pager page={data.page} pageSize={data.pageSize} total={data.total} busy={busy} sizes={[25, 50, 100, 200]}
          onPage={setPage} onPageSize={n => { setSize(n); setPage(1); }} />
      </>}
    </div>
  );
}

function UserDialog({ user, onClose, onSaved }: { user: TeamMember | null; onClose: () => void; onSaved: () => void }) {
  const me = useAuth().user;
  const onError = useErrorHandler();
  const notify = useToast();
  const confirm = useConfirm();
  const [all, setAll] = useState<TeamMember[]>([]);
  const [d, setD] = useState({ name: user?.name ?? '', email: user?.email ?? '', role: user?.role ?? 'Recruiter', managerId: user?.managerId, team: user?.team ?? '', active: user?.active ?? true });
  const [errors, setErrors] = useState<FieldErrors>({});
  const [busy, setBusy] = useState(false);
  const [temp, setTemp] = useState<string | null>(null);
  useEffect(() => { api<TeamMember[]>('/users').then(setAll).catch(onError); }, [onError]);
  const roles = me?.role === 'Admin' ? ['Admin', 'Delivery Manager', 'Manager', 'Team Lead', 'Recruiter'] : ['Manager', 'Team Lead', 'Recruiter'];
  const managers = all.filter(u => u.active && u.id !== user?.id &&
    (d.role === 'Recruiter' ? u.role === 'Team Lead' : d.role === 'Team Lead' || d.role === 'Manager' ? u.role === 'Delivery Manager' : false));

  const save = async () => {
    if (user && user.active && !d.active && !await confirm({ title: 'Deactivate user?', message: `${user.name} won't be able to sign in. Their consultants and submissions move to their manager.`, okLabel: 'Deactivate', danger: true })) return;
    setBusy(true); setErrors({});
    try {
      if (user) { await api(`/users/${user.id}`, { method: 'PUT', body: d }); notify('success', 'User updated', `${d.name} · ${roleLabel(d.role)}`); onSaved(); }
      else { const r = await api<{ temporaryPassword: string }>('/users', { method: 'POST', body: d }); setTemp(r.temporaryPassword); notify('success', 'User created', d.name); }
    } catch (e) { onError(e, setErrors); } finally { setBusy(false); }
  };
  const action = async (path: string, label: string) => {
    try { const r = await api<{ temporaryPassword?: string } | undefined>(`/users/${user!.id}/${path}`, { method: 'POST' }); notify('success', label); if (r?.temporaryPassword) setTemp(r.temporaryPassword); }
    catch (e) { onError(e); }
  };

  if (temp) return (
    <Modal title="Temporary password" onClose={onSaved} footer={<><button className="btn btn-outline" onClick={() => copyText(temp, notify)}>Copy</button><button className="btn btn-primary" onClick={onSaved}>Done</button></>}>
      <Alert type="info" title="Share this securely. It's shown only once.">The user must choose their own password at first sign-in.</Alert>
      <div className="jd mono" style={{ fontSize: 16, textAlign: 'center' }}>{temp}</div>
    </Modal>
  );
  return (
    <Modal title={user ? `Manage ${user.name}` : 'Add user'} onClose={onClose}
      footer={<>{user && <><button className="btn btn-outline" style={{ marginRight: 'auto' }} onClick={() => action('reset-password', 'Password reset')}>Reset password</button>
        {user.locked && <button className="btn btn-outline" onClick={() => action('unlock', 'Account unlocked')}>Unlock</button>}</>}
        <button className="btn btn-outline" onClick={onClose}>Cancel</button><button className="btn btn-primary" onClick={save} disabled={busy}>{busy ? <Spinner /> : user ? 'Save changes' : 'Create user'}</button></>}>
      <div className="row2"><Field label="Full name *" error={errors.name}><input className="inp" value={d.name} onChange={e => setD({ ...d, name: e.target.value })} /></Field>
        <Field label="Work email *" error={errors.email}><input className="inp" value={d.email} onChange={e => setD({ ...d, email: e.target.value })} /></Field></div>
      <div className="row2"><Field label="Role *" error={errors.role}><select className="inp" value={d.role} onChange={e => setD({ ...d, role: e.target.value, managerId: undefined })}>{roles.map(r => <option key={r} value={r}>{roleLabel(r)}</option>)}</select></Field>
        <Field label="Reports to" error={errors.managerId}><select className="inp" value={d.managerId ?? ''} onChange={e => setD({ ...d, managerId: e.target.value ? Number(e.target.value) : undefined })}>
          <option value="">{managers.length ? 'Choose…' : '— none (top level) —'}</option>{managers.map(m => <option key={m.id} value={m.id}>{m.name} · {roleLabel(m.role)}{m.team ? ` · ${m.team}` : ''}</option>)}</select></Field></div>
      {d.role === 'Team Lead' && <Field label="Team name *" error={errors.team}><input className="inp" value={d.team} onChange={e => setD({ ...d, team: e.target.value })} placeholder="e.g. Team Gamma" /></Field>}
      {user && <label className="check"><input type="checkbox" checked={d.active} onChange={e => setD({ ...d, active: e.target.checked })} /><span>Account active</span></label>}
      {!user && <Alert type="info" title="A temporary password is generated">You'll see it once after creating the user.</Alert>}
    </Modal>
  );
}
