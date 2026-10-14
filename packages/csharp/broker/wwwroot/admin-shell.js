(() => {
  const NYA = '/NYAChat/index.html';
  const LA = '/line-admin.html';

  const MENU = [
    { id: 'broker', label: 'Broker', groups: [
      { label: '總覽', items: [
        { label: 'Broker 總覽', href: '/index.html', icon: 'home' },
      ] },
      { label: '管理', items: [
        { label: 'LINE 與使用者', href: LA + '#line', icon: 'users', isDefault: true, keys: 'line 使用者 註冊 權限 tier 會員' },
        { label: '系統監控', href: LA + '#monitoring', icon: 'activity', keys: '監控 健康 狀態' },
        { label: 'Workflow', href: LA + '#workflow', icon: 'list', keys: '工作流程 任務' },
        { label: 'Browser 綁定', href: LA + '#browser', icon: 'link', keys: '瀏覽器 租約' },
        { label: 'Deployment', href: LA + '#deployment', icon: 'upload', keys: '部署' },
        { label: '交付記錄', href: LA + '#delivery', icon: 'archive', keys: '交付 drive 通知' },
        { label: '權限管理', href: LA + '#permissions', icon: 'shield', keys: 'operator 管理員 帳號 密碼' },
        { label: '審批', href: LA + '#approvals', icon: 'check', keys: '核准 approval' },
        { label: '系統警示', href: LA + '#alerts', icon: 'alert', keys: '警示 alert' },
        { label: 'Tool Specs', href: LA + '#tools', icon: 'code', keys: '工具規格' },
      ] },
    ] },
    { id: 'nyachat', label: 'NyaChat', groups: [
      { label: null, items: [
        { label: '總覽', href: NYA + '#home', icon: 'home', isDefault: true, keys: '首頁 狀態' },
      ] },
      { label: '營運', items: [
        { label: '使用者', href: NYA + '#users', icon: 'users', alias: ['user'], keys: '記憶 訊息 筆記 原話 到期 遺忘 聊天室 摘要 歷史搜尋' },
        { label: '稽核日誌', href: NYA + '#audit', icon: 'log', keys: '錯誤 紀錄 log' },
      ] },
      { label: '設定', items: [
        { label: '對話設定', href: NYA + '#settings', icon: 'list', keys: '對話 記憶 上下文 token 預算' },
        { label: 'Soul 人格', href: NYA + '#souls', icon: 'user', keys: 'soul 語氣 規則 prompt' },
        { label: '工具', href: NYA + '#tools', icon: 'zap', keys: '工具 搜尋 交通 開關 search_history 舊對話搜尋' },
        { label: 'LLM 模型', href: NYA + '#llm', icon: 'cpu', keys: '模型 金鑰 api key provider 路由' },
      ] },
    ] },
  ];

  const ICONS = {
    home: 'M3 10.5 12 3l9 7.5V21H3ZM9 21v-6h6v6',
    grid: 'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',
    activity: 'M22 12h-4l-3 9L9 3l-3 9H2',
    users: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75',
    user: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z',
    list: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
    link: 'M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71',
    upload: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12',
    archive: 'M21 8v13H3V8M1 3h22v5H1zM10 12h4',
    shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z',
    check: 'M20 6 9 17 4 12',
    alert: 'M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0ZM12 9v4M12 17h.01',
    code: 'M16 18 22 12 16 6M8 6 2 12 8 18',
    log: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M16 13H8M16 17H8M10 9H8',
    search: 'M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16ZM21 21l-4.3-4.3',
    zap: 'M13 2 3 14h9l-1 8 10-12h-9l1-8Z',
    cpu: 'M9 9h6v6H9zM4 6V4h16v2M4 18v2h16v-2M2 9h2v6H2zM20 9h2v6h-2zM9 2v2M15 2v2M9 20v2M15 20v2',
  };

  const el = (tag, cls, text) => {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  };

  const icon = name => {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('width', '15'); svg.setAttribute('height', '15'); svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor'); svg.setAttribute('stroke-width', '1.5');
    svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round');
    for (const seg of (ICONS[name] || '').split('M').filter(Boolean)) {
      const p = document.createElementNS(ns, 'path');
      p.setAttribute('d', 'M' + seg);
      svg.append(p);
    }
    return svg;
  };

  const normPath = p => {
    const s = p.toLowerCase();
    return s.endsWith('/') ? s + 'index.html' : s;
  };
  const splitHref = href => {
    const i = href.indexOf('#');
    return { path: normPath(i < 0 ? href : href.slice(0, i)), hash: i < 0 ? '' : href.slice(i + 1) };
  };
  const hashBase = h => h.replace(/^#/, '').split(/[?/]/)[0];

  const allItems = [];
  for (const cat of MENU) for (const g of cat.groups) for (const it of g.items) {
    Object.assign(it, splitHref(it.href), { cat });
    allItems.push(it);
  }

  function currentItem() {
    const path = normPath(location.pathname);
    const base = hashBase(location.hash);
    const onPage = allItems.filter(it => it.path === path);
    return onPage.find(it => it.hash && (it.hash === base || (it.alias || []).includes(base)))
      || (base ? null : onPage.find(it => it.isDefault || !it.hash))
      || null;
  }

  const pageCat = (currentItem() || allItems.find(it => it.path === normPath(location.pathname)) || allItems[0]).cat;
  let shownCat = pageCat;

  const app = el('div', 'b4a-app');
  const top = el('header', 'b4a-top');
  const brand = el('a', 'b4a-brand', 'B4A');
  brand.href = '/index.html';
  const cats = el('nav', 'b4a-cats');
  cats.setAttribute('aria-label', '大類');
  const catButtons = MENU.map(cat => {
    const b = el('button', 'b4a-cat', cat.label);
    b.type = 'button';
    b.addEventListener('click', () => { shownCat = cat; renderMenu(); });
    cats.append(b);
    return { cat, b };
  });

  const jump = el('div', 'b4a-jump');
  const jumpInput = el('input');
  jumpInput.placeholder = '跳到功能…';
  jumpInput.setAttribute('aria-label', '快速跳轉');
  const jumpKbd = el('kbd', null, '/');
  const jumpList = el('div', 'b4a-jump-list');
  jumpList.hidden = true;
  jump.append(jumpInput, jumpKbd, jumpList);

  const conn = el('span', 'b4a-conn', '連線中…');
  conn.dataset.state = 'checking';
  top.append(brand, cats, jump, el('div', 'b4a-spacer'), conn);

  const side = el('aside', 'b4a-side');
  const menu = el('nav', 'b4a-menu');
  menu.setAttribute('aria-label', '功能選單');
  const account = el('div', 'b4a-account');
  const accountName = el('span', 'b4a-account-name', '…');
  const logout = el('button', null, '登出');
  logout.type = 'button';
  logout.hidden = true;
  account.append(accountName, logout);
  side.append(menu, account);

  const main = el('main', 'b4a-main');
  app.append(top, side, main);

  const itemNodes = new Map();
  const hiddenHrefs = new Set();

  function renderMenu() {
    for (const { cat, b } of catButtons) b.setAttribute('aria-current', String(cat === shownCat));
    menu.replaceChildren();
    itemNodes.clear();
    const cur = currentItem();
    for (const g of shownCat.groups) {
      const group = el('div', 'b4a-group');
      if (g.label) group.append(el('div', 'b4a-group-label', g.label));
      for (const it of g.items) {
        const a = el('a', 'b4a-item');
        a.href = it.href;
        a.append(icon(it.icon), document.createTextNode(it.label));
        if (it === cur) a.setAttribute('aria-current', 'page');
        a.hidden = hiddenHrefs.has(it.href);
        itemNodes.set(it.href, a);
        group.append(a);
      }
      menu.append(group);
    }
  }

  let providers = [];
  let jumpItems = [];
  let jumpSel = 0;

  function renderJump() {
    const q = jumpInput.value.trim().toLowerCase();
    const extra = q ? providers.flatMap(fn => { try { return fn(jumpInput.value.trim()) || []; } catch { return []; } }) : [];
    const matches = allItems
      .filter(it => !hiddenHrefs.has(it.href))
      .filter(it => !q || it.label.toLowerCase().includes(q) || (it.keys || '').toLowerCase().includes(q) || it.cat.label.toLowerCase().includes(q))
      .map(it => ({ label: it.label, href: it.href, hint: it.cat.label }));
    jumpItems = extra.concat(matches).slice(0, 12);
    jumpSel = Math.min(jumpSel, Math.max(jumpItems.length - 1, 0));
    jumpList.replaceChildren();
    if (!jumpItems.length) jumpList.append(el('div', 'b4a-jump-empty', '沒有符合的功能'));
    jumpItems.forEach((it, i) => {
      const b = el('button');
      b.type = 'button';
      b.setAttribute('aria-selected', String(i === jumpSel));
      b.append(document.createTextNode(it.label));
      if (it.hint) b.append(el('span', 'b4a-jump-cat', it.hint));
      b.addEventListener('mousedown', e => e.preventDefault());
      b.addEventListener('click', () => go(it));
      jumpList.append(b);
    });
    jumpList.hidden = false;
  }

  function go(it) {
    jumpInput.value = '';
    jumpList.hidden = true;
    jumpInput.blur();
    if (it) location.href = it.href;
  }

  jumpInput.addEventListener('focus', () => { jumpSel = 0; renderJump(); });
  jumpInput.addEventListener('input', () => { jumpSel = 0; renderJump(); });
  jumpInput.addEventListener('blur', () => { jumpList.hidden = true; });
  jumpInput.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); jumpSel = Math.min(jumpSel + 1, jumpItems.length - 1); renderJump(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); jumpSel = Math.max(jumpSel - 1, 0); renderJump(); }
    else if (e.key === 'Enter') { e.preventDefault(); go(jumpItems[jumpSel]); }
    else if (e.key === 'Escape') { jumpInput.value = ''; jumpInput.blur(); }
  });
  document.addEventListener('keydown', e => {
    const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || '') || document.activeElement?.isContentEditable;
    if (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); jumpInput.focus(); }
  });

  let fails = 0;
  async function heartbeat() {
    try {
      const ctl = new AbortController();
      const tid = setTimeout(() => ctl.abort(), 4000);
      const res = await fetch('/api/v1/local-admin/status', { credentials: 'include', cache: 'no-store', signal: ctl.signal });
      clearTimeout(tid);
      if (!res.ok) throw new Error('HTTP ' + res.status);
      fails = 0;
      conn.dataset.state = 'up';
      conn.textContent = '已連線';
      const data = (await res.json()).data || {};
      if (normPath(location.pathname) === normPath(NYA)) {
        const previous = JSON.stringify(window.__nyaAuth || {});
        window.__nyaAuth = data;
        if (previous !== JSON.stringify(data)) window.dispatchEvent(new Event('nya-auth-changed'));
      }
      accountName.textContent = data.authenticated ? (data.username || 'admin') : '未登入';
      logout.hidden = !data.authenticated;
    } catch {
      if (++fails >= 2) { conn.dataset.state = 'down'; conn.textContent = '已斷線'; }
    }
  }

  logout.addEventListener('click', async () => {
    try {
      await fetch('/api/v1/local-admin/logout', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    } catch { }
    location.href = LA;
  });

  function mount() {
    for (const node of Array.from(document.body.childNodes)) {
      if (node.nodeType === 1 && node.tagName === 'SCRIPT') continue;
      main.append(node);
    }
    document.body.prepend(app);
    renderMenu();
    heartbeat();
    setInterval(heartbeat, 10000);
  }

  window.addEventListener('hashchange', () => {
    const cur = currentItem();
    if (cur) shownCat = cur.cat;
    renderMenu();
  });

  window.B4AShell = {
    sync: renderMenu,
    setVisible(href, visible) {
      if (visible) hiddenHrefs.delete(href); else hiddenHrefs.add(href);
      const n = itemNodes.get(href);
      if (n) n.hidden = !visible;
    },
    registerJump(fn) { providers.push(fn); },
  };

  if (document.body) mount();
  else document.addEventListener('DOMContentLoaded', mount, { once: true });
})();
