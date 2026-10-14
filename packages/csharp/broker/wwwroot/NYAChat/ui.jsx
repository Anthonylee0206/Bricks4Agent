const { useState } = React;

const MONO = 'var(--font-mono)';

const ICONS = {
  hash:        'M4 9h16M4 15h16M10 3 8 21M16 3l-2 18',
  search:      'M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16ZM21 21l-4.3-4.3',
  settings:    'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06.06A1.65 1.65 0 0 0 4.6 15',
  check:       'M20 6 9 17 4 12',
  x:           'M18 6 6 18M6 6l12 12',
  plus:        'M12 5v14M5 12h14',
  trash:       'M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2',
  users:       'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75',
  user:        'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z',
  arrowLeft:   'M19 12H5M12 19l-7-7 7-7',
  arrowRight:  'M5 12h14M12 5l7 7-7 7',
  chevronDown: 'M6 9l6 6 6-6',
  chevronRight:'M9 18l6-6-6-6',
  refresh:     'M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15',
  cpu:         'M12 12m-1 0a1 1 0 1 0 2 0 1 1 0 1 0-2 0M9 9h6v6H9zM4 6V4h16v2M4 18v2h16v-2M2 9h2v6H2zM20 9h2v6h-2zM9 2v2M15 2v2M9 20v2M15 20v2',
  database:    'M12 2C8.13 2 5 3.34 5 5v14c0 1.66 3.13 3 7 3s7-1.34 7-3V5c0-1.66-3.13-3-7-3ZM5 5c0 1.66 3.13 3 7 3s7-1.34 7-3M5 12c0 1.66 3.13 3 7 3s7-1.34 7-3',
  list:        'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  clock:       'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20ZM12 6v6l4 2',
  history:     'M3 3v5h5M3.05 13A9 9 0 1 0 6 5.3L3 8',
  eye:         'M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8ZM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z',
  alertTriangle:'M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0ZM12 9v4M12 17h.01',
  log:         'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M16 13H8M16 17H8M10 9H8',
  soul:        'M12 2a5 5 0 1 0 0 10A5 5 0 0 0 12 2ZM4 20c0-4 3.58-7 8-7s8 3 8 7',
  undo:        'M3 7v6h6M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13',
  filter:      'M22 3H2l8 9.46V19l4 2v-8.54L22 3Z',
  info:        'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20ZM12 8h.01M12 12v4',
  edit:        'M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5Z',
  send:        'm22 2-7 20-4-9-9-4 20-7Z',
  link:        'M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71',
  zap:         'M13 2 3 14h9l-1 8 10-12h-9l1-8Z',
  archive:     'M21 8v13H3V8M1 3h22v5H1zM10 12h4',
  code:        'M16 18 22 12 16 6M8 6 2 12 8 18',
  message:     'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
  activity:    'M22 12h-4l-3 9L9 3l-3 9H2',
  trendUp:     'M23 6l-9.5 9.5-5-5L1 18M17 6h6v6',
  slash:       'M22 2 2 22',
  shield:      'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z',
  home:        'M3 10.5 12 3l9 7.5V21H3ZM9 21v-6h6v6',
};

function Icon({ name, size = 16, stroke = 1.5, color, style }) {
  const d = ICONS[name];
  if (!d) return React.createElement('span', { style: { width: size, height: size, display: 'inline-block' } });
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke={color || 'currentColor'} strokeWidth={stroke}
      strokeLinecap="round" strokeLinejoin="round"
      style={{ flex: 'none', display: 'block', ...style }}>
      {d.split('M').filter(Boolean).map((seg, i) => (
        <path key={i} d={'M' + seg} />
      ))}
    </svg>
  );
}

