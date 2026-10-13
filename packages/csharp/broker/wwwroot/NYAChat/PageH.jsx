/* Page H — 身分組（角色權限管理 + 使用者綁定分頁）items 1–2 */
const { useState, useEffect, useCallback } = React;

// ── 角色編輯器 ──────────────────────────────────────────────────────────────
function RoleEditor({ roleId, capabilities, onSave, onCancel, saving }) {
  const isNew = !roleId;
  const [form, setForm] = useState({ role_id: '', display_name: '', allowed_task_types: [], capability_ids: [] });
  const [allCaps, setAllCaps] = useState(false);
  const [builtIn, setBuiltIn] = useState(false);
  const [loading, setLoading] = useState(!isNew);

  useEffect(() => {
    if (isNew) return;
    apiCall('GET', `roles/${roleId}`).then(res => {
      if (res.success) {
        const d = res.data;
        setForm({ role_id: d.role_id, display_name: d.display_name, allowed_task_types: d.allowed_task_types || [], capability_ids: d.capability_ids || [] });
        setAllCaps(!!d.all_capabilities);
        setBuiltIn(!!d.built_in);
      }
      setLoading(false);
    }).catch(() => setLoading(false));
  }, [roleId]);

  function toggleCap(id) {
    setForm(f => ({ ...f, capability_ids: f.capability_ids.includes(id)
      ? f.capability_ids.filter(c => c !== id) : [...f.capability_ids, id] }));
  }

  if (loading) return <EmptyState loading />;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title={isNew ? '新增身分組' : `編輯身分組 — ${form.role_id}`}>
        <Button variant="ghost" size="sm" icon="arrowLeft" onClick={onCancel}>返回</Button>
        <Button variant="primary" size="sm" icon="check" loading={saving}
          onClick={() => onSave({ ...form }, isNew)}>儲存</Button>
      </PageHeader>
      <ContentArea>
        {builtIn && <div style={{ fontSize: 12, color: 'var(--c-warn)', padding: '0 2px 4px' }}>
          <Icon name="alertTriangle" size={13} style={{ display: 'inline-block', verticalAlign: '-2px', marginRight: 4 }} />這是內建角色，編輯能力會影響整個 broker 治理；內建角色不可刪除。</div>}
        <Panel title="基本">
          <div style={{ padding: '12px 14px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 16px' }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--c-fg2)', marginBottom: 4 }}>Role ID{isNew && ' *'}</div>
              <Input value={form.role_id} disabled={!isNew} mono
                onChange={v => setForm(f => ({ ...f, role_id: v }))} placeholder="role_custom（英數 _ -）" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--c-fg2)', marginBottom: 4 }}>顯示名稱</div>
              <Input value={form.display_name} onChange={v => setForm(f => ({ ...f, display_name: v }))} />
            </div>
          </div>
        </Panel>
        <Panel title="能力" subtitle={allCaps ? '此角色擁有全部能力（*）' : `${form.capability_ids.length} 項`}>
          {allCaps
            ? <div style={{ padding: '12px 14px', fontSize: 13, color: 'var(--c-fg2)' }}>
                此角色為全開（<code>*</code>）。如要改為明確清單，請取消下方全開並逐項勾選。
                <div style={{ marginTop: 8 }}>
                  <Button variant="secondary" size="sm" onClick={() => { setForm(f => ({ ...f, capability_ids: f.capability_ids.filter(c => c !== '*') })); setAllCaps(false); }}>改為明確清單</Button>
                </div>
              </div>
            : <div style={{ padding: '8px 14px', display: 'flex', flexDirection: 'column', gap: 6, maxHeight: 360, overflow: 'auto' }}>
                {capabilities.map(c => (
                  <Checkbox key={c.capability_id} checked={form.capability_ids.includes(c.capability_id)}
                    onChange={() => toggleCap(c.capability_id)}
                    label={`${c.capability_id}  ·  ${c.route}`} />
                ))}
                {capabilities.length === 0 && <EmptyState icon="info" message="無能力目錄" />}
              </div>}
        </Panel>
      </ContentArea>
    </div>
  );
}

