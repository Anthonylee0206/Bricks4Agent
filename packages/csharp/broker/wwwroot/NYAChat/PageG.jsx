const { useState, useEffect } = React;

function parseParamsJson(raw) {
  try { return JSON.parse(raw || '{}'); } catch { return {}; }
}

const PARAM_LABELS = {
  temperature: '溫度 temperature',
  top_p: 'Top-p',
  max_context_tokens: '最大上下文 token',
  timeout_seconds: '逾時秒數',
  enable_thinking: '開啟思考',
  thinking_budget: '思考 token 預算',
  frequency_penalty: '頻率懲罰',
  presence_penalty: '存在懲罰',
  repeat_penalty: '重複懲罰',
};

function ProfileEditor({ profileId, templates, catalog, onSave, onCancel, saving }) {
  const isNew = !profileId;

  const [origProfile, setOrigProfile] = useState(null);
  const [loadingProfile, setLoadingProfile] = useState(!isNew);

  const [form, setForm] = useState({
    profile_id: '',
    provider: '',
    base_url: '',
    api_key: '',
    model: '',
    template: '',
  });
  const [paramsJson, setParamsJson] = useState('{}');
  const [extraBodyJson, setExtraBodyJson] = useState('{}');
  const [jsonError, setJsonError] = useState(null);
  const [selectedTemplate, setSelectedTemplate] = useState('');

  const commonParams = catalog.filter(p => p.common);

  useEffect(() => {
    if (isNew) return;
    setLoadingProfile(true);
    apiCall('GET', 'llm/profiles').then(res => {
      if (res.success) {
        const p = (res.data || []).find(x => x.profile_id === profileId);
        if (p) {
          setOrigProfile(p);
          setForm({
            profile_id: p.profile_id,
            provider:   p.provider,
            base_url:   p.base_url,
            api_key:    p.api_key || '',
            model:      p.model,
            template:   p.template || '',
          });
          setParamsJson(p.params_json || '{}');
          setExtraBodyJson(JSON.stringify(parseParamsJson(p.params_json).extra_body || {}, null, 2));
        }
      }
      setLoadingProfile(false);
    });
  }, [profileId]);

  function applyTemplate(tplId) {
    const tpl = templates.find(t => t.id === tplId);
    if (!tpl) return;
    setSelectedTemplate(tplId);
    setForm(f => ({
      ...f,
      provider: tpl.provider,
      base_url: tpl.default_base_url,
      template: tplId,
      model: tpl.default_model || f.model,
    }));
    const merged = { ...(tpl.default_params || {}) };
    if (tpl.extra_body) merged.extra_body = tpl.extra_body;
    setExtraBodyJson(JSON.stringify(tpl.extra_body || {}, null, 2));
    setParamsJson(JSON.stringify(merged, null, 2));
    setJsonError(null);
  }

  function setCommonParam(key, value) {
    const obj = parseParamsJson(paramsJson);
    if (value === '' || value === null || value === undefined) {
      delete obj[key];
    } else {
      obj[key] = value;
    }
    setParamsJson(JSON.stringify(obj, null, 2));
    setJsonError(null);
  }

  function handleJsonBlur(raw) {
    try {
      const params = JSON.parse(raw);
      setExtraBodyJson(JSON.stringify(params.extra_body || {}, null, 2));
      setJsonError(null);
    } catch (e) {
      setJsonError('JSON 格式錯誤：' + e.message);
    }
  }

  function handleSubmit() {
    let parsed;
    try { parsed = JSON.parse(paramsJson); }
    catch (e) { setJsonError('JSON 格式錯誤：' + e.message); return; }

    try {
      const extra = JSON.parse(extraBodyJson);
      if (!extra || Array.isArray(extra) || typeof extra !== 'object') throw new Error('extra_body 必須是 JSON 物件');
      if (['model', 'messages', 'tools', 'stream'].some(key => Object.hasOwn(extra, key))) throw new Error('extra_body 不可覆寫 model、messages、tools、stream');
      parsed.extra_body = extra;
    } catch (e) { setJsonError(e.message); return; }
    const payload = {
      ...form,
      params_json: JSON.stringify(parsed),
      extra_body: parsed.extra_body,
    };
    onSave(payload, isNew);
  }

  const obj = parseParamsJson(paramsJson);

  if (loadingProfile) return <EmptyState loading />;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title={isNew ? '新增 Profile' : `編輯 Profile — ${profileId}`}>
        <Button variant="ghost" size="sm" icon="arrowLeft" onClick={onCancel}>返回</Button>
        <Button write="super" variant="primary" size="sm" icon="check" loading={saving} onClick={handleSubmit}>儲存</Button>
      </PageHeader>

      <ContentArea style={{ flexDirection: 'row', gap: 14, alignItems: 'flex-start' }}>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 14 }}>

          {isNew && (
            <Panel title="套用模板（可選）">
              <div style={{ padding: '10px 14px', display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                {templates.map(tpl => (
                  <Button key={tpl.id} variant={selectedTemplate === tpl.id ? 'primary' : 'secondary'} size="sm"
                    onClick={() => applyTemplate(tpl.id)}>
                    {tpl.label}
                  </Button>
                ))}
              </div>
            </Panel>
          )}

          <Panel title="身份欄位">
            <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
              <FieldRow label="Profile ID" hint={isNew ? '英數字、_ 或 -' : undefined} required={isNew}>
                <Input value={form.profile_id} disabled={!isNew}
                  onChange={v => setForm(f => ({ ...f, profile_id: v }))}
                  placeholder="例：gpt4-main" mono />
              </FieldRow>
              <FieldRow label="Provider">
                <Input value={form.provider} onChange={v => setForm(f => ({ ...f, provider: v }))}
                  placeholder="例：openai_chat / anthropic / ollama / gemini" mono />
              </FieldRow>
              <FieldRow label="Base URL" hint="完整 API 端點：openai_chat 使用 /v1/chat/completions；ollama 使用 /api/chat。">
                <Input value={form.base_url} onChange={v => setForm(f => ({ ...f, base_url: v }))}
                  placeholder="例：http://localhost:1234/v1/chat/completions" mono />
              </FieldRow>
              <FieldRow label="API Key" hint="留空或含 * 則保留原有金鑰">
                <Input type="password" value={form.api_key}
                  onChange={v => setForm(f => ({ ...f, api_key: v }))}
                  placeholder={!isNew && origProfile ? origProfile.api_key : '輸入 API Key'}
                  mono />
              </FieldRow>
              <FieldRow label="Model">
                <Input value={form.model} onChange={v => setForm(f => ({ ...f, model: v }))}
                  placeholder="例：gpt-4o / claude-opus-4-5 / gemini-2.0-flash" mono />
              </FieldRow>
              <FieldRow label="Template">
                <Input value={form.template} onChange={v => setForm(f => ({ ...f, template: v }))}
                  placeholder="例：chatgpt" mono />
              </FieldRow>
            </div>
          </Panel>

          <Panel title="常用參數">
            <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
              {commonParams.map(spec => (
                <FieldRow key={spec.key} label={PARAM_LABELS[spec.key] || spec.key}
                  hint={spec.doc}>
                  {spec.type === 'bool'
                    ? <Checkbox
                        checked={!!obj[spec.key]}
                        onChange={v => setCommonParam(spec.key, v)}
                        label=""
                      />
                    : <Input
                        type="number"
                        value={obj[spec.key] !== undefined ? String(obj[spec.key]) : ''}
                        onChange={v => {
                          if (v === '') { setCommonParam(spec.key, undefined); return; }
                          const n = parseFloat(v);
                          if (!isNaN(n)) setCommonParam(spec.key, n);
                        }}
                        placeholder={[
                          spec.min !== null && spec.min !== undefined ? `min: ${spec.min}` : '',
                          spec.max !== null && spec.max !== undefined ? `max: ${spec.max}` : '',
                        ].filter(Boolean).join(', ') || undefined}
                        mono
                        style={{ width: 180 }}
                      />
                  }
                </FieldRow>
              ))}
              {commonParams.length === 0 && <EmptyState icon="info" message="無常用參數" />}
            </div>
          </Panel>
        </div>

        <div style={{ width: 320, flexShrink: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
          <Panel title="額外請求內容 extra_body">
            <textarea aria-label="extra_body" value={extraBodyJson} onChange={e => setExtraBodyJson(e.target.value)}
              style={{ width: '100%', minHeight: 100, background: 'var(--c-surface)', color: 'var(--c-fg)' }} />
          </Panel>
          <Panel title="params_json（取樣 / 思考參數）">
            <div style={{ padding: '10px 14px', display: 'flex', flexDirection: 'column', gap: 6 }}>
              <textarea
                value={paramsJson}
                rows={16}
                onChange={e => { setParamsJson(e.target.value); setJsonError(null); }}
                onBlur={e => handleJsonBlur(e.target.value)}
                spellCheck={false}
                style={{
                  width: '100%', padding: '8px 10px',
                  border: `1px solid ${jsonError ? 'var(--c-danger)' : 'var(--c-fg)'}`,
                  borderRadius: 4, background: 'var(--c-bg)',
                  fontFamily: 'var(--font-mono)', fontSize: 12, color: 'var(--c-fg)',
                  resize: 'vertical', outline: 'none', boxSizing: 'border-box',
                  lineHeight: 1.6,
                }}
              />
              {jsonError && (
                <div style={{ display: 'flex', gap: 6, alignItems: 'flex-start', color: 'var(--c-danger)', fontSize: 12 }}>
                  <Icon name="alertTriangle" size={13} style={{ marginTop: 1, flexShrink: 0 }} />
                  {jsonError}
                </div>
              )}
              <div style={{ fontSize: 11, color: 'var(--c-fg3)', lineHeight: 1.5 }}>
                直接編輯 JSON 後游標移出觸發驗證；左側欄位同步更新。
              </div>
            </div>
          </Panel>
          {origProfile?.effective && <Panel title="依已儲存 Profile 推導的參數">
            <div style={{ padding: 14, color: 'var(--c-fg2)', fontSize: 12 }}>
              <div>{origProfile.effective.precedence}</div>
              <div>取樣落點：{origProfile.effective.sampling_location}</div>
              <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{JSON.stringify(origProfile.effective.sampling, null, 2)}</pre>
              <div>額外請求內容 extra_body：{JSON.stringify(origProfile.effective.extra_body)}</div>
              <div>思考欄位：{JSON.stringify(origProfile.effective.thinking)}</div>
              <div>{origProfile.effective.output_limit}</div>
            </div>
          </Panel>}
        </div>
      </ContentArea>
    </div>
  );
}

function FieldRow({ label, hint, required, children, mono }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, minWidth: 0 }}>
      <div style={{ width: 180, flexShrink: 0, paddingTop: 7 }}>
        <div style={{ fontSize: 12, fontWeight: 500, color: 'var(--c-fg2)', fontFamily: mono ? 'var(--font-mono)' : 'inherit' }}>
          {label}{required && <span style={{ color: 'var(--c-danger)' }}>*</span>}
        </div>
        {hint && <div style={{ fontSize: 11, color: 'var(--c-fg3)', lineHeight: 1.4, marginTop: 2 }}>{hint}</div>}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>{children}</div>
    </div>
  );
}

