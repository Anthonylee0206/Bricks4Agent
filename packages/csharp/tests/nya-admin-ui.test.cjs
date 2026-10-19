const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const repo = path.resolve(__dirname, '../../..');
const root = path.join(repo, 'packages/csharp/broker/wwwroot/NYAChat');
const babelContext = { console };
vm.runInNewContext(fs.readFileSync(path.join(repo, 'packages/csharp/broker/wwwroot/vendor/babel-standalone@7.29.0/babel.min.js'), 'utf8'), babelContext);
const Babel = babelContext.Babel;
const jsx = fs.readdirSync(root).filter(n => n.endsWith('.jsx'));
for (const name of jsx) {
  const code = fs.readFileSync(path.join(root, name), 'utf8');
  const ast = Babel.transform(code, { ast: true, code: false, parserOpts: { plugins: ['jsx'] } }).ast;
  assert.equal(ast.comments.length, 0, name + ' must not contain comments');
  Babel.transform(code, { presets: ['react'], comments: false });
}
for (const name of fs.readdirSync(root).filter(n => /\.(css|html)$/.test(n))) {
  const code = fs.readFileSync(path.join(root, name), 'utf8');
  assert.ok(!/<!--|\/\*/.test(code), name + ' must not contain comments');
  if (name.endsWith('.html')) for (const match of code.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)) {
    if (match[1].trim()) assert.equal(Babel.transform(match[1], { ast: true, code: false }).ast.comments.length, 0);
  }
}
const shell = fs.readFileSync(path.join(repo, 'packages/csharp/broker/wwwroot/admin-shell.js'), 'utf8');
assert.equal(Babel.transform(shell, { ast: true, code: false }).ast.comments.length, 0);
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
assert.ok(!fs.existsSync(path.join(root, 'PageH.jsx')));
assert.ok(!/src=["']PageH\.jsx/.test(html));
assert.ok(html.includes("fetch('/api/v1/local-admin/status'"));
assert.ok(!html.includes('/local-admin/nya/status'));
assert.ok(!fs.readFileSync(path.join(root, 'PageI.jsx'), 'utf8').includes('AutoSummarizeEnabled'));
const effects = [];
const events = {};
const notifications = [];
const visible = {};
const render = (type, props, ...children) => ({ type, props: props || {}, children: children.flat(Infinity) });
const context = {
  console, URLSearchParams, setTimeout, clearTimeout,
  React: {
    createElement: render,
    useState: init => [typeof init === 'function' ? init() : init, () => {}],
    useEffect: effect => effects.push(effect), useCallback: fn => fn,
  },
  ReactDOM: { createRoot: () => ({ render() {} }) },
  document: { getElementById() {} },
  location: { hash: '#home' },
  history: { replaceState: (state, title, hash) => { context.location.hash = hash; } },
  showToast: text => notifications.push(text),
  __nyaReady: { then() {} },
  B4AShell: { registerJump() {}, sync() {}, setVisible: (href, allowed) => { visible[href] = allowed; } },
  addEventListener: (name, fn) => { events[name] = fn; }, removeEventListener() {},
};
context.window = context;
vm.createContext(context);
for (const name of ['ui.jsx', 'api.jsx', 'components.jsx', 'PageB.jsx', 'PageHome.jsx', 'PageC_misc.jsx', 'PageC_facts.jsx', 'PageC.jsx', 'PageD.jsx', 'PageE.jsx', 'PageF.jsx', 'PageG.jsx', 'PageI.jsx', 'App.jsx']) {
  const code = Babel.transform(fs.readFileSync(path.join(root, name), 'utf8'), { presets: ['react'], plugins: ['transform-block-scoping'], comments: false }).code;
  vm.runInContext(code, context, { filename: name });
}
const evaluate = code => vm.runInContext(code, context);
context.showToast = text => notifications.push(text);
const route = evaluate("parseHash('#user/' + encodeURIComponent('web:user/中文?value') + '?tab=messages&topic=' + encodeURIComponent('room/中文?x'))");
assert.equal(route.userId, 'web:user/中文?value');
assert.equal(route.params.get('tab'), 'messages');
assert.equal(route.params.get('topic'), 'room/中文?x');
assert.equal(evaluate("parseHash('#unknown').invalid"), true);
assert.equal(evaluate("parseHash('#user/%GG').invalid"), true);
assert.equal(evaluate("parseHash('#roles').legacyRoles"), true);
context.__nyaAuth = { role: 'permission_admin', permissions: ['permission.user.manage'] };
assert.equal(evaluate("nyaCanRead('users')"), true);
assert.equal(evaluate("nyaCanRead('home')"), false);
assert.equal(evaluate("nyaCanRead('audit')"), false);
assert.equal(evaluate("nyaCanWrite('user')"), true);
assert.equal(evaluate("nyaCanWrite('system')"), false);
assert.equal(evaluate("nyaCanWrite('super')"), false);
let target;
const detailPage = context.PageC({ userId: 'web:user/中文', params: new URLSearchParams('tab=messages&topic=room-old'), onNavigate: next => { target = next; } });
function find(tree, type) {
  if (!tree || typeof tree !== 'object') return undefined;
  if (tree.type === type) return tree;
  for (const child of tree.children || []) { const found = find(child, type); if (found) return found; }
}
const messageTab = find(detailPage, context.TabC2);
assert.equal(messageTab.props.initialTopic, 'room-old');
messageTab.props.onTopicChange('room/中文?x');
assert.equal(context.parseHash('#' + target).params.get('topic'), 'room/中文?x');
const unauthorizedAudit = context.PageC({ userId: 'web:user/中文', params: new URLSearchParams('tab=audit'), onNavigate() {} });
assert.ok(!find(unauthorizedAudit, context.TabC6));
effects.length = 0;
const app = context.App();
assert.ok(app.children.some(n => n?.type === context.PageB));
assert.ok(!app.children.some(n => n?.type === context.PageHome));
effects.splice(0).forEach(effect => effect());
assert.equal(context.location.hash, '#users');
assert.equal(visible['/NYAChat/index.html#llm'], false);
assert.equal(visible['/NYAChat/index.html#users'], true);
context.__nyaAuth = { role: 'super_admin', permissions: ['system.status.read', 'permission.user.manage', 'audit.read', 'system.worker.manage'] };
context.location.hash = '#roles';
events.hashchange();
assert.equal(context.location.hash, '#home');
assert.ok(notifications.some(n => n.includes('角色管理已交回 Broker')));
context.__nyaAuth = { role: 'auditor', permissions: ['audit.read', 'system.status.read'] };
assert.equal(context.Button({ write: 'super', children: '刪除' }).props.disabled, true);
assert.equal(context.Button({ write: 'user', children: '遺忘' }).props.disabled, true);
assert.equal(context.Button({ children: '刷新' }).props.disabled, undefined);
context.__nyaAuth = { role: 'super_admin', permissions: ['system.status.read'] };
const protectedDelete = context.Button({ write: 'super', disabled: true, title: '請先切換系統預設 Profile，再刪除' });
assert.equal(protectedDelete.props.disabled, true);
assert.equal(protectedDelete.props.title, '請先切換系統預設 Profile，再刪除');
context.__nyaAuth = { role: 'super_admin', permissions: ['system.status.read', 'audit.read', 'permission.user.manage'] };
for (const tab of ['overview', 'messages', 'turns', 'topics', 'facts', 'summaries', 'audit']) {
  const page = context.PageC({ userId: 'web:render', params: new URLSearchParams('tab=' + tab), onNavigate() {} });
  const types = { overview: 'TabC1', messages: 'TabC2', turns: 'TabCTurns', topics: 'TabCTopics', facts: 'TabC4', summaries: 'TabC5', audit: 'TabC6' };
  const component = evaluate(types[tab]);
  const child = find(page, component);
  assert.ok(child, tab + ' component exists');
  assert.doesNotThrow(() => component(child.props), tab + ' renders');
}
const loadedOverview = { userId: 'web:render', overview: { messages: { total: 2, deleted: 0, recent_history: 2 }, notes: { total_active: 1, categories: { normal: 1 } }, summaries: { total_versions: 1 }, available_souls: [] } };
assert.doesNotThrow(() => context.TabC1(loadedOverview), 'loaded overview renders the new note model');
const detail = context.detailSummary(JSON.stringify({ message: 'hello', tool_name: 'search_history', output: 'preview', from_topic_id: 'room-a', to_topic_id: 'room-b', context: { facts: 1, summaries: 0, history: 2, estimated_tokens: 100, truncated: true } }));
for (const fragment of ['hello', 'search_history', '截斷預覽', 'room-a', 'room-b', '筆記 1', 'history 2', '100 tokens', '已截斷']) assert.ok(detail.includes(fragment));
const turnsPage = context.PageC({ userId: 'web:user/中文', params: new URLSearchParams('tab=turns'), onNavigate() {} });
assert.ok(find(turnsPage, context.TabCTurns));
const notesPage = context.PageC({ userId: 'web:user/中文', params: new URLSearchParams('tab=facts'), onNavigate() {} });
assert.ok(find(notesPage, context.TabC4));
const noteSource = fs.readFileSync(path.join(root, 'PageC_facts.jsx'), 'utf8');
for (const route of ['/notes', '/forget-find', '/forget', '/hide-references', '/selections']) assert.ok(noteSource.includes(route));
assert.ok(!noteSource.includes('/facts'));
assert.ok(noteSource.includes('note_id'));
assert.ok(noteSource.includes('selection_id'));
assert.ok(noteSource.includes('source_message_ids'));
assert.ok(noteSource.includes('on_date'));
assert.ok(noteSource.includes('write="user"'));
const turnSource = fs.readFileSync(path.join(root, 'PageC.jsx'), 'utf8');
for (const field of ['origin_topic_id', 'turn_status', 'failure_reason', 'turn_id', '/turns']) assert.ok(turnSource.includes(field));
console.log(`NyaChat: ${jsx.length} JSX compiled with Babel 7.29.0; no browser-source comments; routing, permissions and audit presentation checks passed.`);
