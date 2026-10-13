/* NyaChat Admin — App（內容區 + hash router；頂欄與左側選單由 /admin-shell.js 提供） */
const { useState, useEffect } = React;

const PAGES = ['home', 'users', 'audit', 'souls', 'tools', 'llm'];

function parseHash(hash) {
  const raw = hash.replace(/^#/, '') || 'home';
  if (raw.startsWith('user/')) return { page: 'user', userId: decodeURIComponent(raw.slice(5)) };
  const [page, qs] = raw.split('?');
  return { page: PAGES.includes(page) ? page : 'home', params: new URLSearchParams(qs || '') };
}

function looksLikeUserId(q) {
  return /^U[0-9a-f]{32}$/i.test(q) || /^[a-z]+:\S+$/i.test(q);
}

if (window.B4AShell) {
  window.B4AShell.registerJump(q => {
    const items = [];
    if (looksLikeUserId(q)) items.push({ label: `開啟使用者 ${q}`, href: '/NYAChat/index.html#user/' + encodeURIComponent(q), hint: 'NyaChat' });
    items.push({ label: `在 NyaChat 使用者中搜尋「${q}」`, href: '/NYAChat/index.html#users?q=' + encodeURIComponent(q), hint: 'NyaChat' });
    return items;
  });
}

function App() {
  const [route, setRoute]   = useState(() => parseHash(window.location.hash));
  const [status, setStatus] = useState(null);

  useEffect(() => {
    const onHash = () => setRoute(parseHash(window.location.hash));
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  useEffect(() => {
    apiCall('GET', 'status').then(res => { if (res.success) setStatus(res.data); });
  }, []);

  function navigate(target) {
    window.location.hash = '#' + target;
  }

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden', minWidth: 0 }}>
      {route.page === 'home'  && <PageHome onNavigate={navigate} status={status} />}
      {route.page === 'users' && <PageB onNavigate={navigate} initialKeyword={route.params.get('q') || ''} />}
      {route.page === 'user'  && <PageC key={route.userId} userId={route.userId} onNavigate={navigate} />}
      {route.page === 'audit' && <PageD key={route.params.toString()} onNavigate={navigate} initialResult={route.params.get('result') || ''} />}
      {route.page === 'souls' && <PageE />}
      {route.page === 'tools' && <PageF />}
      {route.page === 'llm'   && <PageG />}
      <ToastContainer />
      <ConfirmDialog />
    </div>
  );
}

(window.__nyaReady || Promise.resolve()).then(() => {
  ReactDOM.createRoot(document.getElementById('root')).render(<App />);
});
