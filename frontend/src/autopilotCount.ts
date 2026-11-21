import { useEffect, useState } from 'react';
import { api, AutopilotSummary } from './api';
import { useAuth } from './auth';

/** Submissions waiting for approval + vendor replies to review, for the sidebar badge. Refreshes every 2 minutes and on 'cbs:autopilot'. */
export function useAutopilotCount() {
  const { can, user } = useAuth();
  const [n, setN] = useState(0);
  useEffect(() => {
    if (!user || !can('pipeline.view')) { setN(0); return; }
    let alive = true;
    const load = () => api<AutopilotSummary>('/autopilot/summary').then(s => { if (alive) setN(s.awaitingApproval + s.repliesToReview); }).catch(() => {});
    load();
    const t = setInterval(load, 120_000);
    window.addEventListener('cbs:autopilot', load);
    return () => { alive = false; clearInterval(t); window.removeEventListener('cbs:autopilot', load); };
  }, [user, can]);
  return n;
}
