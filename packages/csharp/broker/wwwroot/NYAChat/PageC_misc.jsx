/* Page C — 補充頁籤：C6 稽核日誌、Soul 綁定面板
   AuditLog is a shared log-stream component reused by PageD. */
const { useState, useEffect } = React;

const ACTION_LABELS = {
  chat_input: '使用者訊息', chat_output: 'NYA 回覆',
  fact_extract: '萃取事實', fact_upsert: '寫入事實', fact_rollback: '回滾事實',
  summary_create: '產生摘要', summary_rollback: '回滾摘要', memory_delete: '刪除記憶',
  topic_create: '新話題', topic_switch: '切換話題', topic_rename: '話題改名',
  tool_call: '呼叫工具', tool_result: '工具結果', tool_toggle: '工具開關',
  role_assign: '指派身分組', role_revoke: '撤銷身分組', error: '錯誤',
  guest_message_blocked: '訪客訊息',
};

const ACTION_OPTIONS = [{ value: '', label: '全部動作' }]
  .concat(Object.keys(ACTION_LABELS).filter(k => k !== 'guest_message_blocked').map(k => ({ value: k, label: ACTION_LABELS[k] })));

function actionColor(a) {
  if (a === 'error' || a === 'memory_delete') return 'var(--c-danger)';
  if (a === 'tool_result')  return 'var(--c-warn)';
  if (a === 'tool_call')    return 'var(--c-warn)';
  if (a && a.startsWith('fact_')) return 'var(--c-accent-fg)';
  return 'var(--c-fg2)';
}

/* One-line inline summary parsed from DetailJson */
function detailSummary(raw) {
  try {
    const o = JSON.parse(raw);
    const parts = [];
    if (o.reply_preview)   parts.push(o.reply_preview);
    if (o.content_preview) parts.push(o.content_preview);
    if (o.error)           parts.push(`錯誤：${o.error}`);
    // fact_upsert: show category/fact_key v{new_version}
    if (o.fact_key) {
      const keyStr = o.category ? `${o.category}/${o.fact_key}` : o.fact_key;
      parts.push(o.new_version != null ? `${keyStr} → v${o.new_version}` : keyStr);
    }
    // fact_rollback: show from→to version
    if (o.from_version != null && o.to_version != null)
      parts.push(`v${o.from_version} → v${o.to_version}`);
    // fact_extract: show extracted keys (up to 4)
    if (Array.isArray(o.keys) && o.keys.length > 0) {
      const preview = o.keys.slice(0, 4).join(', ') + (o.keys.length > 4 ? `… (+${o.keys.length - 4})` : '');
      parts.push(preview);
    }
    if (o.stored != null) parts.push(`stored=${o.stored}${o.skipped != null ? ` skipped=${o.skipped}` : ''}`);
    if (o.message_count != null) parts.push(`${o.message_count} msgs`);
    if (o.topic_id)        parts.push(o.topic_id);
    if (o.role_id)         parts.push(o.role_id);
    if (o.target_type)     parts.push(o.target_type + (o.target_id ? `(${o.target_id})` : ''));
    return parts.join(' · ') || '—';
  } catch { return raw || '—'; }
}
function detailMeta(raw) {
  try {
    const o = JSON.parse(raw);
    const m = [];
    if (o.model)          m.push(o.model);
    if (o.latency_ms != null) m.push(`${o.latency_ms}ms`);
    return m;
  } catch { return []; }
}

