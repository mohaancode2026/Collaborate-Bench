import { ApiError, postForm } from './api';

/**
 * Stores the resume file itself on the server, with the consultant's original resume (the one just saved, or the newest).
 * Returns null when stored, otherwise a short reason. The text is already saved, so a failure here isn't fatal.
 */
export async function storeResumeFile(consultantId: number, file: File, resumeId?: number): Promise<string | null> {
  const fd = new FormData();
  fd.set('consultantId', String(consultantId));
  if (resumeId) fd.set('resumeId', String(resumeId));
  fd.append('file', file, file.name);
  try { await postForm('/resumes/source/file', fd); return null; }
  catch (e) { return e instanceof ApiError ? e.detail : 'the upload failed.'; }
}

/** Extracts plain text from a resume file in the browser (PDF via pdf.js, DOCX via mammoth, TXT/MD as-is). */
export async function fileToText(file: File): Promise<string> {
  const n = file.name.toLowerCase();
  if (file.size > 10 * 1024 * 1024) throw new Error(`${file.name} is ${(file.size / 1048576).toFixed(1)} MB. The limit is 10 MB.`);
  if (n.endsWith('.docx')) {
    const mammoth = await import('mammoth');
    return (await mammoth.extractRawText({ arrayBuffer: await file.arrayBuffer() })).value;
  }
  if (n.endsWith('.pdf')) {
    const pdfjs = await import('pdfjs-dist');
    pdfjs.GlobalWorkerOptions.workerSrc = (await import('pdfjs-dist/build/pdf.worker.min.mjs?url')).default;
    const pdf = await pdfjs.getDocument({ data: new Uint8Array(await file.arrayBuffer()) }).promise;
    let out = '';
    for (let i = 1; i <= pdf.numPages; i++) {
      const page = await pdf.getPage(i);
      const tc = await page.getTextContent();
      // Rebuild lines from positioned text pieces. A space goes in only where there is a visible gap, so names in
      // fonts that place letters one by one ("R","A","V","I") come out as "RAVI", not "R A V I". A wide gap on the same
      // line (name on the left, phone on the right) becomes " | " so the parser can tell the parts apart.
      let lastY: number | null = null, lastEnd: number | null = null;
      for (const raw of tc.items as { str?: string; transform?: number[]; width?: number; hasEOL?: boolean }[]) {
        if (typeof raw.str !== 'string' || !raw.transform) continue;
        const [a, b, , , x, y] = raw.transform;
        const size = Math.hypot(a, b) || 10;
        if (lastY !== null && Math.abs(y - lastY) > 4) { out += '\n'; lastEnd = null; }
        else if (lastEnd !== null && raw.str && !/^\s/.test(raw.str) && !/\s$/.test(out)) {
          const gap = x - lastEnd;
          if (gap > size * 3) out += ' | ';
          else if (gap > size * 0.15) out += ' ';
        }
        out += raw.str;
        lastY = y; lastEnd = x + (raw.width ?? 0);
        if (raw.hasEOL) { out += '\n'; lastY = null; lastEnd = null; }
      }
      out += '\n';
    }
    if (out.trim().length < 30) throw new Error('This PDF has no selectable text (it looks scanned). Upload a text-based PDF or DOCX, or paste the text.');
    return out;
  }
  if (n.endsWith('.txt') || n.endsWith('.md')) return file.text();
  throw new Error('Unsupported file type. Upload a PDF, DOCX or TXT resume (save older .doc files as .docx).');
}
