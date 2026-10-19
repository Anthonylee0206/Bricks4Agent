const { useState, useEffect } = React;

function TabC4({ userId, onChanged }) {
  const [notes, setNotes] = useState([]);
  const [references, setReferences] = useState([]);
  const [selections, setSelections] = useState([]);
  const [capacity, setCapacity] = useState({});
  const [total, setTotal] = useState(0);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false);
  const [form, setForm] = useState({ key: '', content: '', expiry: 'never', date: '', sourceIds: '' });
  const [editing, setEditing] = useState(null);
  const [scope, setScope] = useState('normal');
  const [conversation, setConversation] = useState('');
  const [terms, setTerms] = useState('');
  const [selection, setSelection] = useState(null);
  const [error, setError] = useState('');
  const base = 'users/' + encodeURIComponent(userId);
  const scopeQuery = '?scope=' + scope + (scope === 'external' ? '&conversation_id=' + encodeURIComponent(conversation) : '');

  async function load(page = offset) {
    setLoading(true);
    const responses = await Promise.all([
      apiCall('GET', base + '/notes?limit=50&offset=' + page),
      apiCall('GET', base + '/hide-references'),
      apiCall('GET', base + '/selections'),
    ]);
    const [n, r, s] = responses;
    if (n.success) { setNotes(n.data.notes); setTotal(n.data.total); setCapacity(n.data); }
    if (r.success) setReferences(r.data.references);
    if (s.success) setSelections(s.data.selections);
    setError(responses.find(res => !res.success)?.message || '');
    setLoading(false);
  }
  useEffect(() => { setOffset(0); load(0); }, [userId]);

  async function save() {
    const body = { content: form.content, expiry: { mode: form.expiry } };
    if (editing) body.note_id = editing; else body.key = form.key;
    if (form.expiry === 'on_date') body.expiry.date = form.date;
    if (form.sourceIds.trim()) body.source_message_ids = form.sourceIds.trim().split(/\s+/);
    const res = await apiCall('POST', base + '/notes' + scopeQuery, body);
    if (!res.success) { setError(res.message); return; }
    setEditing(null); setForm({ key: '', content: '', expiry: 'never', date: '', sourceIds: '' });
    showToast('筆記已保存', 'success'); load(); onChanged?.();
  }
  function edit(note) {
    setEditing(note.note_id); setScope(note.scope); setConversation(note.conversation_id || '');
    setForm({ key: note.key, content: note.content, expiry: 'keep', date: '', sourceIds: note.source_message_ids.join(' ') });
  }
  async function find(note) {
    const targetScope = note?.scope || scope;
    const targetConversation = note?.conversation_id || conversation;
    const query = '?scope=' + targetScope + (targetScope === 'external' ? '&conversation_id=' + encodeURIComponent(targetConversation) : '');
    const res = await apiCall('POST', base + '/forget-find' + query, note ? { note_ids: [note.note_id] } : { terms: terms.trim().split(/\s+/), match: 'any', scope: 'all' });
    if (!res.success) { setError(res.message); return; }
    setSelection({ ...res.data.data, query }); load();
  }
  async function forget() {
    if (!await showConfirm('隱藏這組候選？', '只隱藏選中的筆記、訊息、相關回覆及工作紀錄；稽核保留原文，不承諾同義表達都忘掉。')) return;
    const res = await apiCall('POST', base + '/forget' + selection.query, { selection_id: selection.selection_id });
    if (!res.success) { setError(res.message); return; }
    setSelection(null); load(); onChanged?.();
  }
  async function unforget(id) {
    if (!await showConfirm('撤銷這一筆隱藏引用？', '其他有效隱藏引用仍生效；筆記 key 衝突或容量不足會拒絕還原。')) return;
    const res = await apiCall('POST', base + '/hide-references/' + encodeURIComponent(id) + '/unforget', {});
    if (!res.success) { setError(res.message); return; }
    load(); onChanged?.();
  }
  async function restore(note) {
    const query = '?scope=' + note.scope + (note.scope === 'external' ? '&conversation_id=' + encodeURIComponent(note.conversation_id) : '');
    const res = await apiCall('POST', base + '/notes/' + encodeURIComponent(note.note_id) + '/restore' + query, { version: note.version });
    if (!res.success) { setError(res.message); return; }
    load(); onChanged?.();
  }
  const groups = [...new Set(references.filter(r => r.is_active).map(r => r.forget_id))];
  return <ContentArea>
    {error && <div role="alert">{error}</div>}
    <Panel title="筆記版本" subtitle={'容量 ' + (capacity.capacity ?? '—') + ' 則；單則 ' + (capacity.max_length ?? '—') + ' 字'} actions={<Button size="sm" loading={loading} onClick={() => load()}>重新整理</Button>}>
      {notes.map(note => <div key={note.note_id + ':' + note.version} style={{ padding: 14, borderBottom: '1px solid var(--c-border)' }}>
        <div>{note.key} · {note.note_id} · v{note.version} · {note.scope}{note.conversation_id ? ' / ' + note.conversation_id : ''}</div>
        <div>{note.hidden ? '已隱藏' : note.expired ? '已過期' : note.is_current ? '有效版本' : '歷史版本'} · 到期 {note.expires_at ? fmtTime(note.expires_at) : '長期'}</div>
        <div style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{note.content}</div>
        <div>來源訊息：{note.source_message_ids.join('、') || '無'}</div>
        <Button write="user" size="sm" onClick={() => edit(note)} disabled={note.hidden}>更正</Button>
        <Button write="user" size="sm" onClick={() => find(note)}>遺忘候選</Button>
        {!note.is_current && <Button write="user" size="sm" onClick={() => restore(note)} disabled={note.hidden}>還原版本</Button>}
      </div>)}
      <Pagination total={total} limit={50} offset={offset} onPage={page => { setOffset(page); load(page); }} />
    </Panel>
    <Panel title={editing ? '更正筆記 · ' + editing : '新增筆記'}>
      <div style={{ padding: 14, display: 'grid', gap: 10 }}>
        <Select value={scope} onChange={setScope} options={[{ value: 'normal', label: '一般會話' }, { value: 'external', label: '外部明確會話' }]} />
        {scope === 'external' && <Input value={conversation} onChange={setConversation} placeholder="conversation_id" />}
        <Input value={form.key} disabled={!!editing} onChange={key => setForm(f => ({ ...f, key }))} placeholder="新增 key；更正使用穩定 note_id" />
        <textarea value={form.content} onChange={e => setForm(f => ({ ...f, content: e.target.value }))} placeholder="筆記全文" />
        <Select value={form.expiry} onChange={expiry => setForm(f => ({ ...f, expiry }))} options={[{ value: 'keep', label: '沿用期限' }, { value: 'never', label: '長期有效' }, { value: 'on_date', label: '台灣日期含當日' }]} />
        {form.expiry === 'on_date' && <Input value={form.date} onChange={date => setForm(f => ({ ...f, date }))} placeholder="yyyy-MM-dd" />}
        <Input value={form.sourceIds} onChange={sourceIds => setForm(f => ({ ...f, sourceIds }))} placeholder="來源 message ids，以空白分隔" />
        <Button write="user" onClick={save}>保存</Button>
        {editing && <Button onClick={() => setEditing(null)}>改為新增</Button>}
      </div>
    </Panel>
    <Panel title="遺忘候選與隱藏引用" subtitle="稽核保留原始資料；承諾限模型可見性">
      <div style={{ padding: 14 }}>
        <Input value={terms} onChange={setTerms} placeholder="查找候選關鍵字" />
        <Button write="user" onClick={() => find()} disabled={!terms.trim()}>查找固定候選</Button>
        {selection && <div><div>selection_id：{selection.selection_id}</div><JsonBlock raw={JSON.stringify({ notes: selection.notes, messages: selection.messages }, null, 2)} /><Button write="user" onClick={forget}>隱藏候選</Button><Button onClick={() => setSelection(null)}>取消</Button></div>}
        {groups.map(id => <div key={id}>{id} · {references.filter(r => r.forget_id === id && r.is_active).length} 個引用 <Button write="user" size="sm" onClick={() => unforget(id)}>撤銷此筆</Button></div>)}
        <details><summary>隱藏引用原始資料</summary><JsonBlock raw={JSON.stringify(references, null, 2)} /></details>
        <details><summary>候選紀錄</summary><JsonBlock raw={JSON.stringify(selections, null, 2)} /></details>
      </div>
    </Panel>
  </ContentArea>;
}

