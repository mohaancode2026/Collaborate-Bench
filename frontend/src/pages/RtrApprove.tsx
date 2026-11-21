import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { api, ApiError, RtrView } from '../api';
import { Logo } from '../Layout';
import { Alert, Field, Spinner } from '../ui';

/** Public page a consultant opens from the RTR email: review the role and their rate, then e-sign or decline. */
export default function RtrApprove() {
  const { token = '' } = useParams();
  const [view, setView] = useState<RtrView | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [agree, setAgree] = useState(false);
  const [name, setName] = useState('');
  const [declining, setDeclining] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<'Approved' | 'Declined' | null>(null);
  const [fieldError, setFieldError] = useState<string | null>(null);

  useEffect(() => {
    api<RtrView>(`/rtr/${encodeURIComponent(token)}`).then(setView).catch(e => setError(e instanceof ApiError ? e : null));
  }, [token]);

  const respond = async (decision: 'approve' | 'decline') => {
    setBusy(true); setFieldError(null);
    try {
      const r = await api<{ status: 'Approved' | 'Declined' }>(`/rtr/${encodeURIComponent(token)}`, { method: 'POST', body: { decision, signedName: name, reason } });
      setDone(r.status);
    } catch (e) {
      if (e instanceof ApiError && e.errors) setFieldError(Object.values(e.errors).flat().join(' '));
      else setError(e instanceof ApiError ? e : null);
    } finally { setBusy(false); }
  };

  const answered = done ?? (view && view.status !== 'Pending' ? view.status : null);
  return (
    <div className="rtr-page">
      <div className="rtr-card">
        <div className="brand" style={{ color: 'var(--ink)', marginBottom: 18 }}><Logo />{view?.companyName ?? 'Collaborate‑Bench'}</div>
        {error ? <Alert type="error" title={error.title}>{error.detail}</Alert>
          : !view ? <div className="empty"><Spinner dark /> Loading…</div>
          : answered ? (
            <Alert type={answered === 'Approved' ? 'success' : 'info'} title={answered === 'Approved' ? 'Thank you, you approved this submission' : answered === 'Declined' ? 'You declined this submission' : 'This request is no longer active'}>
              {answered === 'Approved' ? `${view.recruiterName} will submit your profile for ${view.jobTitle} shortly.` : answered === 'Declined' ? `${view.recruiterName} has been told. You won't be submitted for this role.` : 'Contact your recruiter if you still want to be submitted.'}
            </Alert>)
          : view.expired ? <Alert type="warning" title="This link has expired">Ask {view.recruiterName} to send a new one.</Alert>
          : <>
            <h1 style={{ fontSize: 22 }}>Hi {view.consultantFirstName}, please approve your submission</h1>
            <p className="muted" style={{ margin: '6px 0 16px' }}>{view.recruiterName} at {view.companyName} would like to submit your profile for this role. The resume that will be sent is attached to the email you received.</p>
            <dl className="rtr-facts">
              <dt>Position</dt><dd>{view.jobTitle}</dd>
              {view.client && <><dt>Client</dt><dd>{view.client}</dd></>}
              {view.vendor && <><dt>Submitted through</dt><dd>{view.vendor}</dd></>}
              {view.location && <><dt>Location</dt><dd>{view.location}</dd></>}
              <dt>Your rate</dt><dd>{view.payRate ? `$${view.payRate}/hr` : '-'} {view.contractType ? `(${view.contractType})` : ''}</dd>
            </dl>
            <div className="rtr-statement">{view.statement}</div>
            {!declining ? <>
              <label className="check" style={{ borderBottom: 0 }}><input type="checkbox" checked={agree} onChange={e => setAgree(e.target.checked)} /><span>I have read the statement above and agree to it.</span></label>
              <Field label="Type your full name to sign" error={fieldError ? [fieldError] : undefined}><input className="inp" value={name} onChange={e => setName(e.target.value)} autoComplete="name" /></Field>
              <button className="btn btn-dark" style={{ width: '100%', justifyContent: 'center' }} disabled={busy || !agree || name.trim().length < 3} onClick={() => respond('approve')}>{busy ? <Spinner /> : 'Approve and sign'}</button>
              <button className="btn btn-sm mt" style={{ color: 'var(--muted)', width: '100%', justifyContent: 'center' }} onClick={() => setDeclining(true)}>I don't want to be submitted for this role</button>
            </> : <>
              <Field label="Reason (optional)"><textarea className="inp" style={{ minHeight: 70 }} value={reason} onChange={e => setReason(e.target.value)} placeholder="e.g. Already submitted by another vendor, rate too low, not interested" maxLength={500} /></Field>
              <div className="flex"><button className="btn btn-outline" onClick={() => setDeclining(false)}>Back</button><button className="btn btn-danger" disabled={busy} onClick={() => respond('decline')}>{busy ? <Spinner /> : 'Decline'}</button></div>
            </>}
            <p className="muted" style={{ fontSize: 12, marginTop: 14 }}>Your answer is recorded with the date, time and your IP address as your Right to Represent. This link expires {new Date(view.expiresUtc).toLocaleString()}.</p>
          </>}
      </div>
    </div>
  );
}
