const { useState, useEffect } = React;

const HOME_TASKS = [
  { title: '查某位使用者的記憶', desc: '聊天室、訊息、筆記與遺忘紀錄', target: 'users',              icon: 'users' },
  { title: '看最近的錯誤',       desc: '工具失敗、模型呼叫失敗等', target: 'audit?result=error', icon: 'alertTriangle' },
  { title: '查稽核日誌',         desc: '依使用者、動作或關鍵字篩選', target: 'audit',            icon: 'log' },
  { title: '調整人格（Soul）',   desc: '語氣、規則、禁止事項',     target: 'souls',              icon: 'soul' },
  { title: '開關工具',           desc: '搜尋、交通、記憶與話題工具', target: 'tools',             icon: 'zap' },
  { title: '更換模型或 API Key', desc: 'LLM profile 與呼叫點路由',  target: 'llm',               icon: 'cpu' },
];

function HomeLink({ label, onClick }) {
  return (
    <button onClick={onClick} style={{
      border: 'none', background: 'none', padding: 0, cursor: 'pointer',
      color: 'var(--c-accent-fg)', fontSize: 13, display: 'inline-flex', alignItems: 'center', gap: 4,
    }}>{label}<Icon name="arrowRight" size={13} /></button>
  );
}

function PageHome({ onNavigate, status, onRefreshStatus }) {
  const [users, setUsers]   = useState(null);
  const [errors, setErrors] = useState(null);
  const [tools, setTools]   = useState(null);
  const [loading, setLoading] = useState(false);

  async function load() {
    setLoading(true);
    const [u, e, t] = await Promise.all([
      nyaCanRead('users') ? apiCall('GET', 'users', { limit: 8, offset: 0 }) : Promise.resolve({ success: false }),
      nyaCanRead('audit') ? apiCall('GET', 'audit', { result: 'error', limit: 6, offset: 0 }) : Promise.resolve({ success: false }),
      apiCall('GET', 'tools'),
      onRefreshStatus(),
    ]);
    if (u.success) setUsers(u.data);
    if (e.success) setErrors(e.data);
    if (t.success) setTools(t.data.tools);
    setLoading(false);
  }

  useEffect(() => { load(); }, []);

  const enabledTools = tools ? tools.filter(t => t.enabled).length : null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, overflow: 'hidden' }}>
      <PageHeader title="總覽" desc="NyaChat 對話引擎的狀態與常用入口">
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={load}>重新整理</Button>
      </PageHeader>

      <ContentArea>
        <StatTiles cols={4} items={[
          { label: '對話引擎', value: status ? (status.enabled ? '運作中' : '已停用') : '—',
            accent: status && !status.enabled ? 'var(--c-danger-fg)' : undefined },
          { label: '使用者', value: users ? users.total : '—' },
          { label: '全域已啟用工具', value: tools ? enabledTools : '—', sub: tools ? `/ ${tools.length}` : null },
          { label: '錯誤紀錄', value: errors ? errors.total : '—',
            accent: errors && errors.total > 0 ? 'var(--c-danger-fg)' : undefined },
        ]} />

        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gap: 16 }}>
          {nyaCanRead('users') && <Panel title="最近活躍的使用者" actions={<HomeLink label="全部使用者" onClick={() => onNavigate('users')} />}>
            {!users
              ? <EmptyState loading />
              : users.users.length === 0
                ? <EmptyState icon="users" message="還沒有使用者" />
                : users.users.map(u => (
                  <RowButton key={u.user_id} onClick={() => onNavigate('user/' + encodeURIComponent(u.user_id))}>
                    <span className="mono" style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 12 }}>{u.user_id}</span>
                    <span style={{ width: 64, color: 'var(--c-fg2)' }}>{CHANNEL_LABELS[channelOf(u.user_id)] || channelOf(u.user_id)}</span>
                    <span style={{ width: 70, textAlign: 'right', color: 'var(--c-fg2)' }}>{u.message_count} 則</span>
                    <span style={{ width: 90, textAlign: 'right', color: 'var(--c-fg3)' }}>{fmtRelative(u.last_message_at)}</span>
                  </RowButton>
                ))}
          </Panel>}

          {nyaCanRead('audit') && <Panel title="最近的錯誤" actions={<HomeLink label="全部錯誤" onClick={() => onNavigate('audit?result=error')} />}>
            {!errors
              ? <EmptyState loading />
              : errors.logs.length === 0
                ? <EmptyState icon="check" message="沒有錯誤紀錄" />
                : errors.logs.map(l => (
                  <RowButton key={l.log_id} onClick={() => onNavigate(l.user_id === '__sys__' ? 'audit?result=error' + (l.trace_id ? '&trace=' + encodeURIComponent(l.trace_id) : '&keyword=' + encodeURIComponent(l.log_id)) : 'user/' + encodeURIComponent(l.user_id) + '?tab=audit' + (l.trace_id ? '&trace=' + encodeURIComponent(l.trace_id) : ''))}>
                    <span style={{ width: 120, color: 'var(--c-danger-fg)' }}>{ACTION_LABELS[l.action] || l.action}</span>
                    <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: 'var(--c-fg2)' }}>{summarizeDetail(l.detail_json)}</span>
                    <span style={{ width: 90, textAlign: 'right', color: 'var(--c-fg3)' }}>{fmtRelative(l.created_at)}</span>
                  </RowButton>
                ))}
          </Panel>}
        </div>

        <Panel title="常用功能">
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))' }}>
            {HOME_TASKS.filter(t => nyaCanRead(t.target.split('?')[0])).map((t, i) => (
              <button key={t.title} onClick={() => onNavigate(t.target)} style={{
                display: 'flex', alignItems: 'flex-start', gap: 12, padding: 16, textAlign: 'left',
                background: 'var(--c-surface)', border: 'none', cursor: 'pointer',
                borderRight: i % 3 !== 2 ? '1px solid var(--c-border-soft)' : 'none',
                borderTop: i >= 3 ? '1px solid var(--c-border-soft)' : 'none',
              }}
                onMouseEnter={e => { e.currentTarget.style.background = 'var(--c-hover)'; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'var(--c-surface)'; }}>
                <Icon name={t.icon} size={18} color="var(--c-fg2)" style={{ marginTop: 1 }} />
                <span>
                  <span style={{ display: 'block', fontSize: 14, fontWeight: 500, color: 'var(--c-fg)' }}>{t.title}</span>
                  <span style={{ display: 'block', fontSize: 12, color: 'var(--c-fg2)', marginTop: 2 }}>{t.desc}</span>
                </span>
              </button>
            ))}
          </div>
        </Panel>

        {status && (
          <Panel title="目前設定">
            <KVGrid cols={3} items={[
              { label: '對話模型', value: `${status.chat_provider} · ${status.chat_model}`, mono: true },
              { label: status.auto_summarize_enabled ? '摘要路由設定' : '摘要路由設定（目前不執行、不注入）', value: status.summarization_model, mono: true },
              { label: '預設 Soul', value: status.default_soul_id, mono: true },
              { label: '目前聊天室的近期訊息上限', value: `${status.short_term_message_limit} 則（排除遺忘內容；另受 token 預算限制）` },
              { label: '自動摘要', value: status.auto_summarize_enabled ? '已開啟' : '已關閉，現有摘要不會用於聊天' },
              ...(status.auto_summarize_enabled ? [{ label: '觸發摘要門檻', value: `${status.summarize_trigger_count} 則訊息` }] : []),
            ]} />
          </Panel>
        )}
      </ContentArea>
    </div>
  );
}

function RowButton({ onClick, children }) {
  const [hover, setHover] = useState(false);
  return (
    <button onClick={onClick} onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{ ...rowButtonStyle, background: hover ? 'var(--c-hover)' : 'var(--c-surface)' }}>
      {children}
    </button>
  );
}

const rowButtonStyle = {
  display: 'flex', alignItems: 'center', gap: 12, width: '100%',
  padding: '10px 16px', border: 'none', borderBottom: '1px solid var(--c-border-soft)',
  background: 'var(--c-surface)', cursor: 'pointer', textAlign: 'left', fontSize: 13, color: 'var(--c-fg)',
};

function summarizeDetail(json) {
  try {
    const d = JSON.parse(json);
    return d.error || d.message || d.tool || d.content || json;
  } catch { return json || ''; }
}

window.PageHome = PageHome;
