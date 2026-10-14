const { useState, useEffect, useCallback } = React;

let _toastFn = null;
window.showToast = (msg, type = 'info', duration = 3000) => {
  _toastFn && _toastFn(msg, type, duration);
};

function ToastItem({ toast, onRemove }) {
  const colors = {
    success: { border: 'var(--c-accent)', text: 'var(--c-accent-fg)', icon: 'check' },
    error:   { border: 'var(--c-danger)', text: 'var(--c-danger-fg)', icon: 'alertTriangle' },
    info:    { border: 'var(--c-fg)',     text: 'var(--c-fg)',        icon: 'info' },
  };
  const c = colors[toast.type] || colors.info;
  return (
    <div role="status" style={{
      display: 'flex', alignItems: 'flex-start', gap: 10,
      padding: '10px 14px', background: 'var(--c-surface)',
      border: `1px solid ${c.border}`, borderLeftWidth: 2, borderRadius: 4,
      minWidth: 280, maxWidth: 420,
    }}>
      <Icon name={c.icon} size={14} color={c.border} style={{ marginTop: 1 }} />
      <span style={{ fontSize: 13, color: c.text, flex: 1, lineHeight: 1.4 }}>{toast.msg}</span>
      <button onClick={() => onRemove(toast.id)} aria-label="關閉" style={{
        border: 'none', background: 'none', cursor: 'pointer', padding: 0, color: c.text,
      }}><Icon name="x" size={13} /></button>
    </div>
  );
}

function ToastContainer() {
  const [toasts, setToasts] = useState([]);
  const remove = useCallback(id => setToasts(p => p.filter(t => t.id !== id)), []);
  useEffect(() => {
    _toastFn = (msg, type, duration) => {
      const id = Date.now() + Math.random();
      setToasts(p => [...p, { id, msg, type }]);
      setTimeout(() => remove(id), type === 'error' ? Math.max(duration, 6000) : duration);
    };
    return () => { _toastFn = null; };
  }, [remove]);
  return (
    <div style={{
      position: 'fixed', bottom: 16, right: 16, zIndex: 9999,
      display: 'flex', flexDirection: 'column', gap: 8, pointerEvents: 'none',
    }}>
      {toasts.map(t => (
        <div key={t.id} style={{ pointerEvents: 'all' }}>
          <ToastItem toast={t} onRemove={remove} />
        </div>
      ))}
    </div>
  );
}

function Overlay({ zIndex, onBackdrop, children }) {
  return (
    <div style={{
      position: 'fixed', inset: 0, zIndex,
      background: 'rgba(26,28,26,0.4)',
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }} onClick={e => { if (e.target === e.currentTarget) onBackdrop(); }}>
      {children}
    </div>
  );
}

let _confirmFn = null;
window.showConfirm = (title, description) => {
  if (!_confirmFn) return Promise.resolve(false);
  return _confirmFn(title, description);
};

function ConfirmDialog() {
  const [state, setState] = useState(null);
  useEffect(() => {
    _confirmFn = (title, description) => new Promise(resolve => {
      setState({ title, description, resolve });
    });
    return () => { _confirmFn = null; };
  }, []);
  useEffect(() => {
    if (!state) return;
    const fn = e => { if (e.key === 'Escape') { e.stopImmediatePropagation(); state.resolve(false); setState(null); } };
    document.addEventListener('keydown', fn, true);
    return () => document.removeEventListener('keydown', fn, true);
  }, [state]);
  if (!state) return null;
  const confirm = () => { state.resolve(true);  setState(null); };
  const cancel  = () => { state.resolve(false); setState(null); };
  return (
    <Overlay zIndex={9000} onBackdrop={cancel}>
      <div role="alertdialog" aria-modal="true" style={{
        background: 'var(--c-surface)', border: '1px solid var(--c-fg)',
        width: 'min(480px, calc(100vw - 32px))',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '14px 20px', borderBottom: '1px solid var(--c-border)' }}>
          <Icon name="alertTriangle" size={15} color="var(--c-danger)" />
          <span style={{ fontSize: 14, fontWeight: 600, color: 'var(--c-fg)' }}>{state.title}</span>
        </div>
        {state.description && (
          <div style={{ padding: '16px 20px', fontSize: 13, color: 'var(--c-fg2)', lineHeight: 1.6 }}>{state.description}</div>
        )}
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, padding: '12px 20px', borderTop: '1px solid var(--c-border)', background: 'var(--c-bg)' }}>
          <Button variant="ghost" onClick={cancel} autoFocus>取消</Button>
          <Button variant="danger-filled" onClick={confirm}>確認執行</Button>
        </div>
      </div>
    </Overlay>
  );
}

