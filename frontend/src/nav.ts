/** hidden: not shown in the sidebar, but the page still opens from links inside the app. */
export const NAV: { path: string; label: string; perm: string; group: string; icon: string; hidden?: boolean }[] = [
  { path: '/dashboard', label: 'Dashboard', perm: 'dash.view', group: 'Overview', icon: 'M3 3h7v9H3zM14 3h7v5h-7zM14 12h7v9h-7zM3 16h7v5H3z' },
  { path: '/inbox', label: 'AI Inbox', perm: 'mail.view', group: 'Overview', icon: 'M22 12h-6l-2 3h-4l-2-3H2M5.45 5.11L2 12v6a2 2 0 002 2h16a2 2 0 002-2v-6l-3.45-6.89A2 2 0 0016.76 4H7.24a2 2 0 00-1.79 1.11z' },
  { path: '/autopilot', label: 'Autopilot', perm: 'pipeline.view', group: 'Overview', hidden: true, icon: 'M13 2L3 14h9l-1 8 10-12h-9l1-8z' },
  { path: '/bench', label: 'Bench & Hotlist', perm: 'bench.view', group: 'Bench', icon: 'M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2M9 11a4 4 0 100-8 4 4 0 000 8z' },
  { path: '/linkedin', label: 'LinkedIn Leads', perm: 'leads.view', group: 'Bench', hidden: true, icon: 'M16 8a6 6 0 016 6v7h-4v-7a2 2 0 00-4 0v7h-4v-7a6 6 0 016-6zM2 9h4v12H2zM4 6a2 2 0 100-4 2 2 0 000 4z' },
  { path: '/parser', label: 'AI Resume Parser', perm: 'parser.use', group: 'Bench', icon: 'M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8zM14 2v6h6' },
  { path: '/resume-builder', label: 'AI Resume Builder', perm: 'parser.use', group: 'Bench', icon: 'M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8zM14 2v6h6M9 15l2 2 4-4' },
  { path: '/jobs', label: 'Jobs & Posting Agent', perm: 'jobs.view', group: 'Requirements', hidden: true, icon: 'M2 7h20v14H2zM16 7V5a2 2 0 00-2-2h-4a2 2 0 00-2 2v2' },
  { path: '/board', label: 'Job Board Search', perm: 'board.view', group: 'Requirements', hidden: true, icon: 'M11 18a7 7 0 100-14 7 7 0 000 14zM21 21l-4.3-4.3' },
  { path: '/external-jobs', label: 'External Jobs', perm: 'board.view', group: 'Requirements', icon: 'M12 22a10 10 0 100-20 10 10 0 000 20zM2 12h20M12 2a15 15 0 010 20M12 2a15 15 0 000 20' },
  { path: '/match', label: 'Match Agent', perm: 'match.view', group: 'Requirements', hidden: true, icon: 'M12 2l3 7h7l-5.5 4.5 2 7.5-6.5-4.5L5.5 21l2-7.5L2 9h7z' },
  { path: '/vendor-mail', label: 'Vendor Emails', perm: 'mail.view', group: 'Delivery', icon: 'M4 4h16a2 2 0 012 2v12a2 2 0 01-2 2H4a2 2 0 01-2-2V6a2 2 0 012-2zM22 6l-10 7L2 6' },
  { path: '/pipeline', label: 'Submissions', perm: 'pipeline.view', group: 'Delivery', icon: 'M3 3h5v18H3zM10 3h5v12h-5zM17 3h4v8h-4z' },
  { path: '/onboarding', label: 'Onboarding & Billing', perm: 'onboard.view', group: 'Delivery', hidden: true, icon: 'M9 11l3 3L22 4M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11' },
  { path: '/rates', label: 'Rate & Margin', perm: 'rates.view', group: 'Delivery', hidden: true, icon: 'M12 1v22M17 5H9.5a3.5 3.5 0 000 7h5a3.5 3.5 0 010 7H6' },
  { path: '/team', label: 'Team & Access', perm: 'team.view', group: 'Delivery', icon: 'M9 11a4 4 0 100-8 4 4 0 000 8zM17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2M23 21v-2a4 4 0 00-3-3.9' },
];
