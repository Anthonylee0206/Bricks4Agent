/* Page F — 工具管理 */
const { useState, useEffect } = React;

const TOOL_GROUP_LABELS = { topic: '話題', memory: '記憶', fact: '事實', search: '搜尋', transport: '交通' };

const TOOL_PERMISSION_LABELS = {
  query: '需要「允許查詢」',
  transport: '需要「允許交通查詢」',
};

function permissionLabel(perm) {
  if (!perm) return null;
  return TOOL_PERMISSION_LABELS[perm] || perm;
}

function Switch({ on, busy, onClick, label }) {
  return (
    <button role="switch" aria-checked={on} aria-label={label} onClick={onClick} disabled={busy}
      style={{
        width: 34, height: 20, borderRadius: 10, border: 'none', padding: 0,
        background: on ? 'var(--c-accent)' : 'var(--c-border)', position: 'relative',
        cursor: busy ? 'wait' : 'pointer', flexShrink: 0, opacity: busy ? 0.6 : 1,
      }}>
      <span style={{ position: 'absolute', top: 3, left: on ? 17 : 3, width: 14, height: 14, borderRadius: '50%', background: '#fff' }} />
    </button>
  );
}

function PageF() {
  const [tools, setTools]       = useState([]);
  const [loading, setLoading]   = useState(false);
  const [toggling, setToggling] = useState({});

  async function loadTools() {
    setLoading(true);
    const res = await apiCall('GET', 'tools');
    if (res.success) setTools(res.data.tools);
    setLoading(false);
  }

  async function handleToggle(tool) {
    const newEnabled = !tool.enabled;
    setToggling(p => ({ ...p, [tool.id]: true }));
    const res = await apiCall('POST', `tools/${tool.id}/toggle`, { enabled: newEnabled });
    if (res.success) {
      setTools(prev => prev.map(t => t.id === tool.id ? { ...t, enabled: newEnabled } : t));
      showToast(`${tool.name} 已${newEnabled ? '啟用' : '停用'}`, 'success');
    }
    setToggling(p => ({ ...p, [tool.id]: false }));
  }

  useEffect(() => { loadTools(); }, []);

  const enabledCount = tools.filter(t => t.enabled).length;
  const groups = {};
  tools.forEach(t => { const g = t.group || 'other'; (groups[g] = groups[g] || []).push(t); });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title="工具" count={tools.length ? `${enabledCount} / ${tools.length} 已啟用` : undefined}
        desc="停用的工具 LLM 仍看得到，但呼叫時會回覆「功能已關閉」；需要權限的工具只對有權限的使用者開放">
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={loadTools}>重新整理</Button>
      </PageHeader>

      <ContentArea>
        {loading && tools.length === 0
          ? <Panel><EmptyState loading /></Panel>
          : tools.length === 0
            ? <Panel><EmptyState icon="zap" message="沒有可用的工具" /></Panel>
            : Object.keys(groups).map(g => (
              <Panel key={g} title={TOOL_GROUP_LABELS[g] || g} subtitle={`${groups[g].filter(t => t.enabled).length} / ${groups[g].length} 已啟用`}>
                {groups[g].map((tool, i) => (
                  <div key={tool.id} style={{
                    display: 'flex', alignItems: 'center', gap: 14, padding: '12px 16px',
                    borderTop: i > 0 ? '1px solid var(--c-border-soft)' : 'none',
                  }}>
                    <Switch on={tool.enabled} busy={toggling[tool.id]} label={`${tool.name} ${tool.enabled ? '停用' : '啟用'}`}
                      onClick={() => !toggling[tool.id] && handleToggle(tool)} />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <span className="mono" style={{ fontSize: 13, fontWeight: 600, color: tool.enabled ? 'var(--c-fg)' : 'var(--c-fg2)' }}>{tool.name}</span>
                        {permissionLabel(tool.required_permission) && <Badge>{permissionLabel(tool.required_permission)}</Badge>}
                      </div>
                      <div style={{ fontSize: 13, color: 'var(--c-fg2)', marginTop: 2 }}>{tool.description}</div>
                    </div>
                    <span style={{ fontSize: 12, color: tool.enabled ? 'var(--c-accent-fg)' : 'var(--c-fg3)', width: 48, textAlign: 'right' }}>
                      {tool.enabled ? '已啟用' : '已停用'}
                    </span>
                  </div>
                ))}
              </Panel>
            ))}
      </ContentArea>
    </div>
  );
}

window.PageF = PageF;
