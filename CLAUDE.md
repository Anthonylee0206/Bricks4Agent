# Bricks4Agent — Claude Code Rules

## Project Identity

Bricks4Agent's end state is an **AI agent service**: it takes requests from human users or from other AI agents and generates systems, pages and features from this component library. Everything in the repo serves that goal:

- **Building blocks** — a zero-runtime-dependency Vanilla JS **UI component library** plus a **page/SPA generator** that turns a JSON `PageDefinition` into working pages (static code generation or dynamic runtime rendering), the SPA scaffolding and the form application studio.

- **Agent service** — the agent runtime ([tools/agent](tools/agent/README.md)), the broker that issues scoped sessions, enforces capability and scope policy and proxies model traffic (`packages/csharp/broker`, `packages/csharp/broker-core`), and the workers it dispatches (`packages/csharp/workers`). The agent container reaches only the broker and holds no provider API key; see [tools/agent/container/README.md](tools/agent/container/README.md).

- Authoritative component list: [component-catalog.json](packages/javascript/browser/ui_components/metadata/component-catalog.json) (134 components)

- Component calling convention (read this before building): [AGENT-UI-GUIDE.md](AGENT-UI-GUIDE.md)

## Build & Test

- Full JS gate: `npm test` (runs `test-all.js` generator examples, then `test:ui-components`, `test:custom-components`, `test:generation` and the Vitest component suites via `npm --prefix packages/javascript/browser run test:vitest`; run `npm --prefix packages/javascript/browser install` once first to get the vitest/jsdom devDependencies)

- UI library checks: `npm run validate:ui-library` (add `:browser` for a real browser)

- Component/renderer unit tests: `npm run test:ui-components` (node:test; covers `ui_components/**/*.test.mjs`, `page-generator/**/*.test.mjs`, and the DOM-equivalence utility)

- Style-token audit: `npm run audit:ui-styles`

- Governed generation (DefinitionTemplate → multi-page prototype): `npm run test:generation` (node:test for `tools/generation/`: layered validation, catalog summary, deterministic build, CLI exit-code contract); `npm run test:definition-site:browser` builds the golden example into `.test-output/` and drives every route in Edge (hard-imports playwright-core from `../tim-web/poc`). Broker side: `GovernedGenerationTests` in the integration suite (one case runs the real generation-worker handlers and `tools/generation/cli.mjs` in process, so the integration suite needs node); design and settings in [GovernedGeneration.md](docs/designs/GovernedGeneration.md)

- All SDK-style .NET 10 projects, with every warning treated as an error: `npm run test:dotnet10`

- .NET tests: `dotnet test packages/csharp/tests/unit/Unit.Tests.csproj`, `dotnet test packages/csharp/tests/integration/Integration.Tests.csproj`, `dotnet test templates/spa/backend.Tests/SpaApi.Template.Tests.csproj`

- CSP + SVG hard-zero gate (must pass before any commit touching ui_components): `node tools/scripts/audit-csp.mjs`

- Browser smoke harnesses (need `python -m http.server 8124` at repo root; they hard-import playwright-core from `../../../tim-web/poc`):
 `node tools/scripts/canvas-chart-smoke.mjs`,
 `node tools/scripts/wave2-stage-sweep.mjs`, `node tools/scripts/data-explorer-smoke.mjs`,
 `node tools/scripts/cluster-graph-perf.mjs`

- Studio browser smoke (self-hosted on a random port, no 8124 server needed): `npm run test:studio:browser`; `node tools/theme-studio/run.mjs` is a compatibility alias that runs the same `tools/scripts/studio-integration-smoke.mjs`

- Generated .NET 10 backend (SPA template): `dotnet build templates/spa/backend/SpaApi.csproj`

- Rebuild component metadata after adding/changing a component:
 `node packages/javascript/browser/ui_components/metadata/build-metadata.mjs` (`--check` to validate only)