function Button({ variant = 'secondary', size = 'md', icon, iconRight, loading, children, style, write, ...rest }) {
  const h = size === 'sm' ? 28 : size === 'lg' ? 38 : 32;
  const px = size === 'sm' ? 10 : size === 'lg' ? 16 : 13;
  const fs = size === 'sm' ? 12 : 13;
  const variants = {
    primary:   { bg: 'var(--c-accent)',  fg: '#fff',            bd: 'var(--c-accent-fg)', bgH: 'var(--c-accent-fg)', fgH: '#fff' },
    secondary: { bg: 'var(--c-surface)', fg: 'var(--c-fg)',     bd: 'var(--c-fg)',        bgH: 'var(--c-fg)',        fgH: '#fff' },
    ghost:     { bg: 'transparent',      fg: 'var(--c-fg)',     bd: 'transparent',        bgH: 'var(--c-bg)',        fgH: 'var(--c-fg)' },
    danger:    { bg: 'var(--c-surface)', fg: 'var(--c-danger)', bd: 'var(--c-danger)',    bgH: 'var(--c-danger)',    fgH: '#fff' },
    'danger-filled': { bg: 'var(--c-danger)', fg: '#fff',       bd: 'var(--c-danger)',    bgH: 'var(--c-danger-fg)', fgH: '#fff' },
  };
  const v = variants[variant] || variants.secondary;
  const [hover, setHover] = useState(false);
  const isDisabled = rest.disabled || loading || (write && !nyaCanWrite(write));
  return (
    <button
      onMouseEnter={() => !isDisabled && setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 6,
        height: h, padding: `0 ${px}px`,
        background: isDisabled ? 'var(--c-bg)' : (hover ? v.bgH : v.bg),
        color: isDisabled ? 'var(--c-fg2)' : (hover ? v.fgH : v.fg),
        border: `1px solid ${isDisabled ? 'var(--c-border)' : (variant === 'ghost' && !hover ? 'transparent' : v.bd)}`,
        borderRadius: 4,
        fontSize: fs, fontWeight: 500,
        cursor: isDisabled ? 'not-allowed' : 'pointer',
        lineHeight: 1, whiteSpace: 'nowrap', flexShrink: 0,
        ...style
      }}
      {...rest}
      title={write && !nyaCanWrite(write) ? '目前帳號沒有此操作權限' : rest.title}
      disabled={isDisabled}
    >
      {loading
        ? <Spinner size={12} color="currentColor" />
        : icon ? <Icon name={icon} size={fs + 2} /> : null}
      {children}
      {iconRight && !loading && <Icon name={iconRight} size={fs + 2} />}
    </button>
  );
}

function IconButton({ name, size = 28, title, onClick, active, color, disabled }) {
  const [hover, setHover] = useState(false);
  return (
    <button title={title} aria-label={title} onClick={onClick} disabled={disabled}
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{
        width: size, height: size,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        background: active ? 'var(--c-fg)' : (hover ? 'var(--c-bg)' : 'transparent'),
        color: color || (active ? '#fff' : 'var(--c-fg)'),
        border: `1px solid ${active || hover ? 'var(--c-fg)' : 'transparent'}`,
        borderRadius: 4, padding: 0,
        cursor: disabled ? 'not-allowed' : 'pointer',
        flexShrink: 0,
      }}>
      <Icon name={name} size={Math.round(size * 0.52)} />
    </button>
  );
}

function Badge({ color = 'var(--c-fg)', bg = 'transparent', mono = true, dot, children, style }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 5,
      padding: '1px 7px',
      border: `1px solid ${color}`, borderRadius: 4,
      color, background: bg,
      fontFamily: mono ? MONO : 'inherit',
      fontSize: 11, fontWeight: 500,
      textTransform: 'uppercase', letterSpacing: '0.04em',
      lineHeight: '16px', whiteSpace: 'nowrap', flexShrink: 0,
      ...style,
    }}>
      {dot && <span style={{ width: 6, height: 6, borderRadius: '50%', background: color, flexShrink: 0 }} />}
      {children}
    </span>
  );
}

function StatusBadge({ ok, pending, err, idle, children }) {
  const color = ok ? 'var(--c-accent-fg)' : pending ? 'var(--c-warn)' : err ? 'var(--c-danger)' : 'var(--c-fg2)';
  return <Badge color={color} dot={ok || pending || err}>{children}</Badge>;
}

function StatTiles({ items, cols, style }) {
  const n = cols || items.length;
  return (
    <div style={{
      display: 'grid', gridTemplateColumns: `repeat(${n}, minmax(0, 1fr))`,
      border: '1px solid var(--c-fg)', background: 'var(--c-surface)', flexShrink: 0, ...style,
    }}>
      {items.map((it, i) => (
        <div key={i} style={{
          padding: '12px 16px 14px',
          borderRight: (i % n !== n - 1) ? '1px solid var(--c-border)' : 'none',
          borderTop: i >= n ? '1px solid var(--c-border)' : 'none',
          minWidth: 0,
        }}>
          <div style={{ fontSize: 11, fontWeight: 500, color: 'var(--c-fg2)', marginBottom: 6, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{it.label}</div>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 6 }}>
            <span className="mono" style={{ fontSize: 28, fontWeight: 500, lineHeight: 1, color: it.accent || 'var(--c-fg)', letterSpacing: '-0.01em' }}>{it.value}</span>
            {it.sub && <span className="mono" style={{ fontSize: 12, color: 'var(--c-fg2)' }}>{it.sub}</span>}
          </div>
        </div>
      ))}
    </div>
  );
}

