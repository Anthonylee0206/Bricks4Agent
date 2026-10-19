const { useState, useEffect } = React;

const _soulInp = {
  border: '1px solid var(--c-border)', borderRadius: 0, padding: '6px 10px',
  fontSize: 13, outline: 'none', fontFamily: 'inherit',
  boxSizing: 'border-box', background: 'var(--c-surface)', width: '100%',
};
const _soulLbl = { fontSize: 12, color: 'var(--c-fg2)', marginBottom: 4, display: 'block', fontWeight: 500 };

function StringList({ field, label, addLabel, items, onAdd, onSet, onRemove }) {
  return (
    <Panel title={label} actions={
      <Button write="system" variant="ghost" size="sm" icon="plus" onClick={() => onAdd(field)}>{addLabel}</Button>
    }>
      <div style={{ padding: '10px 14px', display: 'flex', flexDirection: 'column', gap: 7 }}>
        {items.length === 0 && <EmptyState icon="list" message="尚無項目" />}
        {items.map((v, i) => (
          <div key={i} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <span style={{ fontSize: 12, color: 'var(--c-fg3)', width: 22, textAlign: 'right', flexShrink: 0 }}>{i + 1}.</span>
            <input style={{ ..._soulInp, flex: 1 }} value={v}
              onChange={e => onSet(field, i, e.target.value)} />
            <Button write="system" variant="ghost" size="sm" icon="trash"
              style={{ color: 'var(--c-danger)' }} onClick={() => onRemove(field, i)} />
          </div>
        ))}
      </div>
    </Panel>
  );
}


function SoulEditor({ soul, isNew, onSave, onCancel, saving }) {
  const [form, setForm] = useState(() => soul
    ? JSON.parse(JSON.stringify(soul))
    : {
        soul_id: '', display_name: '',
        personality: { tone: '', language: '繁體中文', greeting_style: '', humor_level: '', formality: '' },
        rules: [], forbidden: [],
      }
  );

  function setPersonality(key, val) {
    setForm(f => ({ ...f, personality: { ...f.personality, [key]: val } }));
  }
  function addItem(field)             { setForm(f => ({ ...f, [field]: [...(f[field] || []), ''] })); }
  function setItem(field, idx, val)   { setForm(f => { const a = [...(f[field] || [])]; a[idx] = val; return { ...f, [field]: a }; }); }
  function removeItem(field, idx)     { setForm(f => { const a = [...(f[field] || [])]; a.splice(idx, 1); return { ...f, [field]: a }; }); }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title={isNew ? '新增 Soul' : `編輯 Soul — ${form.soul_id}`}>
        <Button variant="ghost" size="sm" icon="arrowLeft" onClick={onCancel}>返回</Button>
        <Button write="system" variant="primary" size="sm" icon="check" loading={saving}
          onClick={() => onSave(form)}>儲存</Button>
      </PageHeader>

      <ContentArea>
        <Panel title="基本資訊">
          <div style={{ padding: '12px 14px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 16px' }}>
            <div>
              <label style={_soulLbl}>Soul ID{isNew && <span style={{ color: 'var(--c-danger)' }}> *（建立後不可修改）</span>}</label>
              <input style={{ ..._soulInp, background: isNew ? 'var(--c-surface)' : 'var(--c-bg)', color: isNew ? 'var(--c-fg)' : 'var(--c-fg2)' }}
                value={form.soul_id} readOnly={!isNew}
                onChange={e => setForm(f => ({ ...f, soul_id: e.target.value }))}
                placeholder="例：nya_maid（英數字、_ 或 -）" />
            </div>
            <div>
              <label style={_soulLbl}>顯示名稱 *</label>
              <input style={_soulInp} value={form.display_name}
                onChange={e => setForm(f => ({ ...f, display_name: e.target.value }))}
                placeholder="例：喵機器NYA" />
            </div>
          </div>
        </Panel>

        <Panel title="個性設定 (personality)">
          <div style={{ padding: '12px 14px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 16px' }}>
            {[
              { key: 'tone',           label: '語氣 (tone)',                    ph: '例：溫暖、友善' },
              { key: 'language',       label: '語言 (language)',                ph: '例：繁體中文' },
              { key: 'greeting_style', label: '打招呼風格 (greeting_style)',     ph: '' },
              { key: 'humor_level',    label: '幽默程度 (humor_level)',          ph: '例：適度幽默' },
              { key: 'formality',      label: '正式程度 (formality)',            ph: '例：半正式' },
            ].map(({ key, label, ph }) => (
              <div key={key}>
                <label style={_soulLbl}>{label}</label>
                <input style={_soulInp} value={form.personality[key] || ''} placeholder={ph}
                  onChange={e => setPersonality(key, e.target.value)} />
              </div>
            ))}
          </div>
        </Panel>

        <StringList field="rules"     label="規則 (rules)"         addLabel="新增規則"
          items={form.rules || []}     onAdd={addItem} onSet={setItem} onRemove={removeItem} />
        <StringList field="forbidden" label="禁止事項 (forbidden)"  addLabel="新增禁止"
          items={form.forbidden || []} onAdd={addItem} onSet={setItem} onRemove={removeItem} />
      </ContentArea>
    </div>
  );
}


