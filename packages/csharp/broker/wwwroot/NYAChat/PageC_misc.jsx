const { useState, useEffect } = React;

const ACTION_LABELS = {
  note_save: '保存筆記', note_restore: '還原筆記版本', forget_find: '遺忘候選', forget: '隱藏資料', unforget: '撤銷隱藏引用',
  chat_input: '使用者訊息', chat_output: 'NYA 回覆',
  fact_extract: '萃取事實（歷史）', fact_upsert: '寫入筆記', fact_rollback: '回滾筆記',
  summary_create: '產生摘要', summary_rollback: '回滾摘要', memory_delete: '刪除記憶',
  topic_create: '新話題', topic_switch: '切換話題', topic_rename: '話題改名',
  tool_call: '呼叫工具', tool_result: '工具結果', tool_toggle: '工具開關',
  role_assign: '指派身分組', role_revoke: '撤銷身分組', error: '錯誤',
  guest_message_blocked: '訪客訊息',
  soul_create: '新增 Soul', soul_update: '更新 Soul', soul_delete: '刪除 Soul',
  soul_bind: '綁定 Soul', soul_unbind: '解除 Soul 綁定', soul_reload: '重載 Soul',
  prompt_reload: '重載 Prompt', audit_purge: '清除稽核', config_update: '更新設定',
  llm_profile_upsert: '儲存模型設定', llm_profile_delete: '刪除模型設定', llm_route_set: '設定模型路由',
  config_reset: '還原對話設定', llm_request: '模型請求',
};

const HISTORICAL_ACTIONS = ['fact_extract', 'guest_message_blocked', 'role_assign', 'role_revoke'];
const ACTION_OPTIONS = [{ value: '', label: '全部動作' }]
  .concat(Object.keys(ACTION_LABELS).map(k => ({ value: k, label: ACTION_LABELS[k], group: HISTORICAL_ACTIONS.includes(k) ? '歷史動作' : '現行動作' })));

function MessageTrace({ metadata }) {
  let trace;
  try { trace = JSON.parse(metadata || '{}').trace_id; } catch { }
  return trace && nyaCanRead('audit') ? <a href={'#audit?trace=' + encodeURIComponent(trace)}>本輪 trace</a> : null;
}

function TurnContext({ raw, traceId }) {
  let detail;
  try { detail = JSON.parse(raw || '{}'); } catch { detail = {}; }
  const context = detail.context;
  return <div style={{ padding: 14 }}>
    {context && <div>實際注入：筆記 {context.facts}、摘要 {context.summaries}、近期訊息 {context.history}；估算 {context.estimated_tokens} tokens；{context.truncated ? '已截斷' : '未截斷'}</div>}
    {detail.selection ? <details><summary>當輪保存的選取明細</summary><JsonBlock raw={JSON.stringify({ selection: detail.selection, execution: detail.execution })} /></details> : <div>此舊紀錄未保存注入明細。</div>}
    {traceId && nyaCanRead('audit') && <a href={'#audit?trace=' + encodeURIComponent(traceId)}>查看本輪鏈 {traceId}</a>}
  </div>;
}

function TraceView({ traceId }) {
  const [logs, setLogs] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    setLoading(true);
    apiCall('GET', 'audit/traces/' + encodeURIComponent(traceId)).then(res => {
      if (!active) return;
      if (res.success) { setLogs(res.data.logs || []); setError(''); } else setError(res.message);
      setLoading(false);
    });
    return () => { active = false; };
  }, [traceId]);
  return <Panel title="本輪稽核鏈" subtitle={traceId}>
    {error && <div role="alert">{error}</div>}
    <AuditLog logs={logs} loading={loading} showUser emptyMessage="此 trace 沒有保存紀錄" />
  </Panel>;
}

const SOUL_REASON_LABELS = {
  bound: '使用綁定的 Soul', configured_default: '未綁定，使用設定的預設 Soul',
  bound_soul_missing: '綁定的 Soul 不存在，使用設定的預設 Soul',
  configured_default_missing: '設定的預設 Soul 不存在，使用內建備援 Soul',
  bound_and_default_missing: '綁定與預設 Soul 均不存在，使用內建備援 Soul',
};

function AuditWriteFailures({ count }) {
  return <div role={count > 0 ? 'alert' : 'status'} style={{ padding: '8px 14px', fontSize: 12, color: count > 0 ? 'var(--c-danger-fg)' : 'var(--c-fg2)' }}>
    稽核寫入失敗（本次程序累計）：{count ?? '—'}
  </div>;
}

