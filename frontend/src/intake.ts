/**
 * Reads the consultant onboarding form recruiters paste ("Full Legal Name: …", "Google Voice No: …"), with the value on the
 * same line or on the next line(s). Returns the profile fields and the basic consultant fields it found. "NA" / "N/A" count as empty.
 */
export interface IntakeResult {
  profile: Partial<{
    legalName: string; marketingTechnology: string; keywords: string; googleVoice: string; personalPhone: string; dateOfBirth: string;
    ssnLast4: string; idType: string; bachelors: string; masters: string; locationConstraints: string; currentProjectEnd: string; doNotSubmit: string;
  }>;
  basics: Partial<{ email: string; visa: string; location: string; linkedInUrl: string; exp: number; usExp: number; certifications: string; passportNumber: string }>;
  found: number;
}

type Key = keyof IntakeResult['profile'] | `b:${keyof IntakeResult['basics']}`;
const LABELS: [RegExp, Key][] = [
  [/^any certifications?/i, 'b:certifications'],
  [/^current project end/i, 'currentProjectEnd'],
  [/^full legal name/i, 'legalName'],
  [/^technology( for marketing)?/i, 'marketingTechnology'],
  [/^google voice/i, 'googleVoice'],
  [/^(original )?cell( phone)?( number)?|^personal (phone|number)/i, 'personalPhone'],
  [/^e-?mail( id)?/i, 'b:email'],
  [/^(dob|date of birth)/i, 'dateOfBirth'],
  [/^location constraints?/i, 'locationConstraints'],
  [/^keywords?/i, 'keywords'],
  [/^(visa )?status/i, 'b:visa'],
  [/^bachelors?/i, 'bachelors'],
  [/^masters?/i, 'masters'],
  [/^ssn/i, 'ssnLast4'],
  [/^passport( number| no)?/i, 'b:passportNumber'],
  [/^driver.?s license|^state id/i, 'idType'],
  [/^current location/i, 'b:location'],
  [/^linked ?in/i, 'b:linkedInUrl'],
  [/^total experience/i, 'b:exp'],
  [/^(usa?|us) experience/i, 'b:usExp'],
  [/^vendors?\/?clients? not to be submitted|^do not submit/i, 'doNotSubmit'],
];

const empty = (v: string) => !v || /^(na|n\/a|none|nil|-+)$/i.test(v.trim());
const labelOf = (line: string) => (line.includes(':') ? LABELS.find(([re]) => re.test(line.trim()))?.[1] : undefined);

/** "Initial OPT" → OPT, "STEM OPT extension" → STEM OPT, "H1" → H1B … (the bench's visa list). */
function visa(v: string) {
  const t = v.toUpperCase();
  if (/STEM/.test(t)) return 'STEM OPT';
  if (/OPT/.test(t)) return 'OPT';
  if (/CPT/.test(t)) return 'CPT';
  if (/H-?4/.test(t)) return 'H4-EAD';
  if (/L-?2/.test(t)) return 'L2-EAD';
  if (/H-?1/.test(t)) return 'H1B';
  if (/GC.?EAD/.test(t)) return 'GC-EAD';
  if (/\bGC\b|GREEN CARD/.test(t)) return 'GC';
  if (/USC|CITIZEN/.test(t)) return 'USC';
  if (/\bTN\b/.test(t)) return 'TN';
  if (/EAD/.test(t)) return 'EAD';
  return '';
}

export function parseIntake(text: string): IntakeResult {
  const lines = text.replace(/\r/g, '').split('\n').map(l => l.replace(/ /g, ' ').trim());
  const raw: Partial<Record<Key, string>> = {};
  for (let i = 0; i < lines.length; i++) {
    const key = labelOf(lines[i]);
    if (!key) continue;
    // Value after the colon (minus a trailing "Attach" hint), else the following non-label lines.
    let v = lines[i].slice(lines[i].indexOf(':') + 1).replace(/\battach\b/i, '').trim();
    if (!v) {
      const parts: string[] = [];
      for (let j = i + 1; j < lines.length && !labelOf(lines[j]); j++) if (lines[j]) parts.push(lines[j]);
      v = parts.join(' ').trim();
    }
    if (!empty(v) && raw[key] === undefined) raw[key] = v;
  }

  const r: IntakeResult = { profile: {}, basics: {}, found: 0 };
  for (const [k, v] of Object.entries(raw) as [Key, string][]) {
    if (k === 'dateOfBirth') {
      const m = v.match(/(\d{1,2})[/-](\d{1,2})[/-](\d{4})/);
      if (m) r.profile.dateOfBirth = `${m[3]}-${m[1].padStart(2, '0')}-${m[2].padStart(2, '0')}`;
    } else if (k === 'ssnLast4') {
      const d = v.replace(/\D/g, ''); if (d.length >= 4) r.profile.ssnLast4 = d.slice(-4);
    } else if (k === 'idType') {
      r.profile.idType = /state/i.test(v) ? 'State ID' : /driv|dl|yes/i.test(v) ? "Driver's License" : /no/i.test(v) ? 'None' : undefined;
    } else if (k === 'b:visa') {
      const vv = visa(v); if (vv) r.basics.visa = vv;
    } else if (k === 'b:exp' || k === 'b:usExp') {
      const n = Number(v.match(/\d+(\.\d+)?/)?.[0]); if (!Number.isNaN(n)) r.basics[k === 'b:exp' ? 'exp' : 'usExp'] = Math.round(n);
    } else if (k === 'b:passportNumber') {
      r.basics.passportNumber = v.replace(/\s+/g, '').toUpperCase();
    } else if (k.startsWith('b:')) {
      (r.basics as Record<string, string>)[k.slice(2)] = v;
    } else {
      (r.profile as Record<string, string>)[k] = v;
    }
  }
  r.found = Object.keys(r.profile).length + Object.keys(r.basics).length;
  return r;
}
