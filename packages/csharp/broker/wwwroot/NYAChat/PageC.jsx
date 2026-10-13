/* Page C — 使用者詳情（主頁 + C1 概覽 + C2 訊息） */
const { useState, useEffect, useCallback } = React;

const C_TABS = [
  { id: 'overview',  label: '概覽',     icon: 'user'     },
  { id: 'messages',  label: '訊息',     icon: 'message'  },
  { id: 'topics',    label: '話題',     icon: 'list'     },
  { id: 'facts',     label: '事實',     icon: 'database' },
  { id: 'summaries', label: '摘要',     icon: 'archive'  },
  { id: 'audit',     label: '稽核',     icon: 'log'      },
];

function PageC({ userId, onNavigate }) {
  const [tab, setTab]           = useState('overview');
  const [overview, setOverview] = useState(null);
  const [ovLoading, setOvLoad]  = useState(false);
  const [ovError, setOvError]   = useState(false);

  const loadOverview = useCallback(async () => {
    setOvLoad(true);
    setOvError(false);
    const res = await apiCall('GET', `users/${userId}/overview`);
    if (res.success) {
      setOverview(res.data);
    } else {
      setOvError(true);
    }
    setOvLoad(false);
  }, [userId]);

  useEffect(() => { loadOverview(); }, [loadOverview]);

  const [topicCount, setTopicCount] = useState(null);

  const loadTopicCount = useCallback(async () => {
    const res = await apiCall('GET', `users/${userId}/topics`);
    if (res.success) setTopicCount(res.data.total);
  }, [userId]);

  useEffect(() => { loadTopicCount(); }, [loadTopicCount]);

  const tabsWithBadge = C_TABS.map(t => {
    let badge;
    if (overview) {
      if (t.id === 'messages')  badge = overview.messages.total - overview.messages.deleted;
      if (t.id === 'facts')     badge = overview.facts.total_active;
      if (t.id === 'summaries') badge = overview.summaries.total_versions;
    }
    if (t.id === 'topics' && topicCount != null) badge = topicCount;
    return { ...t, badge };
  });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* User header */}
      <div style={{
        padding: '14px 24px 12px', flexShrink: 0, background: 'var(--c-surface)',
      }}>
        <div style={{ fontSize: 13, color: 'var(--c-fg2)', marginBottom: 4 }}>
          <a href="#users" onClick={e => { e.preventDefault(); onNavigate('users'); }}>使用者</a>
          <span style={{ margin: '0 6px', color: 'var(--c-fg4)' }}>/</span>
          <span>詳情</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <h1 className="mono" style={{ fontSize: 17, fontWeight: 600, color: 'var(--c-fg)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {userId}
          </h1>
          <Badge>{CHANNEL_LABELS[channelOf(userId)] || channelOf(userId)}</Badge>
          {overview && <Badge>Soul：{overview.soul_id}</Badge>}
        </div>
      </div>

      <TabBar tabs={tabsWithBadge} active={tab} onChange={setTab} />

      <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {tab === 'overview'  && <TabC1 userId={userId} overview={overview} loading={ovLoading} error={ovError} onRefresh={loadOverview} />}
        {tab === 'messages'  && <TabC2 userId={userId} onChanged={loadOverview} />}
        {tab === 'topics'    && <TabCTopics userId={userId} onChanged={() => { loadOverview(); loadTopicCount(); }} />}
        {tab === 'facts'     && <TabC4 userId={userId} onChanged={loadOverview} />}
        {tab === 'summaries' && <TabC5 userId={userId} onChanged={loadOverview} />}
        {tab === 'audit'     && <TabC6 userId={userId} />}
        {tab === 'roles'     && <NyaUserRolesTab userId={userId} />}
      </div>
    </div>
  );
}

