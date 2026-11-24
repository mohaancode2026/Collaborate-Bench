/**
 * What each role is called on screen. The keys are the role ids the server uses for permissions (unchanged);
 * the values are the titles the company uses.
 */
export const ROLE_LABEL: Record<string, string> = {
  Admin: 'Admin',
  'Delivery Manager': 'Director of Sales',
  Manager: 'Technical Coordinator',
  'Team Lead': 'Team Lead',
  Recruiter: 'Bench Sales Executive',
};

export const roleLabel = (role?: string | null) => (role ? ROLE_LABEL[role] ?? role : '');
