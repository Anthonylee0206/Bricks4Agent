const { useState, useEffect, useCallback } = React;

const C_TABS = [
  { id: 'overview',  label: '概覽',     icon: 'user'     },
  { id: 'messages',  label: '訊息',     icon: 'message'  },
  { id: 'turns', label: '輪次', icon: 'list' },
  { id: 'topics',    label: '聊天室',   icon: 'list'     },
  { id: 'facts',     label: '筆記',     icon: 'database' },
  { id: 'summaries', label: '摘要',     icon: 'archive'  },
  { id: 'audit',     label: '稽核',     icon: 'log'      },
];

function PageC({ userId, onNavigate, params = new URLSearchParams() }) {
  const [tab, setTab]           = useState(C_TABS.some(t => t.id === params.get('tab') && (t.id !== 'audit' || nyaCanRead('audit'))) ? params.get('tab') : 'overview');
  const [overview, setOverview] = useState(null);
  const [ovLoading, setOvLoad]  = useState(false);
  const [ovError, setOvError]   = useState(false);

  const loadOverview = useCallback(async () => {
    setOvLoad(true);
    setOvError(false);
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/overview`);
    if (res.success) {
      setOverview(res.data);
    } else {
      setOvError(true);
    }
    setOvLoad(false);
  }, [userId]);

  useEffect(() => { loadOverview(); }, [loadOverview]);
  useEffect(() => { setTab(C_TABS.some(t => t.id === params.get('tab') && (t.id !== 'audit' || nyaCanRead('audit'))) ? params.get('tab') : 'overview'); }, [params.toString(), nyaCanRead('audit')]);
  function changeTab(next) { onNavigate('user/' + encodeURIComponent(userId) + '?tab=' + next + (params.get('topic') ? '&topic=' + encodeURIComponent(params.get('topic')) : '') + (params.get('trace') ? '&trace=' + encodeURIComponent(params.get('trace')) : '')); }
  function refreshAll() { loadOverview(); loadTopicCount(); }

  const [topicCount, setTopicCount] = useState(null);

  const loadTopicCount = useCallback(async () => {
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/topics`);
    if (res.success) setTopicCount(res.data.total);
  }, [userId]);

  useEffect(() => { loadTopicCount(); }, [loadTopicCount]);

  const tabsWithBadge = C_TABS.filter(t => t.id !== 'audit' || nyaCanRead('audit')).map(t => {
    let badge;
    if (overview) {
      if (t.id === 'messages')  badge = overview.messages.total - overview.messages.deleted;
      if (t.id === 'facts')     badge = overview.notes.total_active;
      if (t.id === 'summaries') badge = overview.summaries.total_versions;
    }
    if (t.id === 'topics' && topicCount != null) badge = topicCount;
    return { ...t, badge };
  });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
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
          {overview && <Badge>Soul：{overview.effective_soul_id}</Badge>}
        </div>
      </div>

      <TabBar tabs={tabsWithBadge} active={tab} onChange={changeTab} />

      <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {tab === 'overview'  && <TabC1 userId={userId} overview={overview} loading={ovLoading} error={ovError} onRefresh={refreshAll} />}
        {tab === 'messages'  && <TabC2 userId={userId} initialTopic={params.get('topic') || ''} onTopicChange={topic => onNavigate('user/' + encodeURIComponent(userId) + '?tab=messages' + (topic ? '&topic=' + encodeURIComponent(topic) : ''))} onChanged={refreshAll} />}
        {tab === 'topics'    && <TabCTopics userId={userId} onChanged={() => { loadOverview(); loadTopicCount(); }} />}
        {tab === 'turns' && <TabCTurns userId={userId} />}
        {tab === 'facts'     && <TabC4 userId={userId} onChanged={loadOverview} />}
        {tab === 'summaries' && <TabC5 userId={userId} onChanged={loadOverview} />}
        {tab === 'audit' && nyaCanRead('audit') && <TabC6 key={params.get('trace') || ''} userId={userId} initialTrace={params.get('trace') || ''} />}
      </div>
    </div>
  );
}