- Generate pages from a definition: `node tools/page-gen.js --def page.json --mode static --output ./out/`.
 The input must be the page-gen format `{ page: {...}, fields: [{ fieldName, fieldType, ... }] }` or a DefinitionTemplate; a plain PageDefinition `{ name, type, fields }` is rejected (`缺少 page 區塊`).
 For a DefinitionTemplate: `--page <id>` for one page, or `--pages <id,id,...>` / `--all` to generate many in a single process (aggregated JSON result; all selected pages are validated before any is written).

## Test Artifacts and Cleanup

**Rule: all test-generated files must be tracked and cleaned up.**

| Artifact | Source | Cleanup |
|---|---|---|
| `.test-output/` | test output directory | delete after testing |
| generated pages/projects under `out/` (or your `--output`) | `spa-cli.js` / `page-gen.js` | delete after testing |
| `%TEMP%/b4a-gen*` (`b4a-gen-test-*`, `b4a-gen-truncate-*`, `b4a-gengov-*`, `b4a-gencontract-*`, `b4a-genworker-*`, `b4a-gen-toolspec-*`) | generation node:test and xUnit suites | removed by the tests; delete leftovers |
| `%TEMP%/broker_integration_*` (`.db`, `-shm`, `-wal`, and `broker_integration_access_*` directories) | integration test fixtures (`BrokerFixture`) | removed by the fixtures (retried); delete leftovers |
| `%TEMP%/broker_test_*.db` (with `-shm`, `-wal`) | unit tests (`TestDb.CreateInMemory`) | deleted when the test process exits; delete leftovers |
| `%TEMP%/nya_*.db` (with `-shm`, `-wal`) | NyaChat broker-tests (`Nya*Tests`) | removed by the tests (`Pooling=False` + cleanup); delete leftovers |
| `packages/csharp/broker/nyachat.db` (with `-shm`, `-wal`) | broker startup (NyaChat database, gitignored by `*.db`) | delete after testing (clears NyaChat memory) |

When adding tests that produce files: add the pattern to this table, ensure it is in `.gitignore`, and clean it up in the test.

## Code Conventions

- Frontend: JavaScript, ES modules, vanilla — **no third-party runtime UI dependency** (the only exceptions are vendored copies under `ui_components/vendor/`: Leaflet 1.9.4 and html2canvas, loaded locally first with CDN fallback only when the local file is missing).

- Styling: theme tokens only (`var(--cl-*)`); no hard-coded colors. Dark mode via `[data-theme="dark"]`, not per-component media queries. See [STYLE_CONVENTION.md](packages/javascript/browser/ui_components/STYLE_CONVENTION.md).

- Strict CSP (machine-enforced by `tools/scripts/audit-csp.mjs`): no `<style>` injection, no `style=`/`on*=` inside innerHTML templates, no eval / `javascript:` URLs. Style via CSSOM (`cssText`/`setProperty`) or a co-located `.css` + same-origin `<link>`.

- **SVG is banned — Canvas only**. `audit-csp.mjs` enforces hard zero; `tools/scripts/svg-baseline.json` is an empty inventory snapshot and cannot waive findings. Chart base = `viz/CanvasChart.js` (DPR, hit-regions, tooltip, exportPNG); theme reactivity via `utils/theme-bus.js`; `Path2D` accepts SVG path strings directly. Canvas color fallbacks must use `FALLBACK_PAINT` from theme-bus — no loose hex in components.

- Security: escape all dynamic HTML with `escapeHtml()`; opt into raw HTML explicitly with `raw()` (see [utils/security.js](packages/javascript/browser/ui_components/utils/security.js)). `isRawHtml()` only accepts the `Symbol.for('bricks4agent.rawHtml')` own property that `raw()` brands (a plain `{ __html }` logs a one-time console warning naming `raw()` rather than failing silently) — a hand-written or `JSON.parse`d `{ __html }` is no longer an opt-in and falls back to escaping/`sanitizeHTML()`, so API data cannot forge it. `__html` survives on the marker for readers, not as authorisation.

- i18n: user-facing strings go through `Locale.t()` (see [i18n/index.js](packages/javascript/browser/ui_components/i18n/index.js)).