// ── 角色清單（側欄主頁）─────────────────────────────────────────────────────
function PageH() {
  const [roles, setRoles] = useState([]);
  const [caps, setCaps] = useState([]);
  const [loading, setLoading] = useState(false);
  const [view, setView] = useState('list'); // list | edit
  const [editId, setEditId] = useState(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    const [r, c] = await Promise.all([apiCall('GET', 'roles'), apiCall('GET', 'capabilities')]);
    if (r.success) setRoles(r.data.roles || []);
    if (c.success) setCaps(c.data.capabilities || []);
    setLoading(false);
  }, []);
  useEffect(() => { load(); }, [load]);

  async function handleSave(form, isNew) {
    if (!form.role_id.trim()) { showToast('Role ID 不可為空', 'error'); return; }
    setSaving(true);
    const payload = { role_id: form.role_id, display_name: form.display_name,
      allowed_task_types: form.allowed_task_types, capability_ids: form.capability_ids };
    const res = await apiCall(isNew ? 'POST' : 'PUT', isNew ? 'roles' : `roles/${form.role_id}`, payload);
    setSaving(false);
    if (res.success) { showToast(`身分組「${form.display_name || form.role_id}」已儲存`, 'success'); setView('list'); setEditId(null); load(); }
  }

  async function handleDelete(r) {
    const ok = await showConfirm('刪除身分組', `確定刪除「${r.display_name || r.role_id}」？`);
    if (!ok) return;
    const res = await apiCall('DELETE', `roles/${r.role_id}`);
    if (res.success) { showToast('已刪除', 'success'); load(); }
  }

  if (view === 'edit') {
    return <RoleEditor roleId={editId} capabilities={caps}
      onSave={handleSave} onCancel={() => { setView('list'); setEditId(null); }} saving={saving} />;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title="身分組" count={roles.length}>
        <Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={load}>重新整理</Button>
        <Button variant="primary" size="sm" icon="plus" onClick={() => { setEditId(null); setView('edit'); }}>新增身分組</Button>
      </PageHeader>
      <ContentArea>
        <Panel title="角色清單" subtitle={`${roles.length} 個`}>
          {loading && roles.length === 0
            ? <EmptyState loading />
            : roles.length === 0
              ? <EmptyState icon="shield" message="尚無身分組" />
              : <Table columns={[
                  { label: '顯示名稱' }, { label: 'ROLE ID', width: 160 },
                  { label: '能力', width: 90, center: true }, { label: '綁定數', width: 80, center: true },
                  { label: '', width: 150, center: true },
                ]}>
                  {roles.map(r => (
                    <TR key={r.role_id}>
                      <TD>{r.display_name}{r.built_in && <Badge color="var(--c-fg2)" style={{ marginLeft: 6 }}>內建</Badge>}</TD>
                      <TD mono>{r.role_id}</TD>
                      <TD center>{r.all_capabilities ? <StatusBadge ok>全部</StatusBadge> : r.capability_count}</TD>
                      <TD center>{r.active_binding_count}</TD>
                      <TD center>
                        <div style={{ display: 'flex', gap: 6, justifyContent: 'center' }}>
                          <Button variant="ghost" size="sm" icon="edit" onClick={() => { setEditId(r.role_id); setView('edit'); }}>編輯</Button>
                          {!r.built_in && <Button variant="ghost" size="sm" icon="trash" style={{ color: 'var(--c-danger)' }} onClick={() => handleDelete(r)} />}
                        </div>
                      </TD>
                    </TR>
                  ))}
                </Table>}
        </Panel>
      </ContentArea>
    </div>
  );
}

// ── 使用者詳情：身分組分頁 ───────────────────────────────────────────────────
function NyaUserRolesTab({ userId }) {
  const [data, setData] = useState({ principal_id: null, roles: [] });
  const [allRoles, setAllRoles] = useState([]);
  const [pick, setPick] = useState('');
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    const [u, r] = await Promise.all([apiCall('GET', `users/${userId}/roles`), apiCall('GET', 'roles')]);
    if (u.success) setData(u.data);
    if (r.success) setAllRoles(r.data.roles || []);
    setLoading(false);
  }, [userId]);
  useEffect(() => { load(); }, [load]);

  async function assign() {
    if (!pick) return;
    setBusy(true);
    const res = await apiCall('POST', `users/${userId}/roles`, { role_id: pick });
    setBusy(false);
    if (res.success) { showToast('已指派身分組', 'success'); setPick(''); load(); }
  }
  async function revoke(roleId) {
    const ok = await showConfirm('撤銷身分組', `撤銷使用者的「${roleId}」？`);
    if (!ok) return;
    const res = await apiCall('DELETE', `users/${userId}/roles/${roleId}`);
    if (res.success) { showToast('已撤銷', 'success'); load(); }
  }

  const options = [{ value: '', label: '選擇身分組…' },
    ...allRoles.map(r => ({ value: r.role_id, label: `${r.display_name}（${r.role_id}）` }))];

  if (loading) return <EmptyState loading />;
  return (
    <ContentArea>
      <Panel title="指派身分組">
        <div style={{ padding: '12px 14px', display: 'flex', gap: 8, alignItems: 'center' }}>
          <Select value={pick} options={options} onChange={setPick} style={{ minWidth: 280 }} />
          <Button variant="primary" size="sm" icon="plus" loading={busy} onClick={assign}>指派</Button>
          <div style={{ flex: 1 }} />
          <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)' }}>
            principal: {data.principal_id || '（尚未綁定）'}
          </span>
        </div>
      </Panel>
      <Panel title="已綁定身分組" subtitle={`${data.roles.length} 個`}>
        {data.roles.length === 0
          ? <EmptyState icon="shield" message="尚未綁定任何身分組" />
          : <Table columns={[{ label: '顯示名稱' }, { label: 'ROLE ID', width: 160 },
              { label: '能力', width: 80, center: true }, { label: '', width: 90, center: true }]}>
              {data.roles.map(r => (
                <TR key={r.binding_id}>
                  <TD>{r.display_name}</TD>
                  <TD mono>{r.role_id}</TD>
                  <TD center>{r.capability_count}</TD>
                  <TD center><Button variant="ghost" size="sm" icon="trash" style={{ color: 'var(--c-danger)' }} onClick={() => revoke(r.role_id)} /></TD>
                </TR>
              ))}
            </Table>}
      </Panel>
    </ContentArea>
  );
}

window.PageH = PageH;
window.NyaUserRolesTab = NyaUserRolesTab;
