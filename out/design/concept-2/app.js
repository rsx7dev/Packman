// Packman concept 2 — prototype behaviour. Plain JS, no build step.
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const icon = (id, cls = 'i sm') => `<svg class="${cls}"><use href="#${id}"/></svg>`;
const TILE = ['#5D7A2E', '#3E6B8A', '#8A4F3E', '#5B5A8E', '#2F7468', '#8A6A2E', '#6B4E7A', '#4F6B3A'];

const APPS = [
  ['Contoso Reader', 'Contoso', '4.2.0', '2026-10-24', 120, 104, 8, 3, 3, 2, 'System', '6c1f3b2e-7a54-4d0e-b8a1-3f92c5d7e014'],
  ['7-Zip', 'Igor Pavlov', '24.09', '2026-10-22', 1310, 1288, 11, 2, 9, 0, 'System', 'a3e09d71-2b6c-4f85-9e14-7d0c8b5a2f63'],
  ['Mozilla Firefox (x64 en-US)', 'Mozilla', '131.0.3', '2026-10-21', 842, 790, 31, 6, 12, 3, 'System', '0f7b2c94-1e3a-4d68-a5b0-9c4e7f1d2a85'],
  ['Notepad++', 'Notepad++ Team', '8.7', '2026-10-18', 214, 209, 2, 0, 3, 0, 'System', '58d1e6a0-9c27-4b3f-8e05-2a7f4c9b1d36'],
  ['Google Chrome Enterprise', 'Google LLC', '130.0.6723.70', '2026-10-17', 1295, 1240, 38, 9, 8, 0, 'System', 'c92a5f13-4d8e-47b1-a6c0-5e3b9d2f7084'],
  ['Adobe Acrobat Reader', 'Adobe Inc.', '24.003.20180', '2026-10-15', 1188, 1101, 47, 21, 19, 0, 'System', '1d4e8b07-6f2c-4a95-b3e1-8c0a7d5f2e49'],
  ['Zoom Workplace', 'Zoom Video Communications', '6.2.5', '2026-10-11', 1204, 1172, 19, 4, 9, 0, 'User', '7e2b9c41-0a5d-4f36-9b8e-4d1c6a3f5b02'],
  ['VLC media player', 'VideoLAN', '3.0.21', '2026-10-09', 96, 93, 1, 0, 2, 0, 'System', 'b05c3e82-7d1a-4e69-a4f2-0e8b5c9d3a71'],
  ['Git for Windows', 'The Git Development Community', '2.47.0', '2026-10-08', 64, 61, 2, 1, 0, 0, 'System', '3a8f1d6c-5e0b-4c27-b9a3-6f2e8d4c1b95'],
  ['Visual Studio Code', 'Microsoft Corporation', '1.94.2', '2026-10-04', 71, 68, 1, 0, 2, 0, 'User', 'e4b7a209-3c6f-4d81-8a5e-1b9d0c7f6e23'],
  ['KeePassXC', 'KeePassXC Team', '2.7.9', '2026-09-30', 388, 375, 6, 2, 5, 0, 'System', '9c3d5e10-8b4a-4f72-a1e6-7d2c0b9e5f48'],
  ['PuTTY', 'Simon Tatham', '0.81', '2026-09-26', 58, 57, 0, 0, 1, 0, 'System', '2f6a8c93-1d5e-4b07-9c4a-3e8f7b2d0c16'],
  ['WinSCP', 'Martin Přikryl', '6.3.5', '2026-09-23', 47, 45, 1, 0, 1, 0, 'System', 'd71e4b08-6a2c-4935-b8f1-0c5d9e3a7b24'],
  ['Python 3.13', 'Python Software Foundation', '3.13.0', '2026-09-19', 39, 35, 2, 1, 1, 0, 'System', '5b0e9f27-4c8d-4a16-a3b5-8e1f2d7c6a90'],
].map((a, i) => ({ name: a[0], pub: a[1], ver: a[2], updated: a[3], targeted: a[4], installed: a[5], pending: a[6], failed: a[7], notInstalled: a[8], na: a[9], ctx: a[10], id: a[11], tile: TILE[i % TILE.length] }));