function ProfilesView({ onEdit, onAdd }) {
  const [profiles, setProfiles] = useState([]);
  const [loading, setLoading]   = useState(false);
  const [testing, setTesting] = useState(null);
  const [testResults, setTestResults] = useState({});

  async function testProfile(p) {
    setTesting(p.profile_id);
    const res = await apiCall('POST', `llm/profiles/${encodeURIComponent(p.profile_id)}/test`, {});
    setTestResults(results => ({ ...results, [p.profile_id]: res.success ? res.data : { ok: false, error: res.message } }));
    setTesting(null);
  }

  async function load() {
    setLoading(true);
    const res = await apiCall('GET', 'llm/profiles');
    if (res.success) setProfiles(res.data || []);
    setLoading(false);
  }

  useEffect(() => { load(); }, []);

  async function handleDelete(p) {
    const ok = await showConfirm('刪除 Profile',
      `確定要刪除 Profile "${p.profile_id}" 嗎？受影響的呼叫點：${(p.call_sites || []).join('、') || '無'}。已指向此 Profile 的路由將回退到 __default__。系統預設使用中的 Profile 無法刪除。`);
    if (!ok) return;
    const res = await apiCall('DELETE', `llm/profiles/${encodeURIComponent(p.profile_id)}`);
    if (res.success) {
      showToast(`Profile "${p.profile_id}" 已刪除`, 'success');
      load();
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <Toolbar>
        <span style={{ fontSize: 13, color: 'var(--c-fg2)' }}>Profile 是一組模型連線設定（供應商、網址、金鑰、模型與參數）</span>
        <div style={{ flex: 1 }} />
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={load}>重新整理</Button>
        <Button write="super" variant="primary" size="sm" icon="plus" onClick={onAdd}>新增 Profile</Button>
      </Toolbar>

      <ContentArea>
        <Panel title="Profile 清單" subtitle={`${profiles.length} 個`}>
          {loading && profiles.length === 0
            ? <EmptyState loading />
            : profiles.length === 0
              ? <EmptyState icon="cpu" message="尚無 Profile" hint="點擊「新增 Profile」建立第一個" />
              : <div style={{ overflowX: 'auto' }}>
                  <Table columns={[
                    { label: 'Profile ID', width: 180 },
                    { label: 'Provider',   width: 140 },
                    { label: 'Model' },
                    { label: 'Template',   width: 120 },
                    { label: 'API Key',    width: 140 },
                    { label: '解析到此 Profile 的呼叫點', width: 180 },
                    { label: '設定推導參數', width: 220 },
                    { label: '操作 / 連線測試', width: 260, center: true },
                  ]}>
                    {profiles.map(p => (
                      <TR key={p.profile_id}>
                        <TD mono style={{ fontWeight: 600 }}>{p.profile_id}</TD>
                        <TD mono>{p.provider}</TD>
                        <TD mono>{p.model}</TD>
                        <TD mono muted>{p.template || '—'}</TD>
                        <TD mono muted style={{ fontSize: 11 }}>{p.api_key || '—'}</TD>
                        <TD mono>{(p.call_sites || []).join('、') || '未使用'}</TD>
                        <TD>
                          <details>
                            <summary>依已儲存 Profile 推導的參數（{p.effective?.sampling_location}）</summary>
                            <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{JSON.stringify(p.effective?.sampling, null, 2)}</pre>
                            <div>額外請求內容 extra_body：{JSON.stringify(origProfile.effective.extra_body)}</div>
              <div>思考欄位：{JSON.stringify(p.effective?.thinking)}</div>
                          </details>
                        </TD>
                        <TD center>
                          <div style={{ display: 'flex', gap: 6, justifyContent: 'center' }}>
                            <Button write="super" variant="ghost" size="sm" icon="edit"
                              onClick={() => onEdit(p.profile_id)}>編輯</Button>
                            <Button write="super" variant="secondary" size="sm" loading={testing === p.profile_id} disabled={testing !== null}
                              onClick={() => testProfile(p)}>測試連線</Button>
                            <Button write="super" variant="ghost" size="sm" icon="trash"
                              style={{ color: 'var(--c-danger)' }}
                              disabled={(p.call_sites || []).includes("__default__")} title={(p.call_sites || []).includes("__default__") ? "請先切換系統預設 Profile，再刪除" : "刪除 Profile"}
                              onClick={() => handleDelete(p)} />
                          </div>
                          {(p.call_sites || []).includes('__default__') && <div style={{ fontSize: 12, color: 'var(--c-fg2)' }}>系統預設 Profile；請先切換預設再刪除。</div>}
                          {testResults[p.profile_id] && <div role="status" style={{ fontSize: 12, color: testResults[p.profile_id].ok ? 'var(--c-accent-fg)' : 'var(--c-danger)' }}>
                            {testResults[p.profile_id].ok ? '連線成功' : testResults[p.profile_id].error}
                            {testResults[p.profile_id].latency_ms !== undefined && ` · ${testResults[p.profile_id].latency_ms} ms`}
                          </div>}
                        </TD>
                      </TR>
                    ))}
                  </Table>
                </div>
          }
        </Panel>
      </ContentArea>
    </div>
  );
}

function RoutingView() {
  const [callSites, setCallSites] = useState([]);
  const [profiles,  setProfiles]  = useState([]);
  const [routes,    setRoutes]    = useState({});
  const [saving,    setSaving]    = useState({});
  const [loading,   setLoading]   = useState(false);

  async function load() {
    setLoading(true);
    const [csRes, prRes, rtRes] = await Promise.all([
      apiCall('GET', 'llm/call-sites'),
      apiCall('GET', 'llm/profiles'),
      apiCall('GET', 'llm/routes'),
    ]);
    if (csRes.success) setCallSites(csRes.data.call_sites || []);
    if (prRes.success) setProfiles(prRes.data || []);
    if (rtRes.success) {
      const map = {};
      for (const r of (rtRes.data || [])) map[r.call_site_id] = r;
      setRoutes(map);
    }
    setLoading(false);
  }

  useEffect(() => { load(); }, []);

  async function handleRouteChange(callSiteId, profileId) {
    setSaving(p => ({ ...p, [callSiteId]: true }));
    const res = await apiCall('PUT', 'llm/routes', { call_site_id: callSiteId, profile_id: profileId });
    if (res.success) {
      await load();
      showToast(`路由 ${callSiteId} → ${profileId} 已更新`, 'success');
    }
    setSaving(p => ({ ...p, [callSiteId]: false }));
  }

  const profileOptions = [
    { value: '__default__', label: '（預設）' },
    ...profiles.map(p => ({ value: p.profile_id, label: `${p.profile_id}  [${p.provider} · ${p.model}]` })),
  ];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <Toolbar>
        <span style={{ fontSize: 13, color: 'var(--c-fg2)' }}>每個呼叫點（對話、事實萃取、摘要…）使用哪一個 Profile</span>
        <div style={{ flex: 1 }} />
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={load}>重新整理</Button>
      </Toolbar>

      <ContentArea>
        <Panel title="Call-site → Profile 路由"
          subtitle={`${callSites.length} 個呼叫點`}>
          {loading && callSites.length === 0
            ? <EmptyState loading />
            : callSites.length === 0
              ? <EmptyState icon="activity" message="無可用呼叫點" />
              : <div style={{ overflowX: 'auto' }}>
                  <Table columns={[
                    { label: 'Call Site',  width: 260 },
                    { label: '種類',       width: 100 },
                    { label: 'Profile 指派' },
                    { label: '解析後 Profile', width: 280 },
                    { label: '狀態',       width: 80, center: true },
                  ]}>
                    {callSites.map(cs => {
                      const resolved = routes[cs];
                      const current = resolved?.profile_id || '__default__';
                      return (
                        <TR key={cs}>
                          <TD mono style={{ fontWeight: current !== '__default__' ? 600 : 400 }}>{cs}</TD>
                          <TD>
                            <Badge color="var(--c-accent-fg)">{cs === '__default__' ? '系統預設' : '內建'}</Badge>
                          </TD>
                          <TD>
                            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                              <Select
                                value={current}
                                options={cs === '__default__' ? profileOptions.filter(p => p.value !== '__default__') : profileOptions}
                                onChange={v => handleRouteChange(cs, v)}
                                disabled={saving[cs] || !nyaCanWrite('super')}
                                style={{ minWidth: 280 }}
                              />
                              {saving[cs] && <Spinner size={14} />}
                            </div>
                          </TD>
                          <TD>
                            <div>{cs === "chat" ? "現役" : cs === "summarization" ? (resolved?.execution_enabled ? "自動摘要已開啟" : "自動摘要已關閉，不執行、不注入") : "Fallback"}</div><div className="mono">{resolved?.effective_profile_id || '未設定'}</div>
                            <div style={{ fontSize: 12, color: 'var(--c-fg2)' }}>{resolved?.provider} · {resolved?.model}</div>
                            {resolved?.source === '__default__' && cs !== '__default__' && <div>回退至系統預設</div>}
                          </TD>
                          <TD center>
                            {current === '__default__'
                              ? <StatusBadge idle>預設</StatusBadge>
                              : <StatusBadge ok>自訂</StatusBadge>}
                          </TD>
                        </TR>
                      );
                    })}
                  </Table>
                </div>
          }
        </Panel>

        <div style={{ fontSize: 12, color: 'var(--c-fg2)', lineHeight: 1.7, padding: '0 2px' }}>
          「預設」表示未指定路由，系統使用 <code>__default__</code> 路由指向的 Profile。
          更改後立即生效，無需重啟。
        </div>
      </ContentArea>
    </div>
  );
}