function actionColor(a) {
  if (a === 'error' || a === 'memory_delete') return 'var(--c-danger)';
  if (a === 'tool_result')  return 'var(--c-warn)';
  if (a === 'tool_call')    return 'var(--c-warn)';
  if (a && a.startsWith('fact_')) return 'var(--c-accent-fg)';
  return 'var(--c-fg2)';
}

function detailSummary(raw) {
  try {
    const o = JSON.parse(raw);
    const parts = [];
    if (o.message) parts.push(o.message);
    if (o.tool_name) parts.push(o.tool_name);
    if (o.output) parts.push('工具結果截斷預覽：' + o.output);
    if (o.from_topic_id || o.to_topic_id) parts.push(`${o.from_topic_id || '—'} → ${o.to_topic_id || '—'}`);
    if (o.context) parts.push(`筆記 ${o.context.facts}／摘要 ${o.context.summaries}／history ${o.context.history}；${o.context.estimated_tokens} tokens；${o.context.truncated ? '已截斷' : '未截斷'}`);
    if (o.reply_preview)   parts.push(o.reply_preview);
    if (o.content_preview) parts.push(o.content_preview);
    if (o.error)           parts.push(`錯誤：${o.error}`);
    if (o.fact_key) {
      const keyStr = o.category ? `${o.category}/${o.fact_key}` : o.fact_key;
      parts.push(o.new_version != null ? `${keyStr} → v${o.new_version}` : keyStr);
    }
    if (o.from_version != null && o.to_version != null)
      parts.push(`v${o.from_version} → v${o.to_version}`);
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

  let toolOutput = null;
  if ((isToolResultErr || isToolResultSkipped) && log.detail_json) {
    try {
      const o = JSON.parse(log.detail_json);
      toolOutput = o.output || null;
    } catch { }
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
          {log.trace_id && <a href={'#audit?trace=' + encodeURIComponent(log.trace_id)}>本輪鏈：{log.trace_id}</a>}
          {log.action === 'chat_input' && <TurnContext raw={log.detail_json} traceId={log.trace_id} />}

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
              <Button write="super" variant="danger" size="sm" icon="trash" onClick={() => onDelete(log.log_id)}>刪除此筆</Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

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

function TabC6({ userId, initialTrace = '' }) {
  const [logs, setLogs]       = useState([]);
  const [total, setTotal]     = useState(0);
  const [writeFailures, setWriteFailures] = useState(null);
  const [loading, setLoading] = useState(false);
  const [action, setAction]   = useState('');
  const [keyword, setKeyword] = useState('');
  const [result, setResult] = useState('');
  const [limit, setLimit]     = useState(50);
  const [offset, setOffset]   = useState(0);
  const [applied, setApplied] = useState({});

  async function load(act, kw, lim, off, resultFilter = result) {
    setLoading(true);
    const filters = {};
    if (act) filters.action = act;
    if (resultFilter) filters.result = resultFilter;
    if (kw && kw.trim()) filters.keyword = kw.trim();
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/audit`, { ...filters, limit: lim, offset: off });
    if (res.success) { setLogs(res.data.logs); setTotal(res.data.total); setApplied(filters); setWriteFailures(res.data.write_failure_count); }
    setLoading(false);
  }

  useEffect(() => { load(action, keyword, limit, offset); }, [userId]);
  function doQuery(act) { const a = act !== undefined ? act : action; setOffset(0); load(a, keyword, limit, 0); }

  async function handleDeleteOne(logId) {
    const ok = await showConfirm('刪除此筆稽核日誌？', '此操作不可還原。');
    if (!ok) return;
    const res = await apiCall('DELETE', `audit/${encodeURIComponent(logId)}`);
    if (res.success) { showToast('已刪除 1 筆稽核日誌', 'success'); load(action, keyword, limit, offset); }
  }

  const filtered = Object.keys(applied).length > 0;
  async function handleClearUser() {
    const ok = filtered
      ? await showConfirm('清空符合篩選條件的稽核日誌？', `將永久刪除此用戶符合目前篩選條件的 ${total} 筆稽核日誌，不可還原。`)
      : await showConfirm('清空此用戶全部稽核日誌？', `將永久刪除此用戶的全部 ${total} 筆稽核日誌，不可還原。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `users/${encodeURIComponent(userId)}/audit`, { confirm: 'all', ...applied });
    if (res.success) { showToast(`已清空 ${res.data.deleted} 筆稽核日誌`, 'success'); setOffset(0); load(action, keyword, limit, 0); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: 'var(--c-bg)' }}>
      {initialTrace && <ContentArea><TraceView traceId={initialTrace} /></ContentArea>}
      <Toolbar>
        <Input value={keyword} placeholder="關鍵字搜尋（內容 / action）" leftIcon="search"
          onChange={setKeyword} onKeyDown={e => e.key === 'Enter' && doQuery()} style={{ width: 260 }} />
        <Select value={action} options={ACTION_OPTIONS} onChange={v => { setAction(v); doQuery(v); }} style={{ width: 170 }} />
        <Select value={result} options={[{ value: '', label: '全部結果' }, { value: 'success', label: '成功' }, { value: 'error', label: '錯誤' }, { value: 'skipped', label: '略過' }]} onChange={v => { setResult(v); setOffset(0); load(action, keyword, limit, 0, v); }} />
        <Button variant="ghost" size="sm" icon="refresh" onClick={() => doQuery()} loading={loading}>重新整理</Button>
        <div style={{ flex: 1 }} />
        <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)' }}>{total} 筆</span>
        <Button write="super" variant="danger" size="sm" icon="trash" onClick={handleClearUser} disabled={total === 0}>{filtered ? '清空符合條件' : '清空此用戶'}</Button>
      </Toolbar>

      <AuditWriteFailures count={writeFailures} />

      <div style={{ flex: 1, overflow: 'auto', background: 'var(--c-surface)' }}>
        <AuditLog logs={logs} loading={loading} onDelete={handleDeleteOne} />
      </div>

      <Pagination total={total} limit={limit} offset={offset}
        onPage={off => { setOffset(off); load(action, keyword, limit, off); }}
        onLimitChange={lim => { setLimit(lim); setOffset(0); load(action, keyword, lim, 0); }} />
    </div>
  );
}