function TabC1({ userId, overview, loading, error, onRefresh }) {
  const [nukeLoading, setNuke] = useState(false);

  async function handleNuke() {
    if (!overview) return;
    const { messages, notes, summaries } = overview;
    const ok = await showConfirm(
      `清除全部記憶 — ${userId}？`,
      `將軟刪除 ${messages.active + messages.summarized} 筆訊息、硬刪除所有筆記及歷史版本（含已過期）、刪除 ${summaries.total_versions} 個摘要，並清除聊天室與遺忘紀錄（Soul 設定保留）。此操作不可還原。`
    );
    if (!ok) return;
    setNuke(true);
    const res = await apiCall('DELETE', `users/${encodeURIComponent(userId)}`, { confirm: 'all' });
    if (res.success) {
      showToast(`已清除全部記憶：訊息 ${res.data.soft_deleted_messages}、筆記版本 ${res.data.deleted_note_versions}、摘要 ${res.data.deleted_summaries}、聊天室 ${res.data.deleted_topics}`, 'success');
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

  const { messages: m, notes: f, summaries: s } = overview;
  const catEntries = Object.entries(f.categories || {});
  const maxCat = Math.max(1, ...catEntries.map(([, c]) => c));

  return (
    <ContentArea>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: -4, flexShrink: 0 }}>
        <Button variant="secondary" size="sm" icon="refresh" loading={loading} onClick={onRefresh}>重新整理</Button>
      </div>

      <StatTiles cols={4} items={[
        { label: '目前聊天室', value: overview.active_topic?.title || overview.active_topic?.topic_id || '尚未建立' },
        { label: '目前聊天室的近期訊息預覽', value: m.recent_history, sub: `上限 ${overview.short_term_message_limit} 則`, accent: 'var(--c-accent-fg)' },
        { label: '未過期筆記', value: f.total_active },
        { label: '遺忘紀錄', value: overview.forgotten_count },
      ]} />


      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 14 }}>
        <Panel title="筆記" subtitle={`${f.total_active} 筆未過期 · 跨聊天室共用`}>
          {catEntries.length === 0
            ? <EmptyState message="尚無有效筆記" />
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

        <Panel title="摘要歷史資料" subtitle={overview.auto_summarize_enabled === false ? '自動摘要已關閉 · 現有摘要不會用於聊天' : '依話題整理'}>
          <KVGrid cols={1} items={[
            { label: '有摘要的話題', value: s.active_count ?? 0, mono: true },
            { label: '摘要版本總數', value: s.total_versions, mono: true },
            { label: '歷史最大涵蓋 Seq（非當前上下文進度）', value: s.latest_covered_to_seq || '—', mono: true },
          ]} />
        </Panel>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 14 }}>
        <Panel title="時間資訊">
          <KVGrid cols={1} items={[
            { label: '最後訊息時間', value: fmtTime(overview.last_message_at), mono: true },
            { label: '最後筆記更新（含已過期現行版本）', value: fmtTime(overview.last_note_updated_at), mono: true },
          ]} />
        </Panel>
        <SoulBindPanel userId={userId} availableSouls={overview.available_souls || []} currentSoulId={overview.bound_soul_id} effectiveSoulId={overview.effective_soul_id} soulReason={overview.soul_reason} onSoulChanged={onRefresh} />
      </div>

      {overview.last_turn && <Panel title="最後一輪保存的注入內容" subtitle={fmtTime(overview.last_turn.created_at)}>
        <TurnContext raw={overview.last_turn.detail_json} traceId={overview.last_turn.trace_id} />
      </Panel>}
      <DangerZone>
        <DangerAction
          label="清除全部記憶"
          description="軟刪除所有訊息，硬刪除所有筆記、摘要、話題與遺忘紀錄（Soul 設定保留）"
          buttonLabel="清除全部記憶"
          loading={nukeLoading}
          onConfirm={handleNuke}
        />
      </DangerZone>
    </ContentArea>
  );
}