const state = { signedIn: true, sel: APPS[0], page: 0, sort: 'name', query: '', generated: false, uploadFolder: false, uploadRules: 0 };
const PAGE = 10;
const fmtDate = d => new Date(d + 'T12:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const now = () => new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/* ── Routing ─────────────────────────────── */
function show() {
  const sections = $$('section[data-screen]');
  const sec = sections.find(s => s.dataset.screen === location.hash.slice(1)) || sections[0];
  sections.forEach(s => s.classList.toggle('on', s === sec));
  const id = sec.dataset.screen;
  closeDialog();
  $$('.dock .nav').forEach(n => n.classList.toggle('on', n.dataset.nav === sec.dataset.nav));
  $('#dock').classList.toggle('full', sec.classList.contains('no-rail'));
  const ctx = $('#dock-ctx');
  ctx.replaceChildren($('template.dock-ctx', sec).content.cloneNode(true));
  $('#where').textContent = sec.dataset.title;
  document.title = `Packman — ${sec.dataset.title}`;
  if (id === 'applications') renderApps();
  if (id === 'app-detail') renderDetail();
  if (id === 'create') renderCreate();
  if (id === 'upload') renderUpload();
  applyAuth();
  parent.postMessage({ orchScreen: id }, '*');
}
window.addEventListener('hashchange', show);

/* ── Signed in / out ─────────────────────── */
function applyAuth() {
  const on = state.signedIn;
  $('#pill').classList.toggle('on', on);
  $('#pill-label').textContent = on ? 'Connected' : 'Not connected';
  $('#pill-tenant').textContent = on ? 'Contoso Ltd' : 'Sign in';
  $$('[data-signed-out]').forEach(e => e.hidden = on);
  $$('[data-signed-in]').forEach(e => e.hidden = !on);
  $$('[data-needs-auth]').forEach(e => e.hidden = !on);
  $$('[data-auth-action]').forEach(e => e.disabled = !on || (e.id === 'up-publish' && !uploadReady()));
  $('#conn-title').textContent = on ? 'Connected to Contoso Ltd' : 'Not connected';
  $('#conn-sub').textContent = on ? 'Token holds DeviceManagementApps.ReadWrite.All and Group.ReadWrite.All' : 'Sign in with the app registration to browse and publish.';
}
$('#pill').addEventListener('click', () => {
  if (state.signedIn) { state.signedIn = false; toast('Signed out of Contoso Ltd'); show(); }
  else location.hash = '#settings';
});

/* ── Applications ────────────────────────── */
function visibleApps() {
  const q = state.query.trim().toLowerCase();
  const list = APPS.filter(a => !q || a.name.toLowerCase().includes(q) || a.pub.toLowerCase().includes(q));
  return list.sort((a, b) => state.sort === 'name' ? a.name.localeCompare(b.name) : b.updated.localeCompare(a.updated));
}
function renderApps(loading = false) {
  const list = visibleApps();
  const pages = Math.max(1, Math.ceil(list.length / PAGE));
  state.page = Math.min(state.page, pages - 1);
  const slice = list.slice(state.page * PAGE, state.page * PAGE + PAGE);
  const body = $('#apps-rows');
  body.innerHTML = loading
    ? Array.from({ length: 8 }, () => `<tr class="skel"><td><span style="width:60%"></span></td><td><span style="width:70%"></span></td><td><span style="width:40%"></span></td><td><span style="width:60%"></span></td><td></td></tr>`).join('')
    : slice.map(a => `<tr data-id="${a.id}" class="${a === state.sel ? 'sel' : ''}" tabindex="0">
        <td><div class="nm"><span class="tile" style="background:${a.tile}">${a.name[0]}</span>${a.name}</div></td>
        <td>${a.pub}</td><td class="mono">${a.ver}</td><td>${fmtDate(a.updated)}</td>
        <td><a class="btn icon mini open" style="width:30px" href="#app-detail" title="Open details" aria-label="Open details for ${a.name}">${icon('chevr')}</a></td></tr>`).join('');
  const none = !loading && list.length === 0 && state.signedIn;
  $('#apps-nomatch').hidden = !none;
  $('.apps').style.display = none ? 'none' : '';
  if (none) $('#nomatch-text').textContent = `Nothing in Contoso Ltd matches “${state.query}”. Search looks at application and publisher names.`;
  $('#searchbox').classList.toggle('has', !!state.query);
  $('#sort-name').classList.toggle('on', state.sort === 'name');
  $('#sort-updated').classList.toggle('on', state.sort === 'updated');
  const label = $('#page-label');
  if (label) {
    label.textContent = list.length ? `${state.page * PAGE + 1}–${state.page * PAGE + slice.length} of ${list.length}` : '0 of 0';
    $('#prev').disabled = state.page === 0;
    $('#next').disabled = state.page >= pages - 1;
    $('#apps-hint').textContent = state.signedIn ? 'Double-click a row to open it.' : 'Sign in to browse the applications in your tenant.';
  }
  renderAppRail();
}
function renderAppRail() {
  const rail = $('#apps-rail');
  if (!state.signedIn) { rail.innerHTML = `<div class="rail-empty">${icon('shield', 'i')}<p>Not connected to Microsoft Intune.</p></div>`; return; }
  const a = state.sel;
  if (!a) { rail.innerHTML = `<div class="rail-empty"><p>Select an application to see its details.</p></div>`; return; }
  rail.innerHTML = `
    <div class="rail-head" style="display:flex;gap:14px;align-items:center">
      <span class="tile lg" style="background:${a.tile};width:48px;height:48px;font-size:19px">${a.name[0]}</span>
      <div style="min-width:0"><h2>${a.name}</h2><p>${a.pub} · ${a.ctx} context</p></div></div>
    <div class="cells">
      <div class="cell"><div class="k">Version</div><div class="v mono" style="font-size:15px">${a.ver}</div></div>
      <div class="cell"><div class="k">Updated</div><div class="v">${fmtDate(a.updated)}</div></div>
      <div class="cell wide"><div class="k">Targeted devices</div><div class="v num">${a.targeted.toLocaleString('en-US')}</div>${bar(a)}</div>
      ${statCells(a)}
    </div>
    <div class="rail-foot">
      <a class="btn primary" href="#app-detail">Open details</a>
      <button class="btn" data-toast="Opened ${a.name} in the Intune admin center" title="Open this app in the Intune admin center">${icon('external')}View in Intune</button>
    </div>`;
}
const SEGS = [['installed', 'var(--ok)', 'Installed'], ['pending', '#C9A227', 'Pending'], ['failed', 'var(--bad)', 'Failed'], ['notInstalled', 'var(--faint)', 'Not installed']];
const bar = a => `<div class="bar">${SEGS.map(([k, c]) => a[k] ? `<span style="flex:${a[k]};background:${c}"></span>` : '').join('')}</div>`;
const statCells = a => SEGS.map(([k, c, l]) => `<div class="cell"><div class="k"><i style="background:${c}"></i>${l}</div><div class="v num">${a[k].toLocaleString('en-US')}</div></div>`).join('');

$('#apps-rows').addEventListener('click', e => {
  const tr = e.target.closest('tr[data-id]'); if (!tr) return;
  state.sel = APPS.find(a => a.id === tr.dataset.id);
  $$('#apps-rows tr').forEach(r => r.classList.toggle('sel', r === tr));
  renderAppRail();
});
$('#apps-rows').addEventListener('dblclick', e => { if (e.target.closest('tr[data-id]')) location.hash = '#app-detail'; });
$('#apps-rows').addEventListener('keydown', e => {
  const tr = e.target.closest('tr[data-id]'); if (!tr) return;
  if (e.key === 'Enter') { tr.click(); location.hash = '#app-detail'; }
  if (e.key === ' ') { e.preventDefault(); tr.click(); }
});
$('#search').addEventListener('input', e => { state.query = e.target.value; state.page = 0; renderApps(); });
const clearSearch = () => { state.query = ''; $('#search').value = ''; renderApps(); $('#search').focus(); };
$('#search-clear').addEventListener('click', clearSearch);
$('#nomatch-clear').addEventListener('click', clearSearch);
$('#sort-name').addEventListener('click', () => { state.sort = 'name'; renderApps(); });
$('#sort-updated').addEventListener('click', () => { state.sort = 'updated'; renderApps(); });
$('#refresh').addEventListener('click', () => { renderApps(true); setTimeout(() => { renderApps(); toast('Reloaded 14 applications from Intune'); }, 1000); });

/* ── Application detail ──────────────────── */
function renderDetail() {
  const a = state.sel || APPS[0];
  $('#d-crumb').textContent = a.name;
  $('#d-name').textContent = a.name;
  $('#d-tile').textContent = a.name[0];
  $('#d-tile').style.background = a.tile;
  $('#d-meta').textContent = `${a.pub} · ${a.ver} · ${a.ctx} context · Win32`;
  $('#d-info').innerHTML = `
    <dt>Name</dt><dd>${a.name}</dd><dt>Publisher</dt><dd>${a.pub}</dd><dt>Version</dt><dd class="mono">${a.ver}</dd>
    <dt>Description</dt><dd>${a.name} ${a.ver}, packaged with PSADT v4 and published from Packman.</dd>
    <dt>Install context</dt><dd>${a.ctx}</dd><dt>App type</dt><dd>Windows app (Win32)</dd>
    <dt>Last updated</dt><dd>${fmtDate(a.updated)}</dd>`;
  $('#detail-rail').innerHTML = `
    <div class="rail-head"><h2>Deployment status</h2><p>${state.signedIn ? 'Live from Intune' : 'Last synced Oct 27, 09:12'}</p></div>
    <div class="cells">
      <div class="cell wide"><div class="k">Targeted devices</div><div class="v num">${a.targeted.toLocaleString('en-US')}</div>${bar(a)}</div>
      ${statCells(a)}
      <div class="cell wide"><div class="k"><i style="background:var(--line)"></i>Not applicable</div><div class="v num" style="font-size:22px;margin-top:12px">${a.na}</div></div>
    </div>
    <div class="rail-sec">Intune publishing</div>
    <div class="cells" style="margin-top:12px">
      <div class="cell"><div class="k">State</div><div class="v"><span class="chip ok">Published</span></div></div>
      <div class="cell"><div class="k">Published</div><div class="v">${fmtDate(a.updated)}</div></div>
      <div class="cell wide"><div class="k">App ID</div><div class="v mono" style="display:flex;gap:8px;align-items:center">${a.id}<button class="btn icon mini" style="width:26px;height:26px" data-copy title="Copy full App ID" aria-label="Copy full App ID">${icon('copy')}</button></div></div>
    </div>
    <div class="rail-foot"><button class="btn primary" data-toast="Opened ${a.name} in the Intune admin center">${icon('external')}View in Intune</button></div>`;
}

/* ── Generic controls (delegated, so dock contents work after cloning) ── */
document.addEventListener('click', e => {
  const t = e.target;
  const tab = t.closest('.tab[data-tab]');
  if (tab) {
    const sec = tab.closest('section');
    $$('.tab', sec).forEach(x => x.classList.toggle('on', x === tab));
    $$('[data-pane]', sec).forEach(p => p.hidden = p.dataset.pane !== tab.dataset.tab);
  }
  const segBtn = t.closest('.seg button');
  if (segBtn) {
    $$('button', segBtn.parentElement).forEach(b => b.classList.toggle('on', b === segBtn));
    const seg = segBtn.parentElement.id;
    if (seg === 'mode-seg') $$('[data-mode-pane]').forEach(p => p.hidden = p.dataset.modePane !== segBtn.dataset.mode);
    if (seg === 'det-seg') { $$('[data-det-pane]').forEach(p => p.hidden = p.dataset.detPane !== segBtn.dataset.det); $('#sum-det').textContent = segBtn.textContent.replace(' product code', ''); }
    if (seg === 'deploy-seg') {
      const m = segBtn.textContent, flag = m === 'Auto' ? '' : ` -DeployMode ${m}`;
      $('#cmd-i').textContent = `Invoke-AppDeployToolkit.exe -DeploymentType Install${flag}`;
      $('#cmd-u').textContent = `Invoke-AppDeployToolkit.exe -DeploymentType Uninstall${flag}`;
      $('#sum-mode').textContent = m;
    }
    if (seg === 'theme-seg') { document.documentElement.dataset.theme = segBtn.dataset.themeSet; toast(`Switched to the ${segBtn.dataset.themeSet} theme`); }
  }
  if (t.closest('[data-copy]')) toast('Copied to clipboard');
  const tb = t.closest('[data-toast]'); if (tb) toast(tb.dataset.toast);
  const tg = t.closest('.toggle'); if (tg) { tg.classList.toggle('on'); tg.setAttribute('aria-pressed', tg.classList.contains('on')); }
  const br = t.closest('[data-browse]');
  if (br) { $('#' + br.dataset.browse).value = br.dataset.value; if (br.dataset.after === 'upload-ready') { state.uploadFolder = true; renderUpload(); } }
  const rm = t.closest('[data-remove]');
  if (rm) { const row = rm.closest('.rowi'); toast(row.closest('#rules') ? 'Detection rule deleted in Intune' : 'Assignment removed in Intune'); row.remove(); }
  const act = t.closest('[data-act]'); if (act) ACTIONS[act.dataset.act]();

  switch (t.closest('[id]')?.id) {
    case 'prev': state.page--; renderApps(); break;
    case 'next': state.page++; renderApps(); break;
    case 'generate':
      if (state.generated) { location.hash = '#configure'; break; }
      t.closest('button').textContent = 'Generating…';
      setTimeout(() => { state.generated = true; renderCreate(); toast('Package generated on \\\\fs01\\packages'); }, 900);
      break;
    case 'rt-install': runTest('Install'); break;
    case 'rt-uninstall': runTest('Uninstall'); break;
    case 'rt-discover': discover(); break;
    case 'ping': $('#rt-ping').textContent = 'Online · 4 ms'; toast('PC-ENG-0142 answered in 4 ms'); break;
    case 'clear-out': $('#console').innerHTML = '<span class="c">Output from the device appears here when a test runs.</span>'; $('#det-found').hidden = true; break;
    case 'save-script': saveScript(); break;
    case 'save-settings': $('#settings-hint').textContent = `Saved at ${now()}.`; $('#settings-hint').classList.add('saved'); toast('Settings saved'); break;
    case 'reset-settings': $('#settings-hint').textContent = 'Reverted to the last saved settings.'; $('#settings-hint').classList.remove('saved'); break;
    case 'test-conn': testConnection(); break;
    case 'signin-btn': state.signedIn = true; applyAuth(); toast('Signed in to Contoso Ltd'); break;
    case 'up-addrule': addUploadRule(); break;
  }
});
document.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key === 's' && location.hash === '#edit-script') { e.preventDefault(); saveScript(); }
  if (e.key === 'Escape') closeDialog();
});

