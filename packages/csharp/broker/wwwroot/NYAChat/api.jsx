/* NyaChat Admin — API wrapper
   Calls mock resolver in MOCK_MODE, otherwise hits real backend. */

async function apiCall(method, path, params) {
  if (window.MOCK_MODE) {
    await new Promise(r => setTimeout(r, window.MOCK_DELAY || 200));
    try {
      const data = window.mockResolve(method, path, params || {});
      return { success: true, message: 'ok', data, error_code: null, trace_id: 'mock-' + Date.now() };
    } catch (e) {
      const code = e.message.startsWith('404') ? 404 : e.message.startsWith('400') ? 400 : 500;
      window.showToast && window.showToast(`${e.message}`, 'error');
      return { success: false, message: e.message, data: null, error_code: code, trace_id: 'mock-err' };
    }
  }

  const BASE = '/api/v1/local-admin/nya';
  let url = BASE + '/' + path.replace(/^\/+/, '');
  const opts = { method, credentials: 'include', headers: {} };

  if ((method === 'GET' || method === 'DELETE') && params && Object.keys(params).length) {
    const entries = Object.entries(params).filter(([, v]) => v !== null && v !== undefined && v !== '');
    if (entries.length) url += '?' + new URLSearchParams(entries).toString();
  } else if (params && method !== 'GET' && method !== 'DELETE') {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = JSON.stringify(params);
  }

  try {
    const res = await fetch(url, opts);
    const json = await res.json().catch(() => ({ success: false, message: res.statusText, data: null, error_code: res.status, trace_id: null }));
    if (!json.success || res.status === 401 || res.status === 403) {
      if (res.status === 401) {
        window.showToast && window.showToast('Session 已過期，請重新登入', 'error');
        window.location.href = '/line-admin.html';
      } else if (res.status === 403) {
        window.showToast && window.showToast(`權限不足：${json.message}`, 'error');
      } else {
        const traceHint = json.trace_id ? `  trace: ${json.trace_id}` : '';
        window.showToast && window.showToast(`錯誤：${json.message}${traceHint}`, 'error');
      }
    }
    return json;
  } catch (e) {
    window.showToast && window.showToast(`網路錯誤：${e.message}`, 'error');
    return { success: false, message: e.message, data: null, error_code: null, trace_id: null };
  }
}

window.apiCall = apiCall;