- Component contract: `new X(options)` → `.mount(container)` → `.destroy()`; value components expose `getValue/setValue/setDisabled/clear` (form ones also `setError(msg, { display })/clearError`, delegating to `utils/field-error.js`; `display: false` marks the control invalid without rendering text, and `FormField` `markControl` / `SearchForm` `markInvalidFields` forward errors to inner components only when opted in). `destroy()` is mandatory, not optional. `ModalPanel.confirm/alert/prompt` pass `destroyOnClose: true` so the dialog self-destructs after `close()`; a direct `new ModalPanel` keeps `destroyOnClose: false` and stays reusable across `close()`/`open()`. ModalPanel carries `role="dialog"`/`aria-modal`, and one Escape closes only the topmost modal (an Escape an inner control already handled is ignored); focus trapping/restoring is off by default for compatibility — new projects should set `ModalPanel.defaults.manageFocus = true` at startup.

- Generator definitions: `definition.name`, `field.name` and `behaviors.*` (`onInit`/`onSave`/`onDelete`/`fieldTriggers` values, all handler-method names) are emitted as bare JavaScript identifiers, so `PageGenerator` validates them as real `IdentifierName` — CJK names such as `姓名` pass; reserved words are rejected only in binding positions (`definition.name`). Failures come back through the normal `{ code: null, errors: [...] }` contract.

- `DynamicToolRenderer` skips a binding whose value is unchanged since the last one pushed to that component (deep compare; key order counts as a change). Consumers that re-`setState` every leaf path on each sync no longer re-apply every setter — and a bound option with no `set<Option>` setter no longer destroys and recreates the component on every sync. One intended consequence: an unrelated `setState` no longer resets a component's own state (uncommitted input, scroll position) back to the bound value.

- Tool pages (`type: 'tool'` / `DynamicToolRenderer`) default to [LazyComponentFactory](packages/javascript/browser/ui_components/binding/LazyComponentFactory.js), which loads only the components a definition names: `create()` stays synchronous, `preload(names)` is awaited inside `init()`. The eager `ComponentFactory` is unchanged for direct callers.

- Detail pages default to `lazyTabs: true` (`DynamicPageRenderer` / `DynamicDetailRenderer`): a tab's content is built on first activation, so components in never-activated tabs do not exist yet. Pass `lazyTabs: false` to build everything up front. An unpopulated panel carries `data-tab-pending="true"` until first activation (removed once populated), so DOM inspection can tell "not built yet" from "definition is wrong".

- Generated backend: .NET 10 Minimal API, BaseOrm (lightweight, no EF Core).

- Password hashing: use the static `Rfc2898DeriveBytes.Pbkdf2` API without changing the existing iteration/salt/hash sizes or stored formats; compatibility vectors live in the unit and SPA template test projects.

## Adding a Component

1. Create `ui_components/<category>/<Name>/` with `<Name>.js`, `index.js` and, for new components, a self-registering `locale.js` (`Locale.register(lang, namespace, strings)`). Other components import it only through its `index.js` (`validate-ui-library` rejects cross-component implementation imports). Its `component.manifest.json` is generated in step 4 (schema: [manifest-schema.js](packages/javascript/browser/ui_components/metadata/manifest-schema.js)); do not hand-edit it — adjust inferred kind/role/maturity through the override tables in `metadata/renderer.js`.

2. Export it from the category `index.js`.

3. Register it in [ComponentFactory.js](packages/javascript/browser/ui_components/binding/ComponentFactory.js) if it should be usable by name/generator.

4. Rebuild metadata: `node packages/javascript/browser/ui_components/metadata/build-metadata.mjs`, then verify with `--check` (read-only; it fails when a manifest exists without a ComponentFactory registration).

Full details: [AGENT-UI-GUIDE.md](AGENT-UI-GUIDE.md) §8.

## Documentation

- Component calling convention + React-rewrite playbook: [AGENT-UI-GUIDE.md](AGENT-UI-GUIDE.md)

- SPA generator manual: [AGENT.md](AGENT.md)

- Page generator: [page-generator/README.md](packages/javascript/browser/page-generator/README.md)