function TabC2({ userId, onChanged, initialTopic = '', onTopicChange }) {
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
  const [topicFilter, setTopicFilter] = useState(initialTopic);
  const [external, setExternal] = useState([]);

  async function loadMessages(lim, off, topic) {
    const t = topic !== undefined ? topic : topicFilter;
    setLoading(true);
    const params = { limit: lim, offset: off };
    if (t === '__legacy__') params.unassigned = true;
    else if (t) params.topic_id = t;
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/messages`, params);
    if (res.success) { setMessages(res.data.messages); setTotal(res.data.total); setExternal(res.data.external_conversations || []); }
    setLoading(false);
    setSelected(new Set());
  }

  async function loadTopics() {
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/topics`);
    if (res.success) setTopics(res.data.topics);
  }

  useEffect(() => { setTopicFilter(initialTopic); setOffset(0); loadTopics(); loadMessages(limit, 0, initialTopic); }, [userId, initialTopic]);

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
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/messages/${encodeURIComponent(msg.message_id)}`);
    setDetail(res.success ? res.data : null);
    setDLoad(false);
  }

  async function handleDeleteSelected() {
    const ids = [...selected];
    const ok = await showConfirm(`軟刪除選取的 ${ids.length} 筆訊息？`, '選取的訊息將被軟刪除，可透過資料庫還原。');
    if (!ok) return;
    setSelDel(true);
    const res = await apiCall('POST', `users/${encodeURIComponent(userId)}/messages/soft-delete`, { message_ids: ids });
    if (res.success) {
      showToast(`已軟刪除 ${res.data.soft_deleted} 筆訊息`, 'success');
      loadMessages(limit, offset);
      onChanged && onChanged();
    }
    setSelDel(false);
  }

  async function handleDeleteAll() {
    const ov = await apiCall('GET', `users/${encodeURIComponent(userId)}/overview`);
    if (!ov.success) return;
    const count = ov.data.messages.active + ov.data.messages.summarized;
    if (count === 0) { showToast('沒有可刪除的訊息', 'info'); return; }
    const ok = await showConfirm('軟刪除全部訊息？', `將軟刪除此用戶所有話題的全部 ${count} 筆訊息。`);
    if (!ok) return;
    setDelLoad(true);
    const res = await apiCall('DELETE', `users/${encodeURIComponent(userId)}/messages`, { confirm: 'all' });
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
          options={[{ value: '', label: '全部儲存訊息' },
            ...topics.map(t => ({ value: t.topic_id, group: '使用者聊天室', label: (t.is_active ? '★ 目前聊天室：' : '') + (t.title || t.topic_id) })),
            ...external.map(id => ({ value: id, group: '外部隔離會話', label: id })),
            { value: '__legacy__', group: '舊版未歸屬訊息', label: '未歸屬訊息' }]}
          onChange={v => { if (onTopicChange) onTopicChange(v); else { setTopicFilter(v); setOffset(0); loadMessages(limit, 0, v); } }}
          style={{ width: 200 }} />
        <div style={{ flex: 1 }} />
        {selected.size > 0 && (
          <Button write="user" variant="danger" size="sm" loading={selDelLoad} onClick={handleDeleteSelected}>
            軟刪除選取（{selected.size}）
          </Button>
        )}
        <Button write="user" variant="danger" size="sm" loading={delLoading} onClick={handleDeleteAll}>軟刪除全部</Button>
      </Toolbar>


      <div style={{ padding: '8px 14px', fontSize: 12, color: 'var(--c-fg2)' }}>管理員瀏覽儲存訊息；聊天只讀目前聊天室。以下為目前候選診斷，當輪 token 裁切請查 trace；摘要旗標在摘要關閉時不阻止注入。</div>
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
                            {isSum && <Badge>歷史摘要旗標</Badge>}
                            {msg.turn_status === 'failed' && <Badge color="var(--c-danger)">失敗輪：使用者訊息保留</Badge>}
                            {msg.turn_status === 'pending' && <Badge>處理中</Badge>}
                            {(msg.exclusion_reasons || []).map(reason => <Badge key={reason}>{({ forgotten: '受隱藏引用排除', other_conversation: '非目前聊天室', outside_recent_window: '超出近期窗口', context_budget: '上下文預算不足', summarized: '摘要啟用時由摘要接手' })[reason] || reason}</Badge>)}
                          </div>
                        </td>
                        <td className="mono" style={{ padding: '8px 12px', fontSize: 11, color: 'var(--c-fg2)', whiteSpace: 'nowrap' }}>
                          {fmtTimeShort(msg.created_at)}
                        </td>
                        <td style={{ padding: '8px 12px' }}>
                          <Button variant="ghost" size="sm" icon="eye" onClick={() => showDetail(msg)}>全文</Button><MessageTrace metadata={msg.metadata_json} />
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
                  { label: '原始聊天室', value: detailMsg.origin_topic_id || '—', mono: true },
                  { label: '輪次狀態', value: detailMsg.turn_status || '舊資料' },
                  { label: 'Turn ID', value: detailMsg.turn_id || '—', mono: true },
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

function TabCTopics({ userId, onChanged }) {
  const [topics, setTopics]       = useState([]);
  const [loading, setLoading]     = useState(false);
  const [newLoading, setNewLoad]  = useState(false);
  const [swLoading, setSwLoad]    = useState({});
  const [delLoading, setDelLoad]  = useState({});
  const [renameTarget, setRenameTarget] = useState(null);
  const [renameValue, setRenameValue]   = useState('');
  const [renameLoading, setRenameLoad]  = useState(false);

  async function loadTopics() {
    setLoading(true);
    const res = await apiCall('GET', `users/${encodeURIComponent(userId)}/topics`);
    if (res.success) setTopics(res.data.topics);
    setLoading(false);
  }

  useEffect(() => { loadTopics(); }, [userId]);

  async function handleNewTopic() {
    setNewLoad(true);
    const res = await apiCall('POST', `users/${encodeURIComponent(userId)}/topics`);
    if (res.success) {
      showToast(`已建立新話題「${res.data.title}」並設為使用中`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setNewLoad(false);
  }

  async function handleSwitch(topicId, title) {
    const ok = await showConfirm(`切換至話題「${title}」？`, '下一輪起使用此聊天室的近期對話；其他聊天室對話保留，筆記共用，歷史搜尋可跨一般聊天室。');
    if (!ok) return;
    setSwLoad(p => ({ ...p, [topicId]: true }));
    const res = await apiCall('POST', `users/${encodeURIComponent(userId)}/topics/${encodeURIComponent(topicId)}/switch`);
    if (res.success) {
      showToast(`已切換至話題「${res.data.title}」`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setSwLoad(p => ({ ...p, [topicId]: false }));
  }

  async function handleDelete(topicId, title, msgCount) {
    const ok = await showConfirm(`刪除話題「${title}」？`,
      `將軟刪除此話題的 ${msgCount} 則訊息、刪除其摘要並移除話題。若是使用中的話題，會自動改用最近的其他話題。此操作不可還原。`);
    if (!ok) return;
    setDelLoad(p => ({ ...p, [topicId]: true }));
    const res = await apiCall('DELETE', `users/${encodeURIComponent(userId)}/topics/${encodeURIComponent(topicId)}`);
    if (res.success) {
      showToast(`已刪除話題「${title}」`, 'success');
      loadTopics();
      onChanged && onChanged();
    }
    setDelLoad(p => ({ ...p, [topicId]: false }));
  }

  function openRename(t) { setRenameTarget(t); setRenameValue(t.title || ''); }
  async function submitRename() {
    if (!renameTarget || !renameValue.trim()) return;
    setRenameLoad(true);
    const res = await apiCall('POST', `users/${encodeURIComponent(userId)}/topics/${encodeURIComponent(renameTarget.topic_id)}/rename`, { title: renameValue.trim() });
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

      <Panel title="聊天室列表" subtitle={`${topics.length} 個話題`}
        actions={
          <div style={{ display: 'flex', gap: 6 }}>
            <Button write="user" variant="primary" size="sm" icon="plus" loading={newLoading} onClick={handleNewTopic}>新話題</Button>
            <Button variant="ghost" size="sm" icon="refresh" loading={loading} onClick={loadTopics}>重新整理</Button>
          </div>
        }>
        <div style={{ padding: '8px 14px', color: 'var(--c-fg2)', fontSize: 12 }}>新建／切換於下一輪生效，本輪訊息仍屬原聊天室；外部明確 conversation id 優先於目前聊天室。筆記共用，歷史搜尋可跨一般聊天室。</div>
        <Table columns={[
          { label: '聊天室名稱' },
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
                          <Button write="user" variant="ghost" size="sm" icon="arrowRight"
                            loading={swLoading[t.topic_id]}
                            onClick={() => handleSwitch(t.topic_id, t.title)}>切換</Button>
                        )}
                        <Button write="user" variant="ghost" size="sm" icon="edit"
                          onClick={() => openRename(t)}>改名</Button>
                        <Button write="user" variant="danger" size="sm" icon="trash"
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
              <Button write="user" variant="primary" size="sm" loading={renameLoading}
                disabled={!renameValue.trim()} onClick={submitRename}>儲存</Button>
            </div>
          </div>
        </Modal>
      )}
    </ContentArea>
  );
}

window.PageC = PageC;


function TabCTurns({ userId }) {
  const [turns, setTurns] = useState([]);
  const [offset, setOffset] = useState(0);
  const [error, setError] = useState('');
  const [work, setWork] = useState(null);
  async function load(page = 0) {
    const res = await apiCall('GET', 'users/' + encodeURIComponent(userId) + '/turns?limit=50&offset=' + page);
    if (res.success) { setTurns(res.data.turns); setError(''); } else setError(res.message);
  }
  useEffect(() => { setOffset(0); load(); }, [userId]);
  async function inspect(turn) {
    const res = await apiCall('GET', 'users/' + encodeURIComponent(userId) + '/turns/' + encodeURIComponent(turn.turn_id) + '/work');
    if (res.success) setWork({ turn, items: res.data.work }); else setError(res.message);
  }
  return <ContentArea><Panel title="輪次狀態" subtitle="失敗輪保留使用者訊息；單一 broker 實例依使用者排隊">
    {error && <div role="alert">{error}</div>}
    {turns.map(turn => <div key={turn.turn_id} style={{ padding: 14, borderBottom: '1px solid var(--c-border)' }}>
      <div>{turn.turn_id} · {({ pending: '處理中', completed: '完成', failed: '失敗' })[turn.status]} · {turn.failure_reason || ''}</div>
      <div>{turn.scope} · 原始聊天室 {turn.start_topic_id} → 最後聊天室 {turn.current_topic_id}</div>
      <div>{fmtTime(turn.created_at)} · 結束 {fmtTime(turn.finished_at)}</div>
      <Button size="sm" onClick={() => inspect(turn)}>已完成效果與工作紀錄</Button>
    </div>)}
    <Button disabled={offset === 0} onClick={() => { const page = Math.max(0, offset - 50); setOffset(page); load(page); }}>上一頁</Button>
    <Button disabled={turns.length < 50} onClick={() => { const page = offset + 50; setOffset(page); load(page); }}>下一頁</Button>
    {work && <Modal title="輪次原始工作紀錄" onClose={() => setWork(null)}><JsonBlock raw={JSON.stringify(work, null, 2)} /></Modal>}
  </Panel></ContentArea>;
}
