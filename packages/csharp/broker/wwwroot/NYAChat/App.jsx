const { useState, useEffect } = React;

const PAGES = ['home', 'users', 'audit', 'souls', 'tools', 'llm', 'settings'];

function parseHash(hash) {
  const raw = hash.replace(/^#/, '') || 'home';
  const split = raw.indexOf('?');
  const path = split < 0 ? raw : raw.slice(0, split);
  const params = new URLSearchParams(split < 0 ? '' : raw.slice(split + 1));
  if (path.startsWith('user/') && path.length > 5) {
    try { return { page: 'user', userId: decodeURIComponent(path.slice(5)), params }; } catch { }
  }
  return { page: PAGES.includes(path) ? path : 'home', params, invalid: !PAGES.includes(path), legacyRoles: path === 'roles' };
}

function looksLikeUserId(q) {
  return /^U[0-9a-f]{32}$/i.test(q) || /^[a-z]+:\S+$/i.test(q);
}

if (window.B4AShell) {
  window.B4AShell.registerJump(q => {
    const items = [];
    if (nyaCanRead('users')) {
      if (looksLikeUserId(q)) items.push({ label: `開啟使用者 ${q}`, href: '/NYAChat/index.html#user/' + encodeURIComponent(q), hint: 'NyaChat' });
      items.push({ label: `在 NyaChat 使用者中搜尋「${q}」`, href: '/NYAChat/index.html#users?q=' + encodeURIComponent(q), hint: 'NyaChat' });
    }
    return items;
  });
}

function App() {
  const [route, setRoute]   = useState(() => {
    const next = parseHash(window.location.hash);
    if (nyaCanRead(next.page)) return next;
    const landing = PAGES.find(p => nyaCanRead(p));
    return landing ? parseHash('#' + landing) : { page: 'denied', params: new URLSearchParams() };
  });
  const [status, setStatus] = useState(null);

  useEffect(() => {
    const onHash = () => {
      const next = parseHash(window.location.hash);
      if (next.legacyRoles) showToast('角色管理已交回 Broker「LINE 與使用者」與「權限管理」頁。', 'info');
      if (next.invalid) history.replaceState(null, '', '#home');
      if (!nyaCanRead(next.page)) {
        const landing = PAGES.find(p => nyaCanRead(p));
        if (landing) { history.replaceState(null, '', '#' + landing); setRoute(parseHash('#' + landing)); }
        else setRoute({ page: 'denied', params: new URLSearchParams() });
      } else setRoute(next);
      PAGES.forEach(p => window.B4AShell?.setVisible('/NYAChat/index.html#' + p, nyaCanRead(p)));
      window.B4AShell?.sync();
    };
    onHash();
    window.addEventListener('hashchange', onHash);
    window.addEventListener('nya-auth-changed', onHash);
    return () => {
      window.removeEventListener('hashchange', onHash);
      window.removeEventListener('nya-auth-changed', onHash);
    };
  }, []);

  async function refreshStatus() {
    const res = await apiCall('GET', 'status');
    if (res.success) setStatus(res.data);
  }

  function navigate(target) {
    window.location.hash = '#' + target;
  }

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden', minWidth: 0 }}>
      {route.page === 'home'  && <PageHome onNavigate={navigate} status={status} onRefreshStatus={refreshStatus} />}
      {route.page === 'users' && <PageB onNavigate={navigate} initialKeyword={route.params.get('q') || ''} />}
      {route.page === 'user'  && <PageC key={route.userId} userId={route.userId} onNavigate={navigate} params={route.params} />}
      {route.page === 'audit' && <PageD key={route.params.toString()} onNavigate={navigate} initialResult={route.params.get('result') || ''} initialTrace={route.params.get('trace') || ''} initialKeyword={route.params.get('keyword') || ''} />}
      {route.page === 'souls' && <PageE />}
      {route.page === 'tools' && <PageF />}
      {route.page === 'llm'   && <PageG />}
      {route.page === 'settings' && <PageI />}
      {route.page === 'denied' && <EmptyState message="目前帳號沒有 NyaChat 後台讀取權限" hint="請至 Broker 權限管理調整帳號權限" />}
      <ToastContainer />
      <ConfirmDialog />
    </div>
  );
}

(window.__nyaReady || Promise.resolve()).then(() => {
  ReactDOM.createRoot(document.getElementById('root')).render(<App />);
});