/* ── Create ──────────────────────────────── */
function renderCreate() {
  $('#generated').hidden = !state.generated;
  const g = $('#generate'); if (!g) return;
  g.textContent = state.generated ? 'Continue to configure' : 'Generate package';
  $('#create-hint').textContent = state.generated ? 'Package is on the share. Test it now or configure the deployment.' : 'Creates files on your share. Nothing is uploaded yet.';
}

/* ── Upload ──────────────────────────────── */
const uploadReady = () => state.uploadFolder && state.uploadRules > 0;
function renderUpload() {
  if (state.uploadFolder) {
    $('#up-name').value = 'Mozilla Firefox (x64 en-US)'; $('#up-pub').value = 'Mozilla'; $('#up-ver').value = '131.0.3';
    $('#up-help').textContent = 'Read from Application\\Invoke-AppDeployToolkit.ps1. Check the name before publishing.';
  }
  const checks = $$('#up-checks li');
  checks[0].className = state.uploadFolder ? 'ok' : 'off';
  checks[0].innerHTML = `${icon(state.uploadFolder ? 'check' : 'info')}<div class="grow">Package folder<small>${state.uploadFolder ? 'Package ready · 312.4 MB' : 'Not selected'}</small></div>`;
  checks[1].className = state.uploadRules ? 'ok' : 'off';
  checks[1].innerHTML = `${icon(state.uploadRules ? 'check' : 'info')}<div class="grow">Detection rule<small>${state.uploadRules ? `${state.uploadRules} rule${state.uploadRules > 1 ? 's' : ''}` : 'At least one rule is needed'}</small></div>`;
  $('#up-ready-sub').textContent = uploadReady() ? 'Ready to publish.' : state.uploadFolder ? 'Add a detection rule to publish.' : 'Select a package folder to check it for publishing.';
  applyAuth();
}
function addUploadRule() {
  $('#up-norule')?.remove();
  const type = $('#up-rtype').value;
  const text = type === 'File' ? `${$('#up-rpath').value}\\${$('#up-rfile').value} · exists` : type === 'Registry' ? 'HKLM\\SOFTWARE\\Mozilla\\Mozilla Firefox · CurrentVersion = 131.0.3' : '{1A2B3C4D-…} · product code';
  $('#up-rules').insertAdjacentHTML('beforeend', `<div class="rowi"><span class="tag">${type === 'MSI product code' ? 'MSI' : type.toUpperCase()}</span><div class="grow mono">${text}</div><button class="btn icon mini" style="width:28px" aria-label="Remove rule" title="Remove rule" data-up-remove>${icon('trash')}</button></div>`);
  state.uploadRules++; renderUpload();
}
document.addEventListener('click', e => {
  const b = e.target.closest('[data-up-remove]'); if (!b) return;
  b.closest('.rowi').remove(); state.uploadRules--;
  if (!state.uploadRules) $('#up-rules').innerHTML = '<div class="rowi muted" id="up-norule">No detection rule yet. Add one below.</div>';
  renderUpload();
});

