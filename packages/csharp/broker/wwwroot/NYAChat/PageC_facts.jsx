/* Page C — 補充頁籤：C4 事實管理、C5 摘要管理 */
const { useState, useEffect } = React;

/* Category badge colors — strictly within palette. Red is reserved for destructive
   actions only, so categories never use it. */
const CAT_COLORS = {
  identity:    'var(--c-fg)',
  preference:  'var(--c-accent-fg)',
  context:     'var(--c-fg2)',
  instruction: 'var(--c-fg)',
};

/* ── C4：事實管理 ─────────────────────────────────────────── */
function TabC4({ userId, onChanged }) {
  const [facts, setFacts]         = useState([]);
  const [loading, setLoading]     = useState(false);
  const [histModal, setHistModal] = useState(null);
  const [histLoading, setHLoad]   = useState(false);
  const [form, setForm]           = useState({ category: 'identity', key: '', value: '', confidence: '1.0' });
  const [addLoading, setAddLoad]  = useState(false);
  const [deleteLoading, setDLoad] = useState({});

  const CATS = ['identity', 'preference', 'context', 'instruction'];

  async function loadFacts() {
    setLoading(true);
    const res = await apiCall('GET', `users/${userId}/facts`);
    if (res.success) setFacts(res.data.facts);
    setLoading(false);
  }

  useEffect(() => { loadFacts(); }, [userId]);

  async function showHistory(factKey) {
    setHistModal({ factKey, history: null });
    setHLoad(true);
    const res = await apiCall('GET', `users/${userId}/facts/${factKey}/history`);
    setHistModal({ factKey, history: res.success ? res.data.history : [] });
    setHLoad(false);
  }

  async function handleRollback(factKey, targetVersion) {
    const ok = await showConfirm(`回滾事實 ${factKey}？`, `將把 ${factKey} 回滾至版本 ${targetVersion}。目前版本將被退役。`);
    if (!ok) return;
    const res = await apiCall('POST', `users/${userId}/facts/${factKey}/rollback`, { target_version: targetVersion });
    if (res.success) { showToast(`已回滾 ${factKey} 至版本 ${res.data.rolled_to_version}`, 'success'); loadFacts(); setHistModal(null); onChanged && onChanged(); }
  }

  async function handleDeleteFact(factKey) {
    const ok = await showConfirm(`刪除事實 ${factKey}？`, `將永久硬刪除 factKey「${factKey}」的所有版本，不可還原。`);
    if (!ok) return;
    setDLoad(p => ({ ...p, [factKey]: true }));
    const res = await apiCall('DELETE', `users/${userId}/facts/${factKey}`);
    if (res.success) { showToast(`已刪除 ${factKey}（${res.data.deleted_versions} 個版本）`, 'success'); loadFacts(); onChanged && onChanged(); }
    setDLoad(p => ({ ...p, [factKey]: false }));
  }

  async function handleAddFact() {
    if (!form.key.trim() || !form.value.trim()) { showToast('Key 與 Value 為必填', 'info'); return; }
    setAddLoad(true);
    const res = await apiCall('POST', `users/${userId}/facts`, {
      category: form.category, key: form.key.trim(), value: form.value.trim(),
      confidence: parseFloat(form.confidence) || 1.0,
    });
    if (res.success) {
      showToast(`已新增／覆蓋事實 ${res.data.fact_key}（版本 ${res.data.version}）`, 'success');
      setForm({ category: 'identity', key: '', value: '', confidence: '1.0' });
      loadFacts(); onChanged && onChanged();
    }
    setAddLoad(false);
  }

  async function handleDeleteAll() {
    const ok = await showConfirm('硬刪除全部事實？', `將永久刪除此用戶的全部 ${facts.length} 筆事實（含所有版本），不可還原。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `users/${userId}/facts`);
    if (res.success) { showToast(`已刪除全部 ${res.data.deleted} 筆事實`, 'success'); loadFacts(); onChanged && onChanged(); }
  }

  function Field({ label, children, flex }) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 5, flex }}>
        <span style={{ fontSize: 11, fontWeight: 500, color: 'var(--c-fg2)' }}>{label}</span>
        {children}
      </div>
    );
  }

  // 依 category 分組顯示
  const grouped = facts.reduce((acc, f) => {
    (acc[f.category] = acc[f.category] || []).push(f);
    return acc;
  }, {});
  const CAT_LABELS = { identity: '身份', preference: '偏好', context: '當前任務', instruction: '使用者指令' };

  return (
    <ContentArea>
      <Panel title="有效事實" subtitle={`${facts.length} 筆`}
        actions={<Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={loadFacts}>重新整理</Button>}>
        {loading
          ? <EmptyState loading />
          : facts.length === 0
            ? <EmptyState icon="database" message="此用戶尚無有效事實" hint="LLM 會自動從對話中抽取，或可手動新增" />
            : Object.entries(grouped).map(([cat, catFacts]) => (
                <div key={cat} style={{ borderBottom: '1px solid var(--c-border)' }}>
                  <div style={{
                    display: 'flex', alignItems: 'center', gap: 8, padding: '6px 14px',
                    background: 'var(--c-bg)', borderBottom: '1px solid var(--c-border)',
                  }}>
                    <Badge color={CAT_COLORS[cat] || 'var(--c-fg2)'}>{cat}</Badge>
                    <span style={{ fontSize: 12, color: 'var(--c-fg2)' }}>{CAT_LABELS[cat] || cat}</span>
                    <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)', marginLeft: 4 }}>{catFacts.length} 筆</span>
                  </div>
                  {catFacts.map(f => (
                    <div key={f.fact_key} style={{
                      display: 'flex', alignItems: 'flex-start', gap: 12,
                      padding: '10px 14px', borderBottom: '1px solid var(--c-border-soft)',
                      background: 'var(--c-surface)',
                    }}>
                      {/* Key */}
                      <div style={{ width: 160, flexShrink: 0 }}>
                        <span className="mono" style={{ fontSize: 12, fontWeight: 600, color: 'var(--c-fg)', display: 'block', wordBreak: 'break-all' }}>
                          {f.fact_key}
                        </span>
                        <span className="mono" style={{ fontSize: 10, color: 'var(--c-fg3)' }}>v{f.version} · {Number(f.confidence).toFixed(2)}</span>
                      </div>
                      {/* Value */}
                      <div style={{ flex: 1, fontSize: 13, color: 'var(--c-fg)', lineHeight: 1.55, wordBreak: 'break-word', whiteSpace: 'pre-wrap' }}>
                        {f.fact_value}
                      </div>
                      {/* Time */}
                      <div className="mono" style={{ fontSize: 10, color: 'var(--c-fg3)', flexShrink: 0, whiteSpace: 'nowrap' }}>
                        {fmtTimeShort(f.created_at)}
                      </div>
                      {/* Actions */}
                      <div style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
                        <Button variant="ghost" size="sm" icon="history" onClick={() => showHistory(f.fact_key)}>歷史</Button>
                        <Button variant="ghost" size="sm" icon="trash" loading={deleteLoading[f.fact_key]}
                          onClick={() => handleDeleteFact(f.fact_key)} style={{ color: 'var(--c-danger)' }}>刪除</Button>
                      </div>
                    </div>
                  ))}
                </div>
              ))
        }
      </Panel>

      {/* Add form */}
      <Panel title="手動新增 / 覆蓋事實" accent="var(--c-accent)">
        <div style={{ padding: 14, display: 'flex', gap: 10, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <Field label="Category">
            <Select value={form.category} options={CATS} onChange={v => setForm(p => ({ ...p, category: v }))} style={{ width: 130 }} />
          </Field>
          <Field label="Key">
            <Input value={form.key} placeholder="fact_key" mono onChange={v => setForm(p => ({ ...p, key: v }))} style={{ width: 140 }} />
          </Field>
          <Field label="Value" flex={1}>
            <Input value={form.value} placeholder="事實內容" onChange={v => setForm(p => ({ ...p, value: v }))} style={{ width: '100%', minWidth: 160 }} />
          </Field>
          <Field label="Confidence">
            <Input value={form.confidence} type="number" placeholder="1.0" mono onChange={v => setForm(p => ({ ...p, confidence: v }))} style={{ width: 84 }} />
          </Field>
          <Button variant="primary" size="md" icon="plus" loading={addLoading} onClick={handleAddFact}>送出</Button>
        </div>
      </Panel>

      <DangerZone>
        <DangerAction label="硬刪除全部事實"
          description={`永久刪除全部 ${facts.length} 筆事實的所有版本，不可還原`}
          buttonLabel="硬刪除全部" onConfirm={handleDeleteAll} />
      </DangerZone>

      {histModal && (
        <Modal title={`事實歷史版本 · ${histModal.factKey}`} onClose={() => setHistModal(null)} width={640}>
          {histLoading
            ? <EmptyState loading />
            : <Table columns={[
                { label: 'VER', width: 56, center: true },
                { label: 'VALUE' },
                { label: 'CONF', width: 64, center: true },
                { label: 'STATUS', width: 88, center: true },
                { label: '建立時間', width: 130 },
                { label: '操作', width: 90 },
              ]}>
                {(histModal.history || []).length === 0
                  ? <EmptyRow cols={6} message="無版本歷史" />
                  : (histModal.history || []).map(h => (
                      <TR key={h.fact_id} selected={h.is_active}>
                        <TD mono center>v{h.version}</TD>
                        <TD><div style={{ maxHeight: '4.4em', overflowY: 'auto', fontSize: 13, lineHeight: 1.45 }}>{h.fact_value}</div></TD>
                        <TD mono center>{Number(h.confidence).toFixed(2)}</TD>
                        <TD center>{h.is_active ? <StatusBadge ok>active</StatusBadge> : <StatusBadge idle>舊版</StatusBadge>}</TD>
                        <TD mono muted style={{ fontSize: 11 }}>{fmtTimeShort(h.created_at)}</TD>
                        <TD>{!h.is_active && <Button variant="ghost" size="sm" icon="undo" onClick={() => handleRollback(histModal.factKey, h.version)}>回滾</Button>}</TD>
                      </TR>
                    ))
                }
              </Table>
          }
        </Modal>
      )}
    </ContentArea>
  );
}

/* ── C5：摘要管理 ─────────────────────────────────────────── */
function TabC5({ userId, onChanged }) {
  const [summaries, setSummaries]   = useState([]);
  const [loading, setLoading]       = useState(false);
  const [rbLoading, setRBLoad]      = useState({});
  const [trigLoading, setTrigLoad]  = useState(false);
  const [fullText, setFullText]     = useState(null); // { summaryId, version, text }
  const [triggerCount, setTriggerCount] = useState(0);   // Fix 2：自動摘要門檻
  const [topicActive, setTopicActive]   = useState({});  // Fix 2：topic_id → 未摘要(active)訊息數

  async function loadSummaries() {
    setLoading(true);
    const res = await apiCall('GET', `users/${userId}/summaries`);
    if (res.success) {
      setSummaries(res.data.summaries);
      setTriggerCount(res.data.trigger_count || 0);
      setTopicActive(res.data.topic_active || {});
    }
    setLoading(false);
  }

  useEffect(() => { loadSummaries(); }, [userId]);

  async function handleTrigger() {
    setTrigLoad(true);
    const res = await apiCall('POST', `users/${userId}/summaries/trigger`);
    if (res.success) {
      if (res.data.triggered) {
        showToast(`摘要已生成（v${res.data.new_version}，涵蓋至 seq #${res.data.covered_to_seq}）`, 'success');
        loadSummaries();
        onChanged && onChanged();
      } else {
        showToast(res.data.message, 'info');
      }
    }
    setTrigLoad(false);
  }

  async function handleRollback(summaryId, version) {
    const ok = await showConfirm(`回滾至摘要版本 ${version}？`, `目前的 active 版本將被退役，版本 ${version} 成為新的 active 摘要。`);
    if (!ok) return;
    setRBLoad(p => ({ ...p, [summaryId]: true }));
    const res = await apiCall('POST', `users/${userId}/summaries/${summaryId}/rollback`);
    if (res.success) { showToast(`已回滾至摘要版本 ${res.data.rolled_to_version}`, 'success'); loadSummaries(); onChanged && onChanged(); }
    setRBLoad(p => ({ ...p, [summaryId]: false }));
  }

  async function handleDeleteAll() {
    const ok = await showConfirm('硬刪除全部摘要？', `將永久刪除全部 ${summaries.length} 個摘要版本，並重置所有訊息的 is_summarized 旗標。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `users/${userId}/summaries`);
    if (res.success) { showToast(`已刪除全部 ${res.data.deleted} 個摘要`, 'success'); loadSummaries(); onChanged && onChanged(); }
  }

  // 大項#3：摘要依話題隔離 → 依 topic_id 分組，每個話題一個區塊（active 置頂、舊版收於其下）。
  const groups = (() => {
    const map = new Map();
    summaries.forEach(s => {
      const key = s.topic_id || '__global__';
      if (!map.has(key)) map.set(key, { topicId: s.topic_id, topicTitle: s.topic_title, items: [] });
      map.get(key).items.push(s);
    });
    // 每組依版本新→舊；有 active 摘要的話題排前面
    const list = [...map.values()].map(g => ({
      ...g,
      items: g.items.slice().sort((a, b) => b.version - a.version),
      hasActive: g.items.some(s => s.is_active),
    }));
    list.sort((a, b) => (b.hasActive - a.hasActive));
    return list;
  })();

  function SummaryRows({ items }) {
    return items.map(s => (
      <TR key={s.summary_id} selected={s.is_active}>
        <TD mono center>v{s.version}</TD>
        <TD center>{s.is_active ? <StatusBadge ok>active</StatusBadge> : <StatusBadge idle>舊版</StatusBadge>}</TD>
        <TD>
          <div
            onClick={() => setFullText({ summaryId: s.summary_id, version: s.version, text: s.summary_text || s.summary_preview })}
            style={{ maxHeight: '6em', overflowY: 'auto', fontSize: 13, lineHeight: 1.55, cursor: 'pointer', textDecoration: 'underline', textDecorationStyle: 'dotted', textUnderlineOffset: 3 }}
            title="點擊查看完整摘要">
            {s.summary_preview}
          </div>
        </TD>
        <TD mono muted style={{ fontSize: 11 }}>#{s.covered_from_seq}–#{s.covered_to_seq}</TD>
        <TD mono center>{s.message_count}</TD>
        <TD mono muted style={{ fontSize: 11 }}>{fmtTimeShort(s.created_at)}</TD>
        <TD>{!s.is_active && <Button variant="ghost" size="sm" icon="undo" loading={rbLoading[s.summary_id]} onClick={() => handleRollback(s.summary_id, s.version)}>回滾</Button>}</TD>
      </TR>
    ));
  }

  const TOPIC_COLS = [
    { label: 'VER',    width: 56, center: true },
    { label: 'STATUS', width: 88, center: true },
    { label: '摘要預覽' },
    { label: '涵蓋範圍', width: 110 },
    { label: '訊息', width: 64, center: true },
    { label: '建立時間', width: 130 },
    { label: '操作', width: 80 },
  ];

  return (
    <ContentArea>
      {/* 動作列 + 總計（摘要本體改為依話題分組呈現） */}
      <Panel title="摘要" subtitle={`${groups.length} 個話題 · ${summaries.length} 個版本`}
        actions={
          <div style={{ display: 'flex', gap: 6 }}>
            <Button variant="primary" size="sm" icon="archive" loading={trigLoading} onClick={handleTrigger}>立即摘要</Button>
            <Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={loadSummaries}>重新整理</Button>
          </div>
        }>
        <div style={{ padding: '8px 14px', fontSize: 12, color: 'var(--c-fg2)' }}>
          摘要依話題隔離；「立即摘要」對目前 active 話題壓縮最舊訊息。
        </div>
      </Panel>

      {loading && summaries.length === 0
        ? <Panel><EmptyState loading /></Panel>
        : summaries.length === 0
          ? <Panel><EmptyState icon="archive" message="此用戶尚無摘要" hint="累積足夠對話後自動產生，或點「立即摘要」" /></Panel>
          : groups.map(g => {
              const title = g.topicTitle || (g.topicId
                ? g.topicId
                : '（全域 / 舊版）');
              // Fix 2：觸發透明度 —— 未摘要(active)則數 + 距下次自動摘要還差幾則。
              const active = g.topicId ? (topicActive[g.topicId] ?? 0) : 0;
              const remain = Math.max(0, triggerCount - active);
              const trigInfo = g.topicId && triggerCount > 0
                ? ` · 未摘要 ${active} 則 · 距下次自動摘要還差 ${remain} 則`
                : '';
              return (
                <Panel key={g.topicId || '__global__'}
                  title={title}
                  subtitle={`${g.items.length} 版本${g.hasActive ? ' · 有 active' : ''}${trigInfo}`}>
                  <Table columns={TOPIC_COLS}>
                    <SummaryRows items={g.items} />
                  </Table>
                </Panel>
              );
            })
      }

      <DangerZone>
        <DangerAction label="硬刪除全部摘要"
          description="永久刪除所有摘要版本，並重置 is_summarized 旗標"
          buttonLabel="硬刪除全部" onConfirm={handleDeleteAll} />
      </DangerZone>

      {fullText && (
        <Modal title={`摘要全文 · v${fullText.version}`} onClose={() => setFullText(null)} width={640}>
          <div style={{ padding: '14px 16px', whiteSpace: 'pre-wrap', fontSize: 13, lineHeight: 1.8, color: 'var(--c-fg)', maxHeight: 480, overflowY: 'auto' }}>
            {fullText.text}
          </div>
        </Modal>
      )}
    </ContentArea>
  );
}

window.TabC4 = TabC4;
window.TabC5 = TabC5;