function fieldBorder(focus) {
  return `${focus ? 2 : 1}px solid ${focus ? 'var(--c-accent-fg)' : 'var(--c-fg)'}`;
}

function Input({ value, onChange, placeholder, autoFocus, type = 'text', leftIcon, onKeyDown, style, disabled, mono, inputRef }) {
  const [focus, setFocus] = useState(false);
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8,
      height: 32, padding: `0 ${focus ? 9 : 10}px`,
      background: disabled ? 'var(--c-bg)' : 'var(--c-surface)',
      border: fieldBorder(focus), borderRadius: 4, flexShrink: 0,
      ...style
    }}>
      {leftIcon && <Icon name={leftIcon} size={14} style={{ color: 'var(--c-fg2)', flexShrink: 0 }} />}
      <input
        ref={inputRef}
        type={type} value={value} placeholder={placeholder}
        autoFocus={autoFocus} disabled={disabled}
        onChange={e => onChange && onChange(e.target.value)}
        onFocus={() => setFocus(true)} onBlur={() => setFocus(false)}
        onKeyDown={onKeyDown}
        style={{
          flex: 1, border: 0, outline: 'none', background: 'transparent',
          fontFamily: mono ? MONO : 'inherit',
          fontSize: 13, color: 'var(--c-fg)', padding: 0,
          height: '100%', minWidth: 0,
        }}
      />
    </div>
  );
}

function Select({ value, onChange, options, style, disabled }) {
  const [focus, setFocus] = useState(false);
  const normalized = options.map(o => typeof o === 'string' ? { value: o, label: o } : o);
  return (
    <div style={{ position: 'relative', display: 'inline-flex', alignItems: 'center', flexShrink: 0, ...style }}>
      <select
        value={value} disabled={disabled}
        onChange={e => onChange && onChange(e.target.value)}
        onFocus={() => setFocus(true)} onBlur={() => setFocus(false)}
        style={{
          width: '100%', height: 32, padding: '0 28px 0 10px',
          border: fieldBorder(focus), borderRadius: 4,
          background: disabled ? 'var(--c-bg)' : 'var(--c-surface)',
          fontSize: 13, color: 'var(--c-fg)',
          appearance: 'none', cursor: disabled ? 'not-allowed' : 'pointer', outline: 'none',
        }}>
        {normalized.filter(o => !o.group).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
        {[...new Set(normalized.filter(o => o.group).map(o => o.group))].map(group => <optgroup key={group} label={group}>{normalized.filter(o => o.group === group).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}</optgroup>)}
      </select>
      <Icon name="chevronDown" size={12} style={{ position: 'absolute', right: 8, pointerEvents: 'none', color: 'var(--c-fg2)' }} />
    </div>
  );
}

function Checkbox({ checked, onChange, label, id }) {
  return (
    <label htmlFor={id} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, cursor: 'pointer', userSelect: 'none' }}>
      <div style={{
        width: 14, height: 14, border: `1px solid ${checked ? 'var(--c-accent)' : 'var(--c-fg)'}`,
        borderRadius: 2, background: checked ? 'var(--c-accent)' : 'var(--c-surface)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        {checked && <Icon name="check" size={10} color="#fff" stroke={2.5} />}
      </div>
      <input type="checkbox" id={id} checked={checked} onChange={e => onChange && onChange(e.target.checked)}
        style={{ position: 'absolute', opacity: 0, width: 0, height: 0 }} />
      {label && <span style={{ fontSize: 13, color: 'var(--c-fg)' }}>{label}</span>}
    </label>
  );
}

function Spinner({ size = 16, color = 'var(--c-fg)' }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={2}
      strokeLinecap="round" style={{ animation: 'nya-spin 0.8s linear infinite', flexShrink: 0 }}>
      <path d="M12 2a10 10 0 0 1 10 10" />
    </svg>
  );
}

Object.assign(window, { Icon, Button, IconButton, Badge, StatusBadge, StatTiles, Input, Select, Checkbox, Spinner });