/* ── C1：概覽 ─────────────────────────────────────────────── */
function TabC1({ userId, overview, loading, error, onRefresh }) {
  const [nukeLoading, setNuke] = useState(false);

  async function handleNuke() {
    if (!overview) return;
    const { messages, facts, summaries } = overview;
    const ok = await showConfirm(
      `清除全部記憶 — ${userId}？`,
      `將軟刪除 ${messages.active + messages.summarized} 筆訊息、硬刪除 ${facts.total_active} 筆事實、刪除 ${summaries.total_versions} 個摘要。此操作不可還原。`
    );
    if (!ok) return;
    setNuke(true);
    const res = await apiCall('DELETE', `users/${userId}`, { confirm: 'all' });
    if (res.success) {
      showToast(`已清除全部記憶：訊息 ${res.data.soft_deleted_messages}、事實 ${res.data.deleted_facts}、摘要 ${res.data.deleted_summaries}`, 'success');
      onRefresh();
    }
    setNuke(false);
  }

  if (loading && !overview) return <EmptyState loading />;
  if (error && !overview) return (
    <ContentArea>
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 12, padding: '48px 16px' }}>
        <Icon name="alertTriangle" size={24} color="var(--c-danger)" />
        <div style={{ fontSize: 14, color: 'var(--c-danger)', fontWeight: 500 }}>概覽載入失敗</div>
        <div style={{ fontSize: 12, color: 'var(--c-fg2)' }}>請確認後端服務正常，或重新整理頁面。</div>
        <Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={onRefresh}>重試</Button>
      </div>
    </ContentArea>
  );
  if (!overview) return <EmptyState loading={loading} />;

  const { messages: m, facts: f, summaries: s } = overview;
  const catEntries = Object.entries(f.categories || {});
  const maxCat = Math.max(1, ...catEntries.map(([, c]) => c));

  return (
    <ContentArea>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: -4, flexShrink: 0 }}>
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={onRefresh}>重新整理</Button>
      </div>

      {/* Message stat tiles */}
      <StatTiles cols={4} items={[
        { label: '訊息總計', value: m.total },
        { label: '有效（LLM 可見）', value: m.active, accent: 'var(--c-accent-fg)' },
        { label: '已摘要', value: m.summarized },
        { label: '已刪除', value: m.deleted, accent: m.deleted ? 'var(--c-fg2)' : undefined },
      ]} />

      {/* Two-column detail */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 14 }}>
        {/* Facts */}
        <Panel title="事實" subtitle={`${f.total_active} 筆有效`}>
          {catEntries.length === 0
            ? <EmptyState message="尚無有效事實" />
            : <div style={{ padding: '4px 0' }}>
                {catEntries.map(([cat, cnt]) => (
                  <div key={cat} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '7px 14px' }}>
                    <span className="mono" style={{ fontSize: 12, color: 'var(--c-fg)', width: 96, flexShrink: 0 }}>{cat}</span>
                    <div style={{ flex: 1, height: 6, background: 'var(--c-border-soft)', borderRadius: 1, overflow: 'hidden' }}>
                      <div style={{ width: `${(cnt / maxCat) * 100}%`, height: '100%', background: 'var(--c-accent)' }} />
                    </div>
                    <span className="mono" style={{ fontSize: 13, fontWeight: 500, width: 24, textAlign: 'right' }}>{cnt}</span>
                  </div>
                ))}
              </div>
          }
        </Panel>

        {/* Summaries — 大項#3：摘要依話題隔離，概覽改顯示 per-topic 統計 */}
        <Panel title="摘要" subtitle="依話題隔離">
          <KVGrid cols={1} items={[
            { label: '有摘要的話題', value: s.active_count ?? 0, mono: true },
            { label: '摘要版本總數', value: s.total_versions, mono: true },
            { label: '最後涵蓋至 Seq', value: s.latest_covered_to_seq || '—', mono: true },
          ]} />
        </Panel>
      </div>

      {/* Time + Soul binding */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 14 }}>
        <Panel title="時間資訊">
          <KVGrid cols={1} items={[
            { label: '最後訊息時間', value: fmtTime(overview.last_message_at), mono: true },
            { label: '最後事實更新', value: fmtTime(overview.last_fact_updated_at), mono: true },
          ]} />
        </Panel>
        <SoulBindPanel userId={userId} currentSoulId={overview.soul_id} onSoulChanged={onRefresh} />
      </div>

      <DangerZone>
        <DangerAction
          label="清除全部記憶"
          description="軟刪除所有訊息，硬刪除所有事實與摘要"
          buttonLabel="清除全部記憶"
          loading={nukeLoading}
          onConfirm={handleNuke}
        />
      </DangerZone>
    </ContentArea>
  );
}