function TabC5({ userId, onChanged }) {
  const [data, setData] = useState(null);
  const [error, setError] = useState('');
  const base = 'users/' + encodeURIComponent(userId) + '/summaries';
  async function load() {
    const res = await apiCall('GET', base);
    if (res.success) setData(res.data); else setError(res.message);
  }
  useEffect(() => { load(); }, [userId]);
  async function mutate(path) {
    const res = await apiCall('POST', path, {});
    if (res.success) { load(); onChanged?.(); } else setError(res.message);
  }
  return <ContentArea>
    {error && <div role="alert">{error}</div>}
    <Panel title="摘要版本與依賴" actions={<Button onClick={load}>重新整理</Button>}>
      <Button write="user" onClick={() => mutate(base + '/trigger')}>立即摘要</Button>
      {!data ? <EmptyState loading /> : data.summaries.map(s => <div key={s.summary_id} style={{ padding: 14, borderBottom: '1px solid var(--c-border)' }}>
        <div>{s.topic_title || s.topic_id} · v{s.version} · {s.scope}</div>
        <div>{s.invalidated ? '來源隱藏，已失效' : s.is_active ? '生效' : '歷史版本'} · 可見性版本 {s.visibility_version}</div>
        <div style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{s.summary_text}</div>
        <div>來源訊息：{s.source_message_ids.join('、') || '無'}</div>
        {!s.is_active && <Button write="user" disabled={s.invalidated} onClick={() => mutate(base + '/' + encodeURIComponent(s.summary_id) + '/rollback')}>還原版本</Button>}
      </div>)}
    </Panel>
  </ContentArea>;
}