function Modal({ title, onClose, children, width = 600 }) {
  useEffect(() => {
    const fn = e => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', fn);
    return () => document.removeEventListener('keydown', fn);
  }, [onClose]);
  return (
    <Overlay zIndex={8000} onBackdrop={onClose}>
      <div role="dialog" aria-modal="true" style={{
        background: 'var(--c-surface)', border: '1px solid var(--c-fg)',
        width, maxWidth: 'calc(100vw - 48px)', maxHeight: 'calc(100vh - 80px)',
        display: 'flex', flexDirection: 'column',
      }}>
        <div style={{
          display: 'flex', alignItems: 'center', justifyContent: 'space-between',
          padding: '0 16px', height: 46, borderBottom: '1px solid var(--c-border)', flexShrink: 0,
        }}>
          <span style={{ fontSize: 14, fontWeight: 600, color: 'var(--c-fg)' }}>{title}</span>
          <IconButton name="x" size={28} onClick={onClose} title="關閉" />
        </div>
        <div style={{ overflow: 'auto', flex: 1 }}>{children}</div>
      </div>
    </Overlay>
  );
}

function Pagination({ total, limit, offset, onPage, onLimitChange, limitOptions = [20, 50, 100] }) {
  const page = Math.floor(offset / limit) + 1;
  const totalPages = Math.ceil(total / limit);
  const from = total === 0 ? 0 : offset + 1;
  const to = Math.min(offset + limit, total);
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 12,
      padding: '8px 16px', borderTop: '1px solid var(--c-border)', flexShrink: 0,
      background: 'var(--c-surface)',
    }}>
      <span style={{ flex: 1, fontSize: 12, color: 'var(--c-fg2)' }}>第 {from}–{to} 筆，共 {total} 筆</span>
      <span style={{ fontSize: 12, color: 'var(--c-fg2)' }}>每頁</span>
      <Select
        value={String(limit)}
        options={limitOptions.map(v => ({ value: String(v), label: String(v) }))}
        onChange={v => onLimitChange && onLimitChange(Number(v))}
        style={{ width: 70 }}
      />
      <Button variant="ghost" size="sm" icon="arrowLeft" disabled={offset <= 0}
        onClick={() => onPage(Math.max(0, offset - limit))}>上一頁</Button>
      <span className="mono" style={{ fontSize: 12, minWidth: 44, textAlign: 'center' }}>{page} / {totalPages || 1}</span>
      <Button variant="ghost" size="sm" iconRight="arrowRight" disabled={offset + limit >= total}
        onClick={() => onPage(offset + limit)}>下一頁</Button>
    </div>
  );
}

function SectionHeader({ title, actions, subtitle, accent = 'var(--c-fg)' }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'space-between',
      padding: '0 14px', minHeight: 40, borderBottom: '1px solid var(--c-border)', flexShrink: 0,
      background: 'var(--c-surface)',
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 9, minWidth: 0 }}>
        <span style={{ width: 2, height: 13, background: accent, flexShrink: 0 }} />
        <span style={{ fontSize: 13, fontWeight: 600, color: 'var(--c-fg)', whiteSpace: 'nowrap' }}>{title}</span>
        {subtitle && <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg2)' }}>{subtitle}</span>}
      </div>
      {actions && <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>{actions}</div>}
    </div>
  );
}

function Panel({ title, actions, subtitle, accent, children, style, bodyStyle }) {
  return (
    <div style={{ border: '1px solid var(--c-border)', background: 'var(--c-surface)', flexShrink: 0, ...style }}>
      {title && <SectionHeader title={title} actions={actions} subtitle={subtitle} accent={accent} />}
      <div style={bodyStyle}>{children}</div>
    </div>
  );
}