function PageE() {
  const [status,  setStatus]  = useState(null);
  const [souls,   setSouls]   = useState([]);
  const [loading, setLoading] = useState(false);
  const [editing, setEditing] = useState(null);
  const [saving,  setSaving]  = useState(false);
  const [loadFailures, setLoadFailures] = useState([]);

  async function loadAll() {
    setLoading(true);
    const [st, sl] = await Promise.all([apiCall('GET', 'status'), apiCall('GET', 'souls')]);
    if (st.success) setStatus(st.data);
    if (sl.success) setSouls(sl.data.souls);
    setLoading(false);
  }

  async function openEdit(soulId) {
    const res = await apiCall('GET', `souls/${encodeURIComponent(soulId)}`);
    if (res.success) setEditing({ soul: res.data, isNew: false });
  }

  async function handleSave(form) {
    if (!form.soul_id.trim())      { showToast('Soul ID 不可為空', 'error'); return; }
    if (!form.display_name.trim()) { showToast('顯示名稱不可為空', 'error'); return; }

    setSaving(true);
    const method = editing.isNew ? 'POST' : 'PUT';
    const path   = editing.isNew ? 'souls' : `souls/${encodeURIComponent(form.soul_id)}`;
    const res = await apiCall(method, path, form);
    setSaving(false);
    if (res.success) {
      showToast(`Soul "${form.display_name}" 已儲存`, 'success');
      setEditing(null);
      loadAll();
    }
  }

  async function handleDelete(soulId) {
    const preview = await apiCall('GET', `souls/${encodeURIComponent(soulId)}`);
    if (!preview.success) return;
    const affected = preview.data.bound_users || [];
    const ok = await showConfirm('刪除 Soul',
      `確定要刪除 Soul "${soulId}" 嗎？此操作無法復原。已綁定此 Soul 的使用者將回退到預設 Soul；綁定仍保留，同 ID Soul 重建後會再次生效。受影響的綁定使用者（${affected.length}）：${affected.join('、') || '無'}。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `souls/${encodeURIComponent(soulId)}`);
    if (res.success) {
      showToast(`Soul "${soulId}" 已刪除`, 'success');
      loadAll();
    }
  }

  async function handleReload() {
    const res = await apiCall('POST', 'souls/reload');
    if (res.success) {
      setSouls(res.data.souls);
      setLoadFailures(res.data.failures || []);
      showToast(res.data.ok ? `已重新載入 ${res.data.loaded} 個 Soul` : `已載入 ${res.data.loaded} 個 Soul，${res.data.failures.length} 個檔案載入失敗`, res.data.ok ? 'success' : 'error');
    }
  }

  useEffect(() => { loadAll(); }, []);

  if (editing) {
    return <SoulEditor
      soul={editing.soul} isNew={editing.isNew}
      onSave={handleSave}
      onCancel={() => setEditing(null)}
      saving={saving}
    />;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title="Soul 人格" count={souls.length} desc="NYA 的語氣、規則與禁止事項；可指定給個別使用者">
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={loadAll}>重新整理</Button>
        <Button write="system" variant="secondary" size="sm" onClick={handleReload} title="重新讀取 Soul 設定檔">從檔案重新載入</Button>
        <Button write="system" variant="primary" size="sm" icon="plus"
          onClick={() => setEditing({ soul: null, isNew: true })}>新增 Soul</Button>
      </PageHeader>

      <ContentArea>
        {loadFailures.length > 0 && <Panel title="Soul 載入失敗">
          {loadFailures.map((f, i) => <div key={i} style={{ padding: 14, color: 'var(--c-danger-fg)' }}>{f.file}：{f.message}</div>)}
        </Panel>}
        <Panel title="Soul 清單" subtitle={`${souls.length} 個`}>
          {loading && souls.length === 0
            ? <EmptyState loading />
            : souls.length === 0
              ? <EmptyState icon="soul" message="無已載入的 Soul" />
              : <Table columns={[
                  { label: 'ID',          width: 180 },
                  { label: '顯示名稱' },
                  { label: 'DEFAULT',     width: 100, center: true },
                  { label: '',            width: 160, center: true },
                ]}>
                  {souls.map(s => (
                    <TR key={s.soul_id}>
                      <TD mono style={{ fontWeight: 500 }}>{s.soul_id}</TD>
                      <TD>{s.display_name}</TD>
                      <TD center>
                        {status && s.soul_id === status.default_soul_id
                          ? <StatusBadge ok>default</StatusBadge>
                          : <span style={{ color: 'var(--c-fg4)' }}>—</span>}
                      </TD>
                      <TD center>
                        <div style={{ display: 'flex', gap: 6, justifyContent: 'center' }}>
                          <Button write="system" variant="ghost" size="sm" icon="edit" onClick={() => openEdit(s.soul_id)}>編輯</Button>
                          {(!status || s.soul_id !== status.default_soul_id) && (
                            <Button write="system" variant="ghost" size="sm" icon="trash"
                              style={{ color: 'var(--c-danger)' }} onClick={() => handleDelete(s.soul_id)} />
                          )}
                        </div>
                      </TD>
                    </TR>
                  ))}
                </Table>
          }
        </Panel>
      </ContentArea>
    </div>
  );
}

window.PageE = PageE;
