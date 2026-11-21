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
      let lastY: number | null = null;
      for (const it of tc.items as { str: string; transform: number[] }[]) {
        const y = it.transform[5];
        if (lastY !== null && Math.abs(y - lastY) > 4) out += '\n';
        out += it.str + ' '; lastY = y;
      }
      out += '\n';
    }
    if (out.trim().length < 30) throw new Error('This PDF has no selectable text (it looks scanned). Upload a text-based PDF or DOCX, or paste the text.');
    return out;
  }
  if (n.endsWith('.txt') || n.endsWith('.md')) return file.text();
  throw new Error('Unsupported file type. Upload a PDF, DOCX or TXT resume (save older .doc files as .docx).');
}