/* ── Remote test ─────────────────────────── */
let testTimer;
function runTest(kind) {
  clearInterval(testTimer);
  const out = $('#console'); out.innerHTML = '';
  $('#det-found').hidden = true;
  const mode = 'Silent';
  const lines = [
    ['c', `[15:41:02] Connecting to PC-ENG-0142 over WinRM…`],
    ['', `[15:41:03] Copying package: 86.1 MB → C:\\ProgramData\\Packman\\Staging\\Contoso Reader 4.2.0`],
    ['', `[15:41:09] Copied 3 changed files, 41 unchanged`],
    ['', `[15:41:09] Running Invoke-AppDeployToolkit.exe -DeploymentType ${kind} -DeployMode ${mode} as SYSTEM`],
    ['', `[15:41:10] [Initialization] PSAppDeployToolkit 4.0.5 · Contoso Reader 4.2.0`],
    ['', `[15:41:11] [Pre-${kind}] Closing apps: ContosoReader`],
    ['', `[15:41:12] [${kind}] Executing msiexec.exe /${kind === 'Install' ? 'i' : 'x'} "ContosoReader-4.2.0-x64.msi" REBOOT=ReallySuppress /QN`],
    ['', `[15:41:31] [${kind}] msiexec.exe exited with code 0`],
    ['', `[15:41:32] [Post-${kind}] Completed`],
    ['ok', `[15:41:32] ${kind} finished · exit code 0 (Success)`],
  ];
  $('#rt-status').innerHTML = '<span class="chip amber">Running</span>';
  $('#rt-exit').textContent = '—'; $('#rt-dur').textContent = '—';
  let i = 0;
  testTimer = setInterval(() => {
    const [c, l] = lines[i++];
    out.insertAdjacentHTML('beforeend', `<span class="${c}">${l}</span>\n`);
    out.scrollTop = out.scrollHeight;
    if (i === 3) $('#rt-copy').textContent = '86.1 MB';
    if (i === lines.length) {
      clearInterval(testTimer);
      $('#rt-status').innerHTML = '<span class="chip ok">Succeeded</span>';
      $('#rt-exit').textContent = '0'; $('#rt-dur').innerHTML = '30<span class="of"> s</span>';
      toast(`${kind} succeeded on PC-ENG-0142`);
    }
  }, 260);
}
function discover() {
  $('#console').insertAdjacentHTML('beforeend', `<span>[15:42:10] Searching %ProgramFiles% for the installed executable…</span>\n<span class="ok">[15:42:12] Found Reader.exe · file version 4.2.0.0</span>\n`);
  $('#det-found').hidden = false;
}

