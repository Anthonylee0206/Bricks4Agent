const { useState, useEffect } = React;

function channelOf(userId) {
  const i = userId.indexOf(':');
  return i > 0 ? userId.slice(0, i) : 'line';
}

const CHANNEL_LABELS = { line: 'LINE', discord: 'Discord', web: 'Web' };

function PageB({ onNavigate, initialKeyword = '' }) {
  const [users, setUsers]     = useState([]);
  const [total, setTotal]     = useState(0);
  const [loading, setLoading] = useState(false);
  const [input, setInput]     = useState(initialKeyword);
  const [keyword, setKeyword] = useState(initialKeyword);
  const [limit, setLimit]     = useState(50);
  const [offset, setOffset]   = useState(0);

  async function load(kw, lim, off) {
    setLoading(true);
    const res = await apiCall('GET', 'users', { keyword: kw || undefined, limit: lim, offset: off });
    if (res.success) { setUsers(res.data.users); setTotal(res.data.total); }
    setLoading(false);
  }

  useEffect(() => { setInput(initialKeyword); setKeyword(initialKeyword); setOffset(0); load(initialKeyword, limit, 0); }, [initialKeyword]);

  function doSearch() {
    const kw = input.trim();
    setOffset(0);
    setKeyword(kw);
    load(kw, limit, 0);
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, overflow: 'hidden' }}>
      <PageHeader title="使用者" count={total} desc="有訊息、筆記、聊天室、Soul 綁定或遺忘紀錄的使用者；點選一列查看訊息、話題、筆記與摘要">
        <Button variant="secondary" size="sm" icon="refresh" loading={loading}
          onClick={() => load(keyword, limit, offset)}>重新整理</Button>
      </PageHeader>

      <Toolbar>
        <Input
          value={input}
          placeholder="搜尋使用者 ID"
          leftIcon="search" mono
          style={{ width: 320 }}
          onChange={setInput}
          onKeyDown={e => e.key === 'Enter' && doSearch()}
        />
        <Button variant="secondary" size="sm" onClick={doSearch} loading={loading}>搜尋</Button>
        {keyword && (
          <Button variant="ghost" size="sm" icon="x" onClick={() => { setInput(''); setKeyword(''); setOffset(0); load('', limit, 0); }}>
            清除條件
          </Button>
        )}
      </Toolbar>

      <div style={{ flex: 1, overflow: 'auto', background: 'var(--c-surface)' }}>
        <Table columns={[
          { label: '使用者 ID' },
          { label: '通道',     width: 100 },
          { label: '訊息',     width: 90, right: true },
          { label: '未過期筆記', width: 90, right: true },
          { label: '最後訊息', width: 160 },
          { label: 'Soul',     width: 140 },
          { label: '',         width: 40 },
        ]}>
          {loading && users.length === 0
            ? <LoadingRow cols={7} />
            : users.length === 0
              ? <EmptyRow cols={7} message={keyword ? `找不到符合「${keyword}」的使用者` : '還沒有使用者'} />
              : users.map(u => <UserRow key={u.user_id} user={u} onNavigate={onNavigate} />)
          }
        </Table>
      </div>

      <Pagination
        total={total} limit={limit} offset={offset}
        onPage={off => { setOffset(off); load(keyword, limit, off); }}
        onLimitChange={lim => { setLimit(lim); setOffset(0); load(keyword, lim, 0); }}
      />
    </div>
  );
}

function UserRow({ user, onNavigate }) {
  const ch = channelOf(user.user_id);
  return (
    <TR onClick={() => onNavigate('user/' + encodeURIComponent(user.user_id))}>
      <TD mono>
        <span style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 360 }}>
          {user.user_id}
        </span>
      </TD>
      <TD muted>{CHANNEL_LABELS[ch] || ch}</TD>
      <TD right>{user.message_count}</TD>
      <TD right>
        {user.active_note_count > 0 ? user.active_note_count : <span style={{ color: 'var(--c-fg4)' }}>0</span>}
      </TD>
      <TD muted title={fmtTime(user.last_message_at)}>{fmtRelative(user.last_message_at)}</TD>
      <TD muted title={SOUL_REASON_LABELS[user.soul_reason] || user.soul_reason}>綁定：{user.bound_soul_id ?? '未綁定'}<br />生效：{user.effective_soul_id}</TD>
      <TD><Icon name="chevronRight" size={15} color="var(--c-fg3)" /></TD>
    </TR>
  );
}

Object.assign(window, { PageB, channelOf, CHANNEL_LABELS });