function KVGrid({ items, cols = 2 }) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, 1fr)` }}>
      {items.map((item, i) => (
        <div key={i} style={{
          gridColumn: item.wide ? '1 / -1' : undefined,
          padding: '9px 14px',
          borderBottom: '1px solid var(--c-border)',
          borderRight: (i % cols !== cols - 1 && !item.wide) ? '1px solid var(--c-border)' : 'none',
          minWidth: 0,
        }}>
          <div style={{ fontSize: 11, fontWeight: 500, color: 'var(--c-fg2)', marginBottom: 3 }}>{item.label}</div>
          <div className={item.mono ? 'mono' : undefined} style={{ fontSize: 13, color: 'var(--c-fg)', wordBreak: 'break-all', lineHeight: 1.45 }}>
            {item.value ?? <span style={{ color: 'var(--c-fg4)' }}>—</span>}
          </div>
        </div>
      ))}
    </div>
  );
}

function JsonBlock({ raw, flush }) {
  let display;
  try {
    display = JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    display = raw || '(空)';
  }
  return (
    <pre className="mono" style={{
      margin: 0, padding: '12px 14px', fontSize: 12,
      color: 'var(--c-fg)', background: 'var(--c-bg)',
      overflowX: 'auto', lineHeight: 1.6,
      borderTop: flush ? 'none' : '1px solid var(--c-border)',
    }}>
      <code>{display}</code>
    </pre>
  );
}

function LoadingRow({ cols = 5 }) {
  return (
    <tr>
      <td colSpan={cols} style={{ padding: '28px 16px', textAlign: 'center', color: 'var(--c-fg2)' }}>
        <div style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Spinner size={14} /><span style={{ fontSize: 13 }}>載入中…</span>
        </div>
      </td>
    </tr>
  );
}

function EmptyRow({ cols = 5, message = '無資料' }) {
  return (
    <tr>
      <td colSpan={cols} style={{ padding: '36px 16px', textAlign: 'center', color: 'var(--c-fg2)', fontSize: 13 }}>{message}</td>
    </tr>
  );
}

function EmptyState({ icon = 'info', message, hint, loading }) {
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
      gap: 10, padding: '48px 16px', color: 'var(--c-fg2)', textAlign: 'center',
    }}>
      {loading ? <Spinner size={18} /> : <Icon name={icon} size={20} color="var(--c-fg4)" />}
      <div style={{ fontSize: 13 }}>{loading ? '載入中…' : message}</div>
      {hint && !loading && <div className="mono" style={{ fontSize: 11, color: 'var(--c-fg3)' }}>{hint}</div>}
    </div>
  );
}

function Table({ columns, children, style }) {
  return (
    <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13, color: 'var(--c-fg)', tableLayout: 'auto', ...style }}>
      <thead>
        <tr style={{ background: 'var(--c-bg)', borderBottom: '1px solid var(--c-border)' }}>
          {columns.map((col, i) => (
            <th key={i} style={{
              padding: '7px 12px', textAlign: col.center ? 'center' : col.right ? 'right' : 'left',
              fontSize: 11, fontWeight: 600, color: 'var(--c-fg2)', whiteSpace: 'nowrap',
              borderRight: i < columns.length - 1 ? '1px solid var(--c-border-soft)' : 'none',
              background: 'var(--c-bg)',
              width: col.width, position: 'sticky', top: 0, zIndex: 1,
            }}>{col.label}</th>
          ))}
        </tr>
      </thead>
      <tbody>{children}</tbody>
    </table>
  );
}

function TR({ children, onClick, selected }) {
  const [hover, setHover] = useState(false);
  return (
    <tr
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        borderBottom: '1px solid var(--c-border-soft)',
        background: selected ? 'var(--c-bg)' : hover && onClick ? 'var(--c-hover)' : 'var(--c-surface)',
        cursor: onClick ? 'pointer' : 'default',
        boxShadow: selected ? 'inset 2px 0 0 var(--c-fg)' : 'none',
      }}>
      {children}
    </tr>
  );
}

function TD({ children, mono, muted, center, right, style, noWrap, title }) {
  return (
    <td title={title} className={mono ? 'mono' : undefined} style={{
      padding: '8px 12px',
      fontSize: mono ? 12 : 13,
      color: muted ? 'var(--c-fg2)' : 'var(--c-fg)',
      textAlign: center ? 'center' : right ? 'right' : 'left',
      verticalAlign: 'middle',
      whiteSpace: noWrap ? 'nowrap' : undefined,
      ...style
    }}>
      {children}
    </td>
  );
}

function DangerZone({ children, title = '危險操作' }) {
  return (
    <div style={{ border: '1px solid var(--c-danger)', background: 'var(--c-surface)', flexShrink: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '0 14px', height: 40, borderBottom: '1px solid var(--c-danger)' }}>
        <span style={{ width: 2, height: 13, background: 'var(--c-danger)' }} />
        <Icon name="alertTriangle" size={13} color="var(--c-danger)" />
        <span style={{ fontSize: 13, fontWeight: 600, color: 'var(--c-danger)' }}>{title}</span>
      </div>
      <div style={{ padding: 14, display: 'flex', flexDirection: 'column', gap: 12 }}>{children}</div>
    </div>
  );
}

function DangerAction({ label, description, buttonLabel, onConfirm, loading }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16 }}>
      <div>
        <div style={{ fontSize: 13, fontWeight: 500, color: 'var(--c-fg)', marginBottom: 2 }}>{label}</div>
        {description && <div style={{ fontSize: 12, color: 'var(--c-fg2)', lineHeight: 1.5 }}>{description}</div>}
      </div>
      <Button write="user" variant="danger" size="sm" loading={loading} onClick={onConfirm}>{buttonLabel || label}</Button>
    </div>
  );
}

function Toolbar({ children, borderBottom = true }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8,
      padding: '8px 14px', flexWrap: 'wrap', flexShrink: 0,
      borderBottom: borderBottom ? '1px solid var(--c-border)' : 'none',
      background: 'var(--c-surface)',
    }}>
      {children}
    </div>
  );
}

function PageHeader({ title, count, desc, children }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 10,
      padding: desc ? '10px 16px' : '0 16px', minHeight: 52, borderBottom: '1px solid var(--c-border)', flexShrink: 0,
      background: 'var(--c-surface)',
    }}>
      <div style={{ minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h1 style={{ fontSize: 16, fontWeight: 600, color: 'var(--c-fg)' }}>{title}</h1>
          {count != null && (
            <span className="mono" style={{ fontSize: 11, color: 'var(--c-fg2)', border: '1px solid var(--c-border)', borderRadius: 4, padding: '1px 7px', lineHeight: '16px' }}>{count}</span>
          )}
        </div>
        {desc && <div style={{ fontSize: 12, color: 'var(--c-fg2)', marginTop: 3 }}>{desc}</div>}
      </div>
      <div style={{ flex: 1 }} />
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>{children}</div>
    </div>
  );
}

function TabBar({ tabs, active, onChange }) {
  return (
    <div role="tablist" style={{
      display: 'flex', borderBottom: '1px solid var(--c-border)',
      background: 'var(--c-surface)', flexShrink: 0, overflowX: 'auto', padding: '0 6px',
    }}>
      {tabs.map(tab => {
        const isActive = tab.id === active;
        return (
          <button key={tab.id} role="tab" aria-selected={isActive} onClick={() => onChange(tab.id)}
            style={{
              display: 'flex', alignItems: 'center', gap: 6,
              padding: '0 14px', height: 42, border: 'none', background: 'none',
              borderBottom: isActive ? '2px solid var(--c-fg)' : '2px solid transparent',
              fontSize: 13, fontWeight: isActive ? 600 : 400, color: isActive ? 'var(--c-fg)' : 'var(--c-fg2)',
              cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
            }}>
            {tab.icon && <Icon name={tab.icon} size={14} />}
            {tab.label}
            {tab.badge != null && (
              <span className="mono" style={{ fontSize: 10, color: isActive ? 'var(--c-fg)' : 'var(--c-fg3)' }}>{tab.badge}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}

function ContentArea({ children, style }) {
  return (
    <div style={{
      flex: 1, overflow: 'auto', background: 'var(--c-bg)',
      padding: 16, display: 'flex', flexDirection: 'column', gap: 14,
      ...style,
    }}>
      {children}
    </div>
  );
}


function _asUtc(iso) {
  if (!iso) return iso;
  const s = String(iso);
  if (s.endsWith('Z') || s.endsWith('+00:00') || /[+-]\d{2}:\d{2}$/.test(s)) return s;
  return s + 'Z';
}

function fmtTime(iso) {
  if (!iso) return '—';
  try {
    return new Date(_asUtc(iso)).toLocaleString('zh-TW', { timeZone: 'Asia/Taipei', hour12: false });
  } catch { return iso; }
}

function fmtTimeShort(iso) {
  if (!iso) return '—';
  try {
    return new Date(_asUtc(iso)).toLocaleString('zh-TW', { timeZone: 'Asia/Taipei', hour12: false,
      month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
  } catch { return iso; }
}

function fmtRelative(iso) {
  if (!iso) return '—';
  const diff = (Date.now() - new Date(_asUtc(iso)).getTime()) / 1000;
  if (!isFinite(diff)) return '—';
  if (diff < 60) return '剛剛';
  if (diff < 3600) return `${Math.floor(diff / 60)} 分鐘前`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} 小時前`;
  if (diff < 86400 * 7) return `${Math.floor(diff / 86400)} 天前`;
  return fmtTimeShort(iso);
}

Object.assign(window, {
  ToastContainer, ConfirmDialog, Modal,
  Pagination, SectionHeader, Panel, KVGrid, JsonBlock,
  LoadingRow, EmptyRow, EmptyState, Table, TR, TD,
  DangerZone, DangerAction, Toolbar, PageHeader, TabBar, ContentArea,
  fmtTime, fmtTimeShort, fmtRelative,
});
