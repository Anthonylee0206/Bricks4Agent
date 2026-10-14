const { useState, useEffect } = React;

function PageD({ onNavigate, initialResult = '', initialTrace = '', initialKeyword = '' }) {
  const [userId, setUserId]   = useState('');
  const [action, setAction]   = useState('');
  const [keyword, setKeyword] = useState(initialKeyword);
  const [result, setResult]   = useState(initialResult);
  const [logs, setLogs]       = useState([]);
  const [total, setTotal]     = useState(0);
  const [writeFailures, setWriteFailures] = useState(null);
  const [loading, setLoading] = useState(false);
  const [limit, setLimit]     = useState(50);
  const [offset, setOffset]   = useState(0);
  const [applied, setApplied] = useState({});

  useEffect(() => { doQuery('', '', initialKeyword, 50, 0, initialResult); }, []);

  async function doQuery(uid, act, kw, lim, off, res_) {
    const id  = uid  !== undefined ? uid  : userId;
    const a   = act  !== undefined ? act  : action;
    const k   = kw   !== undefined ? kw   : keyword;
    const l   = lim  !== undefined ? lim  : limit;
    const o   = off  !== undefined ? off  : offset;
    const r   = res_ !== undefined ? res_ : result;
    setLoading(true);
    const filters = {};
    if (id.trim())  filters.user_id = id.trim();
    if (a)          filters.action  = a;
    if (k.trim())   filters.keyword = k.trim();
    if (r)          filters.result  = r;
    const res = await apiCall('GET', 'audit', { ...filters, limit: l, offset: o });
    if (res.success) { setLogs(res.data.logs); setTotal(res.data.total); setApplied(filters); setWriteFailures(res.data.write_failure_count); }
    setLoading(false);
  }

  async function handleDeleteOne(logId) {
    const ok = await showConfirm('刪除此筆稽核日誌？', '此操作不可還原。');
    if (!ok) return;
    const res = await apiCall('DELETE', `audit/${encodeURIComponent(logId)}`);
    if (res.success) { showToast('已刪除 1 筆稽核日誌', 'success'); doQuery(userId, action, keyword, limit, offset); }
  }

  const filtered = Object.keys(applied).length > 0;
  async function handleClearAll() {
    const ok = filtered
      ? await showConfirm('清空符合篩選條件的稽核日誌？', `將永久刪除符合目前篩選條件的 ${total} 筆稽核日誌，不可還原。`)
      : await showConfirm('清空全部稽核日誌？', `將永久刪除所有使用者的全部 ${total} 筆稽核日誌，不可還原。`);
    if (!ok) return;
    const res = await apiCall('DELETE', 'audit', { confirm: 'all', ...applied });
    if (res.success) { showToast(`已清空 ${res.data.deleted} 筆稽核日誌`, 'success'); setOffset(0); doQuery(userId, action, keyword, limit, 0); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title="稽核日誌" count={total} desc="所有使用者的對話、工具呼叫與記憶變更紀錄" />
      {initialTrace && <ContentArea><TraceView traceId={initialTrace} /></ContentArea>}

      <Toolbar>
        <Input value={keyword} placeholder="關鍵字搜尋（內容 / action / user）" leftIcon="search"
          onChange={setKeyword} onKeyDown={e => e.key === 'Enter' && (setOffset(0), doQuery(userId, action, keyword, limit, 0))} style={{ width: 300 }} />
        <Input value={userId} placeholder="User ID（選填）" mono
          onChange={setUserId} onKeyDown={e => e.key === 'Enter' && (setOffset(0), doQuery(userId, action, keyword, limit, 0))} style={{ width: 240 }} />
        <Select value={action} options={ACTION_OPTIONS} onChange={setAction} style={{ width: 170 }} />
        <Select value={result} options={[{ value: '', label: '全部結果' }, { value: 'success', label: '成功' }, { value: 'error', label: '錯誤' }, { value: 'skipped', label: '略過' }]} onChange={v => { setResult(v); setOffset(0); doQuery(userId, action, keyword, limit, 0, v); }} style={{ width: 120 }} />
        <Button variant="secondary" size="sm" onClick={() => { setOffset(0); doQuery(userId, action, keyword, limit, 0); }} loading={loading}>查詢</Button>
        <div style={{ flex: 1 }} />
        <Button write="super" variant="danger" size="sm" icon="trash" onClick={handleClearAll} disabled={total === 0}>{filtered ? '清空符合條件' : '清空全部'}</Button>
      </Toolbar>

      <AuditWriteFailures count={writeFailures} />

      <div style={{ flex: 1, overflow: 'auto', background: 'var(--c-surface)' }}>
        {loading && logs.length === 0
          ? <EmptyState loading />
          : <AuditLog logs={logs} loading={loading} showUser onUserClick={uid => onNavigate('user/' + encodeURIComponent(uid))} onDelete={handleDeleteOne} />
        }
      </div>

      <Pagination total={total} limit={limit} offset={offset}
        onPage={off => { setOffset(off); doQuery(userId, action, keyword, limit, off); }}
        onLimitChange={lim => { setLimit(lim); setOffset(0); doQuery(userId, action, keyword, lim, 0); }} />
    </div>
  );
}

window.PageD = PageD;