function PageG() {
  const [tab,       setTab]       = useState('profiles');
  const [view,      setView]      = useState('list');
  const [editingId, setEditingId] = useState(null);
  const [saving,    setSaving]    = useState(false);

  const [templates, setTemplates] = useState([]);
  const [catalog,   setCatalog]   = useState([]);
  const [metaLoaded, setMetaLoaded] = useState(false);

  useEffect(() => {
    Promise.all([
      apiCall('GET', 'llm/templates'),
      apiCall('GET', 'llm/param-catalog'),
    ]).then(([tplRes, catRes]) => {
      if (tplRes.success) setTemplates(tplRes.data || []);
      if (catRes.success) setCatalog(catRes.data || []);
      setMetaLoaded(true);
    });
  }, []);

  async function handleSave(payload, isNew) {
    setSaving(true);
    const method = isNew ? 'POST' : 'PUT';
    const path   = isNew ? 'llm/profiles' : `llm/profiles/${encodeURIComponent(payload.profile_id)}`;
    const res = await apiCall(method, path, payload);
    setSaving(false);
    if (res.success) {
      showToast(`Profile "${payload.profile_id}" 已儲存`, 'success');
      setView('list');
      setEditingId(null);
    }
  }

  function openEdit(id) {
    setEditingId(id);
    setView('edit');
  }

  function openAdd() {
    setEditingId(null);
    setView('edit');
  }

  function backToList() {
    setView('list');
    setEditingId(null);
  }

  if (view === 'edit') {
    if (!metaLoaded) return <EmptyState loading />;
    return (
      <ProfileEditor
        profileId={editingId}
        templates={templates}
        catalog={catalog}
        onSave={handleSave}
        onCancel={backToList}
        saving={saving}
      />
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <PageHeader title="LLM 模型" desc="管理模型連線，並指定 NyaChat 各項工作使用的模型" />
      <TabBar
        tabs={[
          { id: 'profiles', label: 'Profile 清單' },
          { id: 'routing',  label: '呼叫點路由' },
        ]}
        active={tab}
        onChange={setTab}
      />
      {tab === 'profiles' && (
        <ProfilesView
          onEdit={openEdit}
          onAdd={openAdd}
        />
      )}
      {tab === 'routing' && <RoutingView />}
    </div>
  );
}

window.PageG = PageG;