/* ── Script editor ───────────────────────── */
$('#src-code').innerHTML = [
  `<span class="cm">&lt;#</span>`, `<span class="cm">.SYNOPSIS  Contoso Reader 4.2.0 — generated by Packman</span>`, `<span class="cm">#&gt;</span>`,
  `<span class="k">param</span> (`, `    [<span class="f">ValidateSet</span>(<span class="s">'Install'</span>, <span class="s">'Uninstall'</span>, <span class="s">'Repair'</span>)]`,
  `    [<span class="f">System.String</span>]<span class="v">$DeploymentType</span> = <span class="s">'Install'</span>,`,
  `    [<span class="f">ValidateSet</span>(<span class="s">'Auto'</span>, <span class="s">'Interactive'</span>, <span class="s">'NonInteractive'</span>, <span class="s">'Silent'</span>)]`,
  `    [<span class="f">System.String</span>]<span class="v">$DeployMode</span> = <span class="s">'Auto'</span>`, `)`, ``,
  `<span class="v">$adtSession</span> = @{`, `    AppVendor = <span class="s">'Contoso'</span>`, `    AppName = <span class="s">'Contoso Reader'</span>`, `    AppVersion = <span class="s">'4.2.0'</span>`, `    AppArch = <span class="s">'x64'</span>`,
  `    AppProcessesToClose = @(<span class="s">'ContosoReader'</span>)`, `    AppScriptVersion = <span class="s">'1.0.0'</span>`, `    AppScriptAuthor = <span class="s">'Workplace engineering'</span>`, `}`, ``,
  `<span class="k">function</span> <span class="f">Install-ADTDeployment</span>`, `{`, `    <span class="v">$adtSession</span>.InstallPhase = <span class="s">"Pre-$($adtSession.DeploymentType)"</span>`,
  `    <span class="f">Show-ADTInstallationWelcome</span> -CloseProcesses <span class="v">$adtSession</span>.AppProcessesToClose -CloseProcessesCountdown 600`,
  ``, `    <span class="v">$adtSession</span>.InstallPhase = <span class="v">$adtSession</span>.DeploymentType`,
  `    <span class="f">Start-ADTMsiProcess</span> -Action Install -FilePath <span class="s">'ContosoReader-4.2.0-x64.msi'</span> -AdditionalArgumentList <span class="s">'ALLUSERS=1 AUTOUPDATE=0'</span>`,
  ``, `    <span class="v">$adtSession</span>.InstallPhase = <span class="s">"Post-$($adtSession.DeploymentType)"</span>`,
  `    <span class="f">Copy-ADTFile</span> -Path <span class="s">"$($adtSession.DirSupportFiles)\\reader-defaults.json"</span> -Destination <span class="s">"$envProgramData\\Contoso\\Reader"</span>`, `}`, ``,
  `<span class="k">function</span> <span class="f">Uninstall-ADTDeployment</span>`, `{`, `    <span class="f">Start-ADTMsiProcess</span> -Action Uninstall -ProductCode <span class="s">'{6F2A3C1E-94B0-4D7E-A1C2-0B5E8F3D7A41}'</span>`, `}`,
].map(l => `<div>${l || ' '}</div>`).join('');
function saveScript() {
  const h = $('#edit-hint');
  if (h) { h.textContent = `Saved at ${now()}. No syntax errors.`; h.classList.add('saved'); }
  toast('Saved Invoke-AppDeployToolkit.ps1');
}

