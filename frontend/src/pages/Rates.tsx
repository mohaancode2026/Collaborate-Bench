import { useState } from 'react';
import { Field, money } from '../ui';

export default function Rates() {
  const [v, setV] = useState({ client: 95, prime: 12, t1: 8, pay: 68, type: 'C2C', hrs: 40, wks: 52 });
  const n = (k: keyof typeof v) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setV({ ...v, [k]: k === 'type' ? e.target.value : Number(e.target.value) });
  const errors: Record<string, string[]> = {};
  if (!(v.client > 0)) errors.client = ['Enter a positive rate'];
  if (v.prime < 0 || v.prime >= 60) errors.prime = ['Use 0–60%'];
  if (v.t1 < 0 || v.t1 >= 60) errors.t1 = ['Use 0–60%'];
  if (!(v.pay > 0)) errors.pay = ['Enter a positive pay rate'];
  if (!(v.hrs > 0 && v.hrs <= 80)) errors.hrs = ['1–80 hours'];
  const ok = Object.keys(errors).length === 0;
  const afterPrime = v.client * (1 - v.prime / 100), toUs = afterPrime * (1 - v.t1 / 100);
  const burden = v.type === 'W2' ? 1.12 : 1, cost = v.pay * burden, m = toUs - cost, mp = toUs ? m / toUs * 100 : 0;
  return (
    <>
      <div className="ph"><div><h1>Rate &amp; Margin Calculator</h1><p>What each layer of the staffing chain keeps, from end client to consultant.</p></div></div>
      <div className="grid g12">
        <div className="card"><div className="card-h"><h3>Inputs</h3></div><div className="card-b">
          <Field label="End‑client bill rate ($/hr)" error={errors.client}><input className="inp" type="number" value={v.client} onChange={n('client')} /></Field>
          <div className="row2"><Field label="Prime / implementation partner margin (%)" error={errors.prime}><input className="inp" type="number" value={v.prime} onChange={n('prime')} /></Field>
            <Field label="Tier‑1 vendor margin (%)" error={errors.t1}><input className="inp" type="number" value={v.t1} onChange={n('t1')} /></Field></div>
          <div className="row2"><Field label="Consultant pay rate ($/hr)" error={errors.pay}><input className="inp" type="number" value={v.pay} onChange={n('pay')} /></Field>
            <Field label="Contract type"><select className="inp" value={v.type} onChange={n('type')}><option value="C2C">C2C</option><option value="W2">W2 (adds ~12% burden)</option><option value="1099">1099</option></select></Field></div>
          <div className="row2"><Field label="Hours per week" error={errors.hrs}><input className="inp" type="number" value={v.hrs} onChange={n('hrs')} /></Field>
            <Field label="Contract length (weeks)"><input className="inp" type="number" value={v.wks} onChange={n('wks')} /></Field></div>
        </div></div>
        <div>
          <div className="calc-out">{!ok ? <div className="big" style={{ fontSize: 18 }}>Fix the highlighted inputs</div> : <>
            <small style={{ opacity: .75 }}>Your gross margin</small><div className="big">{money(m)}/hr <span style={{ fontSize: 16, opacity: .75 }}>({mp.toFixed(1)}%)</span></div>
            <div className="mt">
              <div className="cr"><span>Rate you bill the Tier‑1 vendor</span><b className="mono">{money(toUs)}/hr</b></div>
              <div className="cr"><span>Consultant cost{v.type === 'W2' ? ' (incl. burden)' : ''}</span><b className="mono">{money(cost)}/hr</b></div>
              <div className="cr"><span>Weekly margin</span><b className="mono">{money(m * v.hrs)}</b></div>
              <div className="cr"><span>Contract margin ({v.wks} wks)</span><b className="mono">{money(m * v.hrs * v.wks)}</b></div>
              <div className="cr"><span>Max pay rate at 15% margin</span><b className="mono">{money(toUs * 0.85 / burden)}/hr</b></div>
            </div>
            {m < 0 ? <div className="mt" style={{ background: 'rgba(239,68,68,.25)', padding: 10, borderRadius: 10 }}>⚠ Negative margin: renegotiate the rate or lower the pay rate.</div>
              : mp < 8 && <div className="mt" style={{ background: 'rgba(245,158,11,.25)', padding: 10, borderRadius: 10 }}>⚠ Margin under 8%. Delivery Manager approval recommended.</div>}
          </>}</div>
          {ok && <div className="chain">
            {[['End client', v.client], ['Prime keeps', v.client * v.prime / 100], ['Tier‑1 keeps', afterPrime * v.t1 / 100], ['You keep', m], ['Consultant', v.pay]].map(([l, x], i) =>
              <span key={String(l)} style={{ display: 'contents' }}>{i > 0 && <span>→</span>}<div><small className="muted">{l}</small><b>{money(Number(x))}</b></div></span>)}
          </div>}
        </div>
      </div>
    </>
  );
}
