function PageI() {
  const [data, setData] = React.useState(null);
  const [drafts, setDrafts] = React.useState({});
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState('');

  async function load() {
    const res = await apiCall('GET', 'config/effective');
    if (res.success) {
      setData(res.data);
      setDrafts(Object.fromEntries(res.data.settings.map(s => [s.key, s.value])));
    } else setError(res.message);
  }

  React.useEffect(() => { load(); }, []);

  async function apply(setting, raw) {
    if (busy || raw === setting.value || String(raw) === String(setting.value)) return;
    const value = setting.type === 'bool' ? raw : setting.type === 'string' ? raw.trim() : Number(raw);
    if (setting.type !== 'bool' && setting.type !== 'string' &&
      (String(raw).trim() === '' || !Number.isFinite(value) ||
       (setting.type === 'integer' && !Number.isInteger(value)) || value < setting.min || value > setting.max)) {
      setError(`${setting.label} 必須在 ${setting.min}–${setting.max} 範圍內${setting.type === 'integer' ? '，且為整數' : ''}。`);
      return;
    }
    setBusy(true);
    setError('');
    const res = await apiCall('PATCH', 'config', { [setting.key]: value });
    if (res.success) {
      const effective = await apiCall('GET', 'config/effective');
      if (effective.success) {
        setData(effective.data);
        setDrafts(d => ({ ...d, [setting.key]: value }));
        showToast(`${setting.label} 已儲存並立即套用`, 'success');
      } else setError(effective.message);
    } else {
      setDrafts(d => ({ ...d, [setting.key]: setting.value }));
      setError(res.message);
    }
    setBusy(false);
  }

  async function reset() {
    if (!await showConfirm('還原對話設定', '刪除所有後台對話設定覆寫，立即回到 appsettings 預設值。LLM Profile 與路由仍由 LLM 模型頁管理。')) return;
    setBusy(true);
    setError('');
    const res = await apiCall('POST', 'config/reset', {});
    if (res.success) {
      setData(res.data);
      setDrafts(Object.fromEntries(res.data.settings.map(s => [s.key, s.value])));
      showToast('已還原 appsettings 預設', 'success');
    } else setError(res.message);
    setBusy(false);
  }

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PageHeader title="對話設定" desc="修改數值後離開欄位，或切換開關，即儲存並套用；後續請求使用新值。">
        <Button variant="secondary" size="sm" disabled={busy} onClick={load}>重新整理</Button>
        <Button variant="secondary" size="sm" disabled={busy || !data} onClick={reset}>還原 appsettings 預設</Button>
      </PageHeader>
      <ContentArea>
        <Panel title="上下文與生效方式">
          <div style={{ padding: 14, color: 'var(--c-fg2)', lineHeight: 1.7 }}>
            LM Studio 載入模型時的上下文長度是獨立設定，必須大於或等於 LLM Profile 的「最大上下文 token」。
            NyaChat 的預算控制帶入多少訊息、事實及摘要，並保留輸出與安全餘額。
            Profile 已指定上下文時，優先使用 Profile；此頁的預設上下文僅作回退。
            模型、API 金鑰及路由請至 <a href="#llm">LLM 模型</a> 設定；舊版模型欄位不提供編輯。
          </div>
        </Panel>
        {error && <div role="alert" style={{ color: 'var(--c-danger)' }}>{error}</div>}
        {!data ? <EmptyState loading /> : <>
          <Panel title="對話與記憶限制" subtitle={busy ? '儲存中…' : '修改後立即生效'}>
            <div style={{ overflowX: 'auto' }}>
              <Table columns={[{ label: '設定' }, { label: '修改值', width: 180 }, { label: '目前生效值', width: 130 }, { label: '來源', width: 150 }, { label: '套用', width: 100 }]}>
                {data.settings.map(s => <TR key={s.key}>
                  <TD>
                    <div>{s.label}</div>
                    <div className="mono" style={{ color: 'var(--c-fg3)', fontSize: 11 }}>{s.key}</div>
                    <div style={{ color: 'var(--c-fg2)', fontSize: 12 }}>{s.description}</div>
                  </TD>
                  <TD>
                    {s.type === 'bool' ? <input type="checkbox" aria-label={s.label} checked={!!drafts[s.key]} disabled={busy}
                      onChange={e => { setDrafts(d => ({ ...d, [s.key]: e.target.checked })); apply(s, e.target.checked); }} />
                      : <input aria-label={s.label} type={s.type === 'string' ? 'text' : 'number'} value={drafts[s.key] ?? ''}
                          min={s.min ?? undefined} max={s.max ?? undefined} step={s.type === 'integer' ? 1 : 'any'} disabled={busy}
                          onChange={e => setDrafts(d => ({ ...d, [s.key]: e.target.value }))}
                          onBlur={e => apply(s, e.target.value)} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }}
                          style={{ width: 160, padding: 6, background: 'var(--c-surface)', color: 'var(--c-fg)', border: '1px solid var(--c-border)' }} />}
                  </TD>
                  <TD mono>{typeof s.value === 'boolean' ? (s.value ? '開啟' : '關閉') : String(s.value)}</TD>
                  <TD>{s.source}</TD>
                  <TD>{s.applies_immediately ? '立即' : '需重啟'}</TD>
                </TR>)}
              </Table>
            </div>
          </Panel>
          <Panel title="啟動設定（唯讀）">
            <div style={{ padding: 14, display: 'grid', gap: 12 }}>
              {data.read_only.map(s => <div key={s.key}>
                <div className="mono">{s.key}：{String(s.value)}</div>
                <div style={{ color: 'var(--c-fg2)', fontSize: 12 }}>{s.explanation}</div>
              </div>)}
            </div>
          </Panel>
        </>}
      </ContentArea>
    </div>
  );
}

window.PageI = PageI;