/* ── Settings ────────────────────────────── */
function testConnection() {
  const b = $('#test-conn'); b.textContent = 'Testing…'; b.disabled = true;
  setTimeout(() => {
    b.textContent = 'Test connection'; b.disabled = false;
    if (state.signedIn) toast('Connection works · write permission confirmed');
    else { $('#conn-sub').textContent = 'The token request failed: not signed in. Select Sign in, then test again.'; }
  }, 900);
}

/* ── Dialogs ─────────────────────────────── */
function openDialog(html) { $('#dialog').innerHTML = html; $('#scrim').classList.add('on'); $('#dialog').querySelector('button, input, select')?.focus(); }
function closeDialog() { $('#scrim').classList.remove('on'); clearInterval(pubTimer); }
$('#scrim').addEventListener('click', e => { if (e.target.id === 'scrim' || e.target.closest('[data-close]')) closeDialog(); });

let pubTimer;
function publishFlow(title, steps, done) {
  openDialog(`<h3>${title}</h3><p>You can cancel until content upload starts.</p>
    <ul class="plist">${steps.map(s => `<li><span class="st"></span>${s}<span class="pct"></span></li>`).join('')}</ul>
    <div class="actions"><button class="btn" data-close id="pub-cancel">Cancel</button></div>`);
  const items = $$('.plist li'); let i = 0, pct = 0;
  items[0].classList.add('run');
  pubTimer = setInterval(() => {
    const li = items[i];
    if (li.textContent.startsWith('Upload')) { pct += 20; li.querySelector('.pct').textContent = `${pct}%`; if (pct < 100) return; }
    li.classList.replace('run', 'done'); li.querySelector('.st').innerHTML = icon('check');
    if (++i < items.length) { items[i].classList.add('run'); return; }
    clearInterval(pubTimer);
    $('#dialog').querySelector('p').textContent = done;
    $('#dialog .actions').innerHTML = `<button class="btn" data-toast="Opened in the Intune admin center">${icon('external')}View in Intune</button><button class="btn primary" data-close>Close</button>`;
    toast(done);
  }, 520);
}
const ACTIONS = {
  publish: () => publishFlow('Publishing Contoso Reader 4.2.0', ['Validate package files', 'Sign scripts', 'Build .intunewin', 'Create app in Intune', 'Upload content', 'Commit content', 'Assign 1 group'], 'Published to Intune. Devices pick it up at their next check-in.'),
  'publish-upload': () => publishFlow('Publishing Mozilla Firefox 131.0.3', ['Validate package files', 'Sign scripts', 'Build .intunewin', 'Create app in Intune', 'Upload content', 'Commit content'], 'Published to Intune.'),
  republish: () => publishFlow(`Updating package content`, ['Validate package files', 'Sign scripts', 'Build .intunewin', 'Upload content', 'Commit content'], `${state.sel.name} content updated in Intune.`),
  delete: () => openDialog(`<h3>Delete ${state.sel.name} from Intune?</h3>
    <p>This permanently deletes the app and its 3 assignments. Installed copies stay on devices; use an Uninstall assignment to remove them.</p>
    <div class="actions"><button class="btn" data-close>Cancel</button><button class="btn danger" data-close data-toast="Deleted ${state.sel.name} from Intune">${icon('trash')}Delete from Intune</button></div>`),
  rule: () => openDialog(`<h3>Edit detection rule</h3><p>Saved to Intune as soon as you select Save rule.</p>
    <div class="grid2"><div class="field span2"><label>Rule type</label><div class="seg"><button class="on">MSI</button><button>File</button><button>Registry</button></div></div>
    <div class="field span2"><label>Product code</label><input class="input mono" value="{6F2A3C1E-94B0-4D7E-A1C2-0B5E8F3D7A41}"></div>
    <div class="field"><label>Operator</label><select class="input"><option>Greater than or equal to</option><option>Equals</option></select></div>
    <div class="field"><label>Value</label><input class="input mono" value="4.2.0"></div></div>
    <div class="actions"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="Detection rule saved in Intune">Save rule</button></div>`),
  group: () => openDialog(`<h3>Add assignment</h3><p>Adding an assignment updates Intune immediately.</p>
    <div class="field"><label>Group</label><input class="input" placeholder="Search Entra groups" value="SG-Intune-Engineering-Devices"></div>
    <div class="field" style="margin-top:14px"><label>Intent</label><div class="seg"><button class="on">Required</button><button>Available</button><button>Uninstall</button></div></div>
    <div class="actions"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="Assigned to SG-Intune-Engineering-Devices as Required">Add assignment</button></div>`),
  bulk: () => { $('#bulk-results').hidden = false; $('#adv-members').textContent = '99'; toast('Added 3 devices to SG-Intune-Finance-Laptops'); },
};

/* ── Toast ───────────────────────────────── */
let toastTimer;
function toast(text) {
  $('#toast-text').textContent = text;
  $('#toast').classList.add('on');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => $('#toast').classList.remove('on'), 2400);
}

show();