/* ── C2：訊息管理 ─────────────────────────────────────────── */
function TabC2({ userId, onChanged }) {
  const [messages, setMessages]   = useState([]);
  const [total, setTotal]         = useState(0);
  const [loading, setLoading]     = useState(false);
  const [limit, setLimit]         = useState(50);
  const [offset, setOffset]       = useState(0);
  const [selected, setSelected]   = useState(new Set());
  const [detailMsg, setDetail]    = useState(null);
  const [detailLoad, setDLoad]    = useState(false);
  const [delLoading, setDelLoad]  = useState(false);
  const [selDelLoad, setSelDel]   = useState(false);
  const [topics, setTopics]       = useState([]);
  const [topicFilter, setTopicFilter] = useState(''); // 大項#5：依話題篩選

  async function loadMessages(lim, off, topic) {
    const t = topic !== undefined ? topic : topicFilter;
    setLoading(true);
    const params = { limit: lim, offset: off };
    if (t) params.topic_id = t;
    const res = await apiCall('GET', `users/${userId}/messages`, params);
    if (res.success) { setMessages(res.data.messages); setTotal(res.data.total); }
    setLoading(false);
    setSelected(new Set());
  }

  async function loadTopics() {
    const res = await apiCall('GET', `users/${userId}/topics`);
    if (res.success) setTopics(res.data.topics);
  }

  useEffect(() => { setTopicFilter(''); loadTopics(); loadMessages(limit, 0, ''); }, [userId]);

  function toggleSelect(id) {
    setSelected(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  function toggleSelectAll() {
    const selectable = messages.filter(m => !m.is_deleted).map(m => m.message_id);
    if (selectable.every(id => selected.has(id))) {
      setSelected(new Set());
    } else {
      setSelected(new Set(selectable));
    }
  }

  async function showDetail(msg) {
    setDetail({ ...msg, _loading: true });
    setDLoad(true);
    const res = await apiCall('GET', `users/${userId}/messages/${msg.message_id}`);
    setDetail(res.success ? res.data : null);
    setDLoad(false);
  }

  async function handleDeleteSelected() {
    const ids = [...selected];
    const ok = await showConfirm(`軟刪除選取的 ${ids.length} 筆訊息？`, '選取的訊息將被軟刪除，可透過資料庫還原。');
    if (!ok) return;
    setSelDel(true);
    const res = await apiCall('POST', `users/${userId}/messages/soft-delete`, { message_ids: ids });
    if (res.success) {
      showToast(`已軟刪除 ${res.data.soft_deleted} 筆訊息`, 'success');
      loadMessages(limit, offset);
      onChanged && onChanged();
    }
    setSelDel(false);
  }

  async function handleDeleteAll() {
    // 刪除範圍 = 此用戶所有話題的未刪除訊息（含已摘要），不受話題篩選與分頁影響 → 以概覽統計計數。
    const ov = await apiCall('GET', `users/${userId}/overview`);
    if (!ov.success) return;
    const count = ov.data.messages.active + ov.data.messages.summarized;
    if (count === 0) { showToast('沒有可刪除的訊息', 'info'); return; }
    const ok = await showConfirm('軟刪除全部訊息？', `將軟刪除此用戶所有話題的全部 ${count} 筆訊息。`);
    if (!ok) return;
    setDelLoad(true);
    const res = await apiCall('DELETE', `users/${userId}/messages`, { confirm: 'all' });
    if (res.success) { showToast(`已軟刪除 ${res.data.soft_deleted} 筆訊息`, 'success'); loadMessages(limit, offset); onChanged && onChanged(); }
    setDelLoad(false);
  }

  const selectableCount = messages.filter(m => !m.is_deleted).length;
  const allSelected = selectableCount > 0 && messages.filter(m => !m.is_deleted).every(m => selected.has(m.message_id));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: 'var(--c-bg)' }}>
      <Toolbar>
        <Button variant="ghost" size="sm" icon="refresh" loading={loading}
          onClick={() => loadMessages(limit, offset)}>重新整理</Button>
        <span style={{ fontSize: 12, color: 'var(--c-fg2)' }}>話題</span>
        <Select value={topicFilter}
          options={[{ value: '', label: '全部話題' },
            ...topics.map(t => ({ value: t.topic_id, label: (t.is_active ? '★ ' : '') + (t.title || t.topic_id) }))]}
          onChange={v => { setTopicFilter(v); setOffset(0); loadMessages(limit, 0, v); }}
          style={{ width: 200 }} />
        <div style={{ flex: 1 }} />
        {selected.size > 0 && (
          <Button variant="danger" size="sm" loading={selDelLoad} onClick={handleDeleteSelected}>
            軟刪除選取（{selected.size}）
          </Button>
        )}
        <Button variant="danger" size="sm" loading={delLoading} onClick={handleDeleteAll}>軟刪除全部</Button>
      </Toolbar>

      <div style={{ flex: 1, overflow: 'auto' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13, background: 'var(--c-surface)' }}>
          <thead>
            <tr style={{ background: 'var(--c-bg)', borderBottom: '1px solid var(--c-border)' }}>
              <th style={{ padding: '7px 12px', width: 36, position: 'sticky', top: 0 }}>
                <input type="checkbox" checked={allSelected} onChange={toggleSelectAll}
                  style={{ cursor: 'pointer', accentColor: 'var(--c-accent)' }} />
              </th>
              {[['#', 56], ['角色', 80], ['內容', null], ['話題', 140], ['狀態', 90], ['時間', 120], ['', 72]].map(([h, w], i) => (
                <th key={i} style={{
                  padding: '7px 12px', textAlign: 'left', width: w || undefined,
                  fontSize: 12, fontWeight: 500, color: 'var(--c-fg2)', whiteSpace: 'nowrap',
                  position: 'sticky', top: 0, background: 'var(--c-bg)',
                }}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {loading && messages.length === 0
              ? <LoadingRow cols={8} />
              : messages.length === 0
                ? <EmptyRow cols={8} message="無對話記錄" />
                : messages.map(msg => {
                    const isDel = msg.is_deleted, isSum = msg.is_summarized;
                    const isSel = selected.has(msg.message_id);
                    return (
                      <TR key={msg.message_id} selected={isSel}>
                        <td style={{ padding: '8px 12px', width: 36 }}>
                          {!isDel && (
                            <input type="checkbox" checked={isSel}
                              onChange={() => toggleSelect(msg.message_id)}
                              style={{ cursor: 'pointer', accentColor: 'var(--c-accent)' }} />
                          )}
                        </td>
                        <td className="mono" style={{ padding: '8px 12px', fontSize: 12, color: 'var(--c-fg2)', whiteSpace: 'nowrap' }}>
                          {msg.sequence}
                        </td>
                        <td style={{ padding: '8px 12px', whiteSpace: 'nowrap' }}>
                          <span style={{ fontSize: 13, color: msg.role === 'user' ? 'var(--c-fg)' : 'var(--c-accent-fg)' }}>{msg.role === 'user' ? '使用者' : msg.role === 'assistant' ? 'NYA' : msg.role}</span>
                        </td>
                        <td style={{ padding: '8px 12px', maxWidth: 360 }}>
                          <span style={{
                            fontSize: 13, color: isDel ? 'var(--c-fg3)' : 'var(--c-fg)',
                            textDecoration: isDel ? 'line-through' : 'none',
                            display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                          }}>{msg.content_preview}</span>
                        </td>
                        <td title={msg.topic_id || ''} style={{ padding: '8px 12px', fontSize: 12, color: 'var(--c-fg2)', whiteSpace: 'nowrap', maxWidth: 140, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                          {msg.topic_id ? ((topics.find(t => t.topic_id === msg.topic_id) || {}).title || msg.topic_id) : <span style={{ color: 'var(--c-fg4)' }}>—</span>}
                        </td>
                        <td style={{ padding: '8px 12px', whiteSpace: 'nowrap' }}>
                          <div style={{ display: 'flex', gap: 4 }}>
                            {isDel && <Badge color="var(--c-danger)">已刪除</Badge>}
                            {isSum && <Badge>已摘要</Badge>}
                          </div>
                        </td>
                        <td className="mono" style={{ padding: '8px 12px', fontSize: 11, color: 'var(--c-fg2)', whiteSpace: 'nowrap' }}>
                          {fmtTimeShort(msg.created_at)}
                        </td>
                        <td style={{ padding: '8px 12px' }}>
                          <Button variant="ghost" size="sm" icon="eye" onClick={() => showDetail(msg)}>全文</Button>
                        </td>
                      </TR>
                    );
                  })
            }
          </tbody>
        </table>
      </div>

      <Pagination
        total={total} limit={limit} offset={offset}
        onPage={off => { setOffset(off); loadMessages(limit, off); }}
        onLimitChange={lim => { setLimit(lim); setOffset(0); loadMessages(lim, 0); }}
      />

      {detailMsg && (
        <Modal title={`訊息全文 · ${detailMsg.message_id || detailMsg.MessageId}`}
          onClose={() => setDetail(null)} width={640}>
          {detailLoad
            ? <EmptyState loading />
            : <>
                <KVGrid cols={2} items={[
                  { label: 'Message ID', value: detailMsg.message_id, mono: true, wide: true },
                  { label: 'Role',       value: detailMsg.role,       mono: true },
                  { label: 'Sequence',   value: detailMsg.sequence,   mono: true },
                  { label: 'Topic ID',   value: detailMsg.topic_id || '—', mono: true },
                  { label: 'Is Deleted',    value: detailMsg.is_deleted    ? '是' : '否' },
                  { label: 'Is Summarized', value: detailMsg.is_summarized ? '是' : '否' },
                  { label: '建立時間',    value: fmtTime(detailMsg.created_at), mono: true, wide: true },
                ]} />
                <div style={{ padding: '12px 14px', borderTop: '1px solid var(--c-border)' }}>
                  <div style={{ fontSize: 11, fontWeight: 500, color: 'var(--c-fg2)', marginBottom: 6 }}>內容</div>
                  <div style={{
                    fontSize: 13, lineHeight: 1.7, color: 'var(--c-fg)',
                    whiteSpace: 'pre-wrap', wordBreak: 'break-word',
                    maxHeight: 220, overflowY: 'auto',
                    border: '1px solid var(--c-border)', padding: 12, background: 'var(--c-bg)',
                  }}>{detailMsg.content}</div>
                </div>
                <div>
                  <div style={{ padding: '10px 14px 0', fontSize: 11, fontWeight: 500, color: 'var(--c-fg2)' }}>Metadata JSON</div>
                  <div style={{ padding: '6px 14px 14px' }}>
                    <div style={{ border: '1px solid var(--c-border)' }}><JsonBlock raw={detailMsg.metadata_json} flush /></div>
                  </div>
                </div>
              </>
          }
        </Modal>
      )}
    </div>
  );
}

/* ── 話題管理 ─────────────────────────────────────────────── */
function TabCTopics({ userId, onChanged }) {
  const [topics, setTopics]       = useState([]);
  const [loading, setLoading]     = useState(false);
  const [newLoading, setNewLoad]  = useState(false);
  const [swLoading, setSwLoad]    = useState({});
  const [delLoading, setDelLoad]  = useState({});
  const [renameTarget, setRenameTarget] = useState(null); // { topic_id, title }
  const [renameValue, setRenameValue]   = useState('');
  const [renameLoading, setRenameLoad]  = useState(false);

  async function loadTopics() {
    setLoading(true);
    const res = await apiCall('GET', `users/${userId}/topics`);
    if (res.success) setTopics(res.data.topics);
    setLoading(false);
  }

  useEffect(() => { loadTopics(); }, [userId]);

  async function handleNewTopic() {
    setNewLoad(true);
    const res = await apiCall('POST', `users/${userId}/topics`);
    if (res.success) {
      showToast(`已建立新話題「${res.data.title}」並設為使用中`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setNewLoad(false);
  }

  async function handleSwitch(topicId, title) {
    const ok = await showConfirm(`切換至話題「${title}」？`, '後續對話訊息將被標記至此話題。');
    if (!ok) return;
    setSwLoad(p => ({ ...p, [topicId]: true }));
    const res = await apiCall('POST', `users/${userId}/topics/${topicId}/switch`);
    if (res.success) {
      showToast(`已切換至話題「${res.data.title}」`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setSwLoad(p => ({ ...p, [topicId]: false }));
  }

  // 大項#2：刪除話題（管理端，不開放 LLM）。連同其訊息（軟刪除）與摘要一併清除。
  async function handleDelete(topicId, title, msgCount) {
    const ok = await showConfirm(`刪除話題「${title}」？`,
      `將軟刪除此話題的 ${msgCount} 則訊息、刪除其摘要並移除話題。若是使用中的話題，會自動改用最近的其他話題。此操作不可還原。`);
    if (!ok) return;
    setDelLoad(p => ({ ...p, [topicId]: true }));
    const res = await apiCall('DELETE', `users/${userId}/topics/${topicId}`);
    if (res.success) {
      showToast(`已刪除話題「${title}」`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setDelLoad(p => ({ ...p, [topicId]: false }));
  }

  // 大項#4/#1：話題改名（網頁，同 LLM rename_topic 對應端點）。
  function openRename(t) { setRenameTarget(t); setRenameValue(t.title || ''); }
  async function submitRename() {
    if (!renameTarget || !renameValue.trim()) return;
    setRenameLoad(true);
    const res = await apiCall('POST', `users/${userId}/topics/${renameTarget.topic_id}/rename`, { title: renameValue.trim() });
    if (res.success) {
      showToast(`已將話題改名為「${res.data.title}」`, 'success');
      setRenameTarget(null);
      loadTopics();
      onChanged && onChanged();
    }
    setRenameLoad(false);
  }

  const activeTopic = topics.find(t => t.is_active);

  return (
    <ContentArea>
      {activeTopic && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 10,
          padding: '10px 14px', background: 'var(--c-accent-soft)',
          border: '1px solid var(--c-accent)', borderRadius: 6, marginBottom: 2,
        }}>
          <Icon name="list" size={14} color="var(--c-accent-fg)" />
          <span style={{ fontSize: 12, color: 'var(--c-accent-fg)', fontWeight: 500 }}>目前使用中的話題：</span>
          <span className="mono" style={{ fontSize: 12, color: 'var(--c-fg)', fontWeight: 600 }}>{activeTopic.title}</span>
          <span className="mono" style={{ fontSize: 10, color: 'var(--c-fg3)', marginLeft: 4 }}>({activeTopic.topic_id})</span>
          <span style={{ flex: 1 }} />
          <span style={{ fontSize: 11, color: 'var(--c-fg2)' }}>{activeTopic.msg_count} 則訊息</span>
        </div>
      )}

      <Panel title="話題列表" subtitle={`${topics.length} 個話題`}
        actions={
          <div style={{ display: 'flex', gap: 6 }}>
            <Button variant="primary" size="sm" icon="plus" loading={newLoading} onClick={handleNewTopic}>新話題</Button>
            <Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={loadTopics}>重新整理</Button>
          </div>
        }>
        <Table columns={[
          { label: '話題名稱' },
          { label: 'ID', width: 160 },
          { label: '狀態', width: 88, center: true },
          { label: '訊息數', width: 72, center: true },
          { label: '建立時間', width: 130 },
          { label: '操作', width: 210 },
        ]}>
          {loading
            ? <LoadingRow cols={6} />
            : topics.length === 0
              ? <tr><td colSpan={6} style={{ padding: '36px 16px', textAlign: 'center', color: 'var(--c-fg2)', fontSize: 13 }}>
                    尚無話題
                    <div style={{ fontSize: 11, color: 'var(--c-fg3)', marginTop: 4 }}>使用者發送第一則訊息後自動建立，或點「新話題」手動建立</div>
                  </td></tr>
              : topics.map(t => (
                  <TR key={t.topic_id} selected={t.is_active}>
                    <TD>
                      <span style={{ fontSize: 13, fontWeight: t.is_active ? 600 : 400 }}>{t.title}</span>
                    </TD>
                    <TD mono muted style={{ fontSize: 11 }}>{t.topic_id}</TD>
                    <TD center>
                      {t.is_active ? <Badge color="var(--c-accent)">使用中</Badge> : <span style={{ fontSize: 12, color: 'var(--c-fg3)' }}>—</span>}
                    </TD>
                    <TD mono center>{t.msg_count}</TD>
                    <TD mono muted style={{ fontSize: 11 }}>{fmtTimeShort(t.created_at)}</TD>
                    <TD>
                      <div style={{ display: 'flex', gap: 4 }}>
                        {!t.is_active && (
                          <Button variant="ghost" size="sm" icon="arrowRight"
                            loading={swLoading[t.topic_id]}
                            onClick={() => handleSwitch(t.topic_id, t.title)}>切換</Button>
                        )}
                        <Button variant="ghost" size="sm" icon="edit"
                          onClick={() => openRename(t)}>改名</Button>
                        <Button variant="danger" size="sm" icon="trash"
                          loading={delLoading[t.topic_id]}
                          onClick={() => handleDelete(t.topic_id, t.title, t.msg_count)}>刪除</Button>
                      </div>
                    </TD>
                  </TR>
                ))
          }
        </Table>
      </Panel>

      {renameTarget && (
        <Modal title="話題改名" onClose={() => setRenameTarget(null)} width={420}>
          <div style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ fontSize: 12, color: 'var(--c-fg2)' }}>
              話題 <span className="mono" style={{ color: 'var(--c-fg)' }}>{renameTarget.topic_id}</span>
            </div>
            <Input value={renameValue} placeholder="新的話題標題" autoFocus
              onChange={setRenameValue} onKeyDown={e => e.key === 'Enter' && submitRename()} />
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
              <Button variant="ghost" size="sm" onClick={() => setRenameTarget(null)}>取消</Button>
              <Button variant="primary" size="sm" loading={renameLoading}
                disabled={!renameValue.trim()} onClick={submitRename}>儲存</Button>
            </div>
          </div>
        </Modal>
      )}
    </ContentArea>
  );
}

window.PageC = PageC;