function AuditRow({ log, showUser, onUserClick, onDelete }) {
  const [open, setOpen] = useState(false);
  const meta = detailMeta(log.detail_json);
  const isErr     = log.result === 'error';
  const isSkipped = log.result === 'skipped';
  const isToolResultErr = log.action === 'tool_result' && isErr;
  const isToolResultSkipped = log.action === 'tool_result' && isSkipped;

  // Parse tool output for alert box
  let toolOutput = null;
  if ((isToolResultErr || isToolResultSkipped) && log.detail_json) {
    try {
      const o = JSON.parse(log.detail_json);
      toolOutput = o.output || null;
    } catch { /* ignore */ }
  }

  return (
    <div style={{ borderBottom: '1px solid var(--c-border-soft)' }}>
      <div onClick={() => setOpen(o => !o)}
        style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '9px 14px', cursor: 'pointer',
          background: open ? 'var(--c-bg)' : 'var(--c-surface)' }}>
        <Icon name={open ? 'chevronDown' : 'chevronRight'} size={13} color="var(--c-fg3)" style={{ flexShrink: 0 }} />
        <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg2)', width: 132, flexShrink: 0, whiteSpace: 'nowrap' }}>
          {fmtTime(log.created_at)}
        </span>
        {showUser && (
          <button onClick={e => { e.stopPropagation(); onUserClick && onUserClick(log.user_id); }}
            className="mono" style={{ fontSize: 12, color: 'var(--c-accent-fg)', background: 'none', border: 'none', padding: 0, cursor: 'pointer', width: 170, flexShrink: 0, textAlign: 'left', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {log.user_id}
          </button>
        )}
        <span style={{ width: 150, flexShrink: 0 }}>
          <Badge color={actionColor(log.action)}>{ACTION_LABELS[log.action] || log.action}</Badge>
        </span>
        <span style={{ flexShrink: 0, width: 40, fontSize: 12, color: isErr ? 'var(--c-danger-fg)' : isSkipped ? 'var(--c-warn)' : 'var(--c-fg3)' }}>
          {isErr ? '失敗' : isSkipped ? '略過' : '成功'}
        </span>
        <span style={{ flex: 1, minWidth: 0, fontSize: 12, color: isErr ? 'var(--c-danger-fg)' : isSkipped ? 'var(--c-warn)' : 'var(--c-fg2)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {detailSummary(log.detail_json)}
        </span>
        {meta.length > 0 && (
          <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)', flexShrink: 0, whiteSpace: 'nowrap' }}>
            {meta.join(' · ')}
          </span>
        )}
      </div>
      {open && (
        <div style={{ padding: '0 14px 12px 39px', background: 'var(--c-bg)' }}>
          <div className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)', padding: '8px 0 4px' }}>{log.log_id}</div>

          {/* 工具執行失敗：紅色警示框 */}
          {isToolResultErr && (
            <div style={{
              marginBottom: 8,
              padding: '10px 14px',
              background: 'var(--c-danger-soft)',
              border: '1px solid var(--c-danger-soft)',
              borderRadius: 6,
              display: 'flex',
              alignItems: 'flex-start',
              gap: 10
            }}>
              <Icon name="alertTriangle" size={15} color="var(--c-danger)" style={{ flexShrink: 0, marginTop: 1 }} />
              <div>
                <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--c-danger-fg)', marginBottom: 2 }}>工具執行失敗</div>
                <div className="mono" style={{ fontSize: 12, color: 'var(--c-danger-fg)', wordBreak: 'break-all' }}>
                  {toolOutput || '未知錯誤'}
                </div>
              </div>
            </div>
          )}

          {/* 工具被跳過：黃色提示框 */}
          {isToolResultSkipped && (
            <div style={{
              marginBottom: 8,
              padding: '10px 14px',
              background: 'var(--c-warn-soft)',
              border: '1px solid var(--c-warn-soft)',
              borderRadius: 6,
              display: 'flex',
              alignItems: 'flex-start',
              gap: 10
            }}>
              <Icon name="info" size={15} color="var(--c-warn)" style={{ flexShrink: 0, marginTop: 1 }} />
              <div>
                <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--c-warn)', marginBottom: 2 }}>工具被跳過</div>
                <div className="mono" style={{ fontSize: 12, color: 'var(--c-warn)', wordBreak: 'break-all' }}>
                  {toolOutput || '被跳過'}
                </div>
              </div>
            </div>
          )}

          <div style={{ border: '1px solid var(--c-border)', borderRadius: 6, overflow: 'hidden' }}><JsonBlock raw={log.detail_json} flush /></div>

          {onDelete && (
            <div style={{ display: 'flex', justifyContent: 'flex-end', paddingTop: 8 }}>
              <Button variant="danger" size="sm" icon="trash" onClick={() => onDelete(log.log_id)}>刪除此筆</Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/* Shared log-stream list */
function AuditLog({ logs, loading, showUser, onUserClick, onDelete, emptyMessage = '無稽核日誌', emptyHint }) {
  if (loading && (!logs || logs.length === 0)) return <EmptyState loading />;
  if (!logs || logs.length === 0) return <EmptyState icon="log" message={emptyMessage} hint={emptyHint} />;
  return (
    <div>
      {logs.map(log => (
        <AuditRow key={(log.user_id || '') + log.log_id} log={log} showUser={showUser} onUserClick={onUserClick} onDelete={onDelete} />
      ))}
    </div>
  );
}

/* ── C6：用戶稽核日誌 ─────────────────────────────────────── */
function TabC6({ userId }) {
  const [logs, setLogs]       = useState([]);
  const [total, setTotal]     = useState(0);
  const [loading, setLoading] = useState(false);
  const [action, setAction]   = useState('');
  const [keyword, setKeyword] = useState('');
  const [limit, setLimit]     = useState(50);
  const [offset, setOffset]   = useState(0);
  const [applied, setApplied] = useState({}); // 列表目前實際套用的篩選；「清空」以此為範圍

  async function load(act, kw, lim, off) {
    setLoading(true);
    // 大項#6：稽核日誌改以關鍵字搜尋（比對 detail_json / action / user_id）。
    const filters = {};
    if (act) filters.action = act;
    if (kw && kw.trim()) filters.keyword = kw.trim();
    const res = await apiCall('GET', `users/${userId}/audit`, { ...filters, limit: lim, offset: off });
    if (res.success) { setLogs(res.data.logs); setTotal(res.data.total); setApplied(filters); }
    setLoading(false);
  }

  useEffect(() => { load(action, keyword, limit, offset); }, [userId]);
  function doQuery(act) { const a = act !== undefined ? act : action; setOffset(0); load(a, keyword, limit, 0); }

  // 大項#3：刪除單筆 / 清空此用戶（管理端專用）。
  async function handleDeleteOne(logId) {
    const ok = await showConfirm('刪除此筆稽核日誌？', '此操作不可還原。');
    if (!ok) return;
    const res = await apiCall('DELETE', `audit/${logId}`);
    if (res.success) { showToast('已刪除 1 筆稽核日誌', 'success'); load(action, keyword, limit, offset); }
  }

  const filtered = Object.keys(applied).length > 0;
  async function handleClearUser() {
    const ok = filtered
      ? await showConfirm('清空符合篩選條件的稽核日誌？', `將永久刪除此用戶符合目前篩選條件的 ${total} 筆稽核日誌，不可還原。`)
      : await showConfirm('清空此用戶全部稽核日誌？', `將永久刪除此用戶的全部 ${total} 筆稽核日誌，不可還原。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `users/${userId}/audit`, { confirm: 'all', ...applied });
    if (res.success) { showToast(`已清空 ${res.data.deleted} 筆稽核日誌`, 'success'); setOffset(0); load(action, keyword, limit, 0); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: 'var(--c-bg)' }}>
      <Toolbar>
        <Input value={keyword} placeholder="關鍵字搜尋（內容 / action）" leftIcon="search"
          onChange={setKeyword} onKeyDown={e => e.key === 'Enter' && doQuery()} style={{ width: 260 }} />
        <Select value={action} options={ACTION_OPTIONS} onChange={v => { setAction(v); doQuery(v); }} style={{ width: 170 }} />
        <Button variant="ghost" size="sm" icon="refresh" onClick={() => doQuery()} loading={loading}>重新整理</Button>
        <div style={{ flex: 1 }} />
        <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)' }}>{total} 筆</span>
        <Button variant="danger" size="sm" icon="trash" onClick={handleClearUser} disabled={total === 0}>{filtered ? '清空符合條件' : '清空此用戶'}</Button>
      </Toolbar>

      <div style={{ flex: 1, overflow: 'auto', background: 'var(--c-surface)' }}>
        <AuditLog logs={logs} loading={loading} onDelete={handleDeleteOne} />
      </div>

      <Pagination total={total} limit={limit} offset={offset}
        onPage={off => { setOffset(off); load(action, keyword, limit, off); }}
        onLimitChange={lim => { setLimit(lim); setOffset(0); load(action, keyword, lim, 0); }} />
    </div>
  );
}

/* ── Soul 綁定面板（內嵌於概覽） ──────────────────────────── */
function SoulBindPanel({ userId, currentSoulId, onSoulChanged }) {
  const [souls, setSouls]        = useState([]);
  const [selected, setSelected]  = useState(currentSoulId || '');
  const [loading, setLoading]    = useState(false);
  const [applyLoading, setApply] = useState(false);

  useEffect(() => {
    setLoading(true);
    apiCall('GET', 'souls').then(res => {
      if (res.success) { setSouls(res.data.souls); setSelected(s => s || (res.data.souls[0] || {}).id || ''); }
      setLoading(false);
    });
  }, []);
  useEffect(() => { setSelected(currentSoulId || ''); }, [currentSoulId]);

  async function handleApply() {
    setApply(true);
    const res = await apiCall('POST', `users/${userId}/soul`, { soul_id: selected });
    if (res.success) { showToast('Soul 綁定成功', 'success'); onSoulChanged && onSoulChanged(selected); }
    setApply(false);
  }

  return (
    <Panel title="Soul 綁定">
      <div style={{ padding: 14, display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <span style={{ fontSize: 12, color: 'var(--c-fg2)', width: 52, flexShrink: 0 }}>目前</span>
          <Badge color="var(--c-fg)" mono>{currentSoulId || 'default'}</Badge>
        </div>
        {loading
          ? <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--c-fg2)' }}><Spinner size={13} /><span style={{ fontSize: 13 }}>載入 Soul 清單…</span></div>
          : <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{ fontSize: 12, color: 'var(--c-fg2)', width: 52, flexShrink: 0 }}>變更</span>
              <Select value={selected} options={souls.map(s => ({ value: s.id, label: `${s.display_name}（${s.id}）` }))}
                onChange={setSelected} style={{ flex: 1 }} />
              <Button variant="primary" size="sm" loading={applyLoading} onClick={handleApply}
                disabled={!selected || selected === currentSoulId}>套用</Button>
            </div>
        }
      </div>
    </Panel>
  );
}

window.TabC6 = TabC6;
window.AuditLog = AuditLog;
window.ACTION_OPTIONS = ACTION_OPTIONS;
window.ACTION_LABELS = ACTION_LABELS;
window.SoulBindPanel = SoulBindPanel;