function SoulBindPanel({ userId, availableSouls, currentSoulId, effectiveSoulId, soulReason, onSoulChanged }) {
  const [souls, setSouls]        = useState([]);
  const [selected, setSelected]  = useState(currentSoulId || '');
  const [loading, setLoading]    = useState(false);
  const [applyLoading, setApply] = useState(false);

  useEffect(() => {
    setSouls(availableSouls);
    setSelected(s => s || (availableSouls[0] || {}).soul_id || '');
    setLoading(false);
  }, [availableSouls]);
  useEffect(() => { setSelected(currentSoulId || ''); }, [currentSoulId]);

  async function handleApply() {
    setApply(true);
    const res = await apiCall('POST', `users/${encodeURIComponent(userId)}/soul`, { soul_id: selected });
    if (res.success) { showToast('Soul 綁定成功', 'success'); onSoulChanged && onSoulChanged(selected); }
    setApply(false);
  }

  async function handleUnbind() {
    setApply(true);
    const res = await apiCall('DELETE', `users/${encodeURIComponent(userId)}/soul`);
    if (res.success) { showToast('已解除綁定，改回預設 Soul', 'success'); onSoulChanged && onSoulChanged(null); }
    setApply(false);
  }

  return (
    <Panel title="Soul 綁定">
      <div style={{ padding: 14, display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <span style={{ fontSize: 12, color: 'var(--c-fg2)', width: 52, flexShrink: 0 }}>綁定</span>
          <Badge color="var(--c-fg)" mono>{currentSoulId ?? '未綁定'}</Badge>
          <Button write="user" variant="secondary" size="sm" loading={applyLoading} disabled={!currentSoulId} onClick={handleUnbind}>改回預設</Button>
        </div>
        <div style={{ fontSize: 12, color: 'var(--c-fg2)' }}>生效：{effectiveSoulId} · {SOUL_REASON_LABELS[soulReason] || soulReason}</div>
        {loading
          ? <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--c-fg2)' }}><Spinner size={13} /><span style={{ fontSize: 13 }}>載入 Soul 清單…</span></div>
          : <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{ fontSize: 12, color: 'var(--c-fg2)', width: 52, flexShrink: 0 }}>變更</span>
              <Select value={selected} options={souls.map(s => ({ value: s.soul_id, label: `${s.display_name}（${s.soul_id}）` }))}
                onChange={setSelected} style={{ flex: 1 }} />
              <Button write="user" variant="primary" size="sm" loading={applyLoading} onClick={handleApply}
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
window.SOUL_REASON_LABELS = SOUL_REASON_LABELS;
window.AuditWriteFailures = AuditWriteFailures;
window.SoulBindPanel = SoulBindPanel;
