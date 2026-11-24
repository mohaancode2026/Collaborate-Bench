/** Result of a paged list endpoint. */
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; }

/**
 * Pager under a table: "Showing 26–50 of 132", Previous / page numbers / Next, and the page size.
 * Pages are 1-based. Numbers around the current page, with the first and last always shown.
 */
export function Pager({ page, pageSize, total, onPage, onPageSize, sizes = [25, 50, 100], busy }: {
  page: number; pageSize: number; total: number; onPage: (p: number) => void; onPageSize?: (n: number) => void; sizes?: number[]; busy?: boolean;
}) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (total === 0) return null;
  const from = (page - 1) * pageSize + 1, to = Math.min(total, page * pageSize);
  const nums = [...new Set([1, page - 1, page, page + 1, pages].filter(n => n >= 1 && n <= pages))].sort((a, b) => a - b);
  return (
    <nav className="pager" aria-label="Pages">
      <span className="muted">Showing <b>{from}–{to}</b> of <b>{total}</b>{busy ? ' · loading…' : ''}</span>
      <div className="pager-btns">
        <button className="btn btn-sm btn-outline" disabled={page <= 1 || busy} onClick={() => onPage(page - 1)} aria-label="Previous page">‹ Prev</button>
        {nums.map((n, i) => (
          <span key={n} style={{ display: 'contents' }}>
            {i > 0 && n - nums[i - 1] > 1 && <span className="muted" aria-hidden="true">…</span>}
            <button className={`btn btn-sm ${n === page ? 'btn-primary' : 'btn-outline'}`} aria-current={n === page ? 'page' : undefined} disabled={busy} onClick={() => onPage(n)}>{n}</button>
          </span>))}
        <button className="btn btn-sm btn-outline" disabled={page >= pages || busy} onClick={() => onPage(page + 1)} aria-label="Next page">Next ›</button>
      </div>
      {onPageSize && <label className="muted pager-size">Rows
        <select className="inp" value={pageSize} onChange={e => onPageSize(Number(e.target.value))}>{sizes.map(s => <option key={s} value={s}>{s}</option>)}</select></label>}
    </nav>
  );
}
