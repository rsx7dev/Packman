const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const app = $("#app");

/* ---------- sample data ---------- */
// name, publisher, version, updated, failed installs, targeted devices
const CAT = { "7-Zip":"Utilities", "Citrix Workspace":"Remote access", "Docker Desktop":"Developer tools", "FileZilla":"Utilities", "FortiClient VPN":"Security", "Git for Windows":"Developer tools", "Google Chrome Enterprise":"Browsers", "Greenshot":"Utilities", "KeePassXC":"Security", "Mozilla Firefox ESR":"Browsers", "OpenVPN Connect":"Security", "PuTTY":"Remote access", "Python":"Developer tools", "Slack":"Collaboration", "TeamViewer Host":"Remote access", "VLC media player":"Media", "Visual Studio Code":"Developer tools", "WinSCP":"Utilities", "Wireshark":"Developer tools", "Zoom Workplace":"Collaboration" };
const APPS = [
  ["7-Zip","Igor Pavlov","24.09","2026-10-21",0,64],
  ["Adobe Acrobat Reader","Adobe","24.005.20414","2026-10-19",1,312],
  ["Citrix Workspace","Citrix Systems","24.9.10.23","2026-10-17",7,148],
  ["Contoso Reader","Contoso","4.2.0","2026-10-24",3,120],
  ["Docker Desktop","Docker Inc.","4.36.0","2026-09-30",0,22],
  ["FileZilla","Tim Kosse","3.68.1","2026-08-12",0,18],
  ["FortiClient VPN","Fortinet","7.4.0.1658","2026-10-22",12,410],
  ["Git for Windows","The Git Development Community","2.47.1","2026-10-08",0,96],
  ["Google Chrome Enterprise","Google LLC","131.0.6778.205","2026-10-26",2,640],
  ["Greenshot","Greenshot","1.3.296","2026-07-03",0,41],
  ["KeePassXC","KeePassXC Team","2.7.9","2026-09-14",0,57],
  ["Mozilla Firefox ESR","Mozilla","128.5.0","2026-10-15",0,203],
  ["Notepad++","Notepad++ Team","8.7.4","2026-10-18",0,288],
  ["OpenVPN Connect","OpenVPN Inc.","3.5.1","2026-08-29",0,35],
  ["PuTTY","Simon Tatham","0.82","2026-06-21",0,26],
  ["Python","Python Software Foundation","3.13.1","2026-10-02",0,44],
  ["Slack","Slack Technologies","4.41.97","2026-10-11",1,180],
  ["SumatraPDF","Krzysztof Kowalczyk","3.5.2","2026-07-19",0,72],
  ["TeamViewer Host","TeamViewer","15.60.3","2026-10-09",0,31],
  ["VLC media player","VideoLAN","3.0.21","2026-09-22",0,109],
  ["Visual Studio Code","Microsoft","1.96.2","2026-10-23",0,167],
  ["WinSCP","Martin Prikryl","6.3.6","2026-10-13",0,38],
  ["Wireshark","Wireshark Foundation","4.4.2","2026-09-05",0,12],
  ["Zoom Workplace","Zoom Video Communications","6.3.0","2026-10-25",5,520],
  ["Zotero","Corporation for Digital Scholarship","7.0.11","2026-08-01",0,15],
  ["draw.io Desktop","JGraph","25.0.2","2026-10-06",0,63],
].map(([name, pub, ver, upd, fail, total], i) => {
  const pend = Math.ceil(total * .06), na = Math.round(total * .02), not = Math.round(total * .025);
  return { id: i, name, pub, ver, upd, fail, total, pend, na, not, inst: total - fail - pend - na - not,
    key: (0x3f9c61a2 + i * 7919791).toString(16).slice(0, 8),
    cat: CAT[name] || "Productivity" };
});
const fmt = d => new Date(d + "T12:00").toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" });
const PAGE = 10;
let selected = APPS[3], page = 0, sortKey = "name", sortDir = 1, query = "", refreshes = 0;

/* ---------- router ---------- */
let lastNav = "applications";
function route() {
  const id = location.hash.slice(1);
  const screens = $$("[data-screen]");
  const cur = screens.find(s => s.dataset.screen === id) || screens[0];
  screens.forEach(s => s.classList.toggle("active", s === cur));
  let nav = cur.dataset.nav;
  if (cur.dataset.screen === "edit-script") nav = lastNav;
  else lastNav = nav;
  $$(".nav a, .menubar a").forEach(a => a.dataset.nav === nav ? a.setAttribute("aria-current", "page") : a.removeAttribute("aria-current"));
  $("#crumb").textContent = cur.dataset.title;
  document.title = `Packman — ${cur.dataset.title}`;
  $$(".frame.rail-open").forEach(f => f.classList.remove("rail-open"));
  $(".body", cur)?.scrollTo(0, 0);
  if (cur.dataset.screen === "application-detail") renderDetail();
  if (cur.dataset.screen === "edit-script") {
    const back = $("#es-back-1"), from = lastNav === "create" ? "create" : "applications";
    back.href = "#" + (from === "create" ? "create" : "application-detail");
    back.textContent = from === "create" ? "Create package" : "Applications";
  }
  parent.postMessage({ orchScreen: cur.dataset.screen }, "*");
}
addEventListener("hashchange", route);
$$("[data-from]").forEach(a => a.addEventListener("click", () => { lastNav = a.dataset.from; }));

/* ---------- toast ---------- */
let toastTimer;
function toast(msg) {
  $("#toast-t").textContent = msg;
  $("#toast").classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $("#toast").classList.remove("show"), 2400);
}
document.addEventListener("click", e => {
  const t = e.target.closest("[data-toast]");
  if (t) toast(t.dataset.toast);
  const c = e.target.closest("[data-copy]");
  if (c) toast(`${c.dataset.copy} copied`);
  const r = e.target.closest("[data-rail-toggle]");
  if (r) r.closest("[data-screen]").querySelector(".frame").classList.toggle("rail-open");
});

/* ---------- connection ---------- */
function setConn(on, silent) {
  app.dataset.conn = on ? "on" : "off";
  $$("[data-needs-conn]").forEach(b => on ? (b.removeAttribute("disabled")) : b.setAttribute("disabled", ""));
  $$(".when-on-i").forEach(el => el.textContent = on ? "contoso.onmicrosoft.com" : "—");
  $("#auth-btn").textContent = on ? "Sign out" : "Sign in";
  $("#rd-conn").textContent = on ? "connected" : "not connected";
  $("#rd-dot").style.background = on ? "var(--ok)" : "var(--faint)";
  $$(".rd2").forEach(d => d.style.background = on ? "var(--ok)" : "var(--faint)");
  $$(".rd2-t").forEach(d => d.textContent = on ? "connected" : "not connected");
  $("#conn").setAttribute("aria-label", on ? "Connected to contoso.onmicrosoft.com. Click to sign out" : "Not connected. Click to sign in");
  renderApps();
  syncCreate();
  if (!silent) toast(on ? "Connected to contoso.onmicrosoft.com" : "Signed out");
}
$("#conn").addEventListener("click", () => setConn(app.dataset.conn === "off"));
document.addEventListener("click", e => {
  const s = e.target.closest("[data-signin]");
  if (!s) return;
  setConn(s.id === "auth-btn" ? app.dataset.conn === "off" : true);
});

/* ---------- applications ---------- */
function filtered() {
  const q = query.trim().toLowerCase();
  const rows = APPS.filter(a => !q || `${a.name} ${a.pub} ${a.ver}`.toLowerCase().includes(q));
  return rows.sort((a, b) => sortKey === "updated" ? sortDir * a.upd.localeCompare(b.upd) : sortDir * a.name.localeCompare(b.name));
}
function renderApps(loading) {
  const on = app.dataset.conn === "on", rows = filtered();
  const pages = Math.max(1, Math.ceil(rows.length / PAGE));
  page = Math.min(page, pages - 1);
  const slice = rows.slice(page * PAGE, page * PAGE + PAGE);
  const body = $("#apps-body");
  $(".apps").hidden = !on || (!loading && !rows.length);
  $("#apps-offline").hidden = on;
  $("#apps-empty").hidden = !on || loading || !!rows.length;
  $(".toolbar").style.opacity = on ? 1 : .5;
  $("#search").disabled = !on; $("#refresh").disabled = !on;
  $("#apps-foot").style.visibility = on && rows.length ? "visible" : "hidden";
  if (!rows.length) $("#apps-empty-text").textContent = `Nothing matches “${query.trim()}”. Check the spelling or search by publisher or version.`;
  if (loading) {
    body.innerHTML = Array.from({ length: 8 }, () => `<tr class="skel"><td><i style="width:62%"></i></td><td><i style="width:48%"></i></td><td><i style="width:56%"></i></td><td><i style="width:60%"></i></td></tr>`).join("");
    return;
  }
  body.innerHTML = slice.map(a => `
    <tr tabindex="0" data-id="${a.id}" aria-selected="${a === selected}">
      <td><div class="name"><div class="tile sm">${a.name[0].toUpperCase()}</div><div style="min-width:0"><b>${a.name}</b>${a.fail >= 5 ? `<small><svg class="i sm"><use href="#i-alert"/></svg>${a.fail} failed installs</small>` : ""}</div></div></td>
      <td class="muted">${a.pub}</td><td class="mono">${a.ver}</td><td class="muted">${fmt(a.upd)}</td>
    </tr>`).join("");
  const from = rows.length ? page * PAGE + 1 : 0;
  $("#range").textContent = `${from}–${page * PAGE + slice.length} of ${rows.length}`;
  $("#pagedisp").textContent = `Page ${page + 1} of ${pages}`;
  $("#prev").disabled = page === 0; $("#next").disabled = page >= pages - 1;
  $("#prev").style.opacity = page === 0 ? .4 : 1; $("#next").style.opacity = page >= pages - 1 ? .4 : 1;
  $$("th[aria-sort]").forEach(th => th.removeAttribute("aria-sort"));
  $(`[data-sort="${sortKey}"]`).closest("th").setAttribute("aria-sort", sortDir > 0 ? "ascending" : "descending");
  renderRail();
}
function renderRail() {
  const visible = app.dataset.conn === "on" && filtered().includes(selected);
  $("#rail-empty").hidden = visible; $("#rail-body").hidden = !visible;
  if (!visible) return;
  const a = selected;
  $("#r-tile").textContent = a.name[0].toUpperCase();
  $("#r-name").textContent = a.name; $("#r-pub").textContent = a.pub;
  $("#r-chips").innerHTML = `<span class="chip">Win32 (.intunewin)</span><span class="chip ok"><span class="d"></span>Published</span>${a.fail >= 5 ? `<span class="chip bad">${a.fail} failed</span>` : ""}`;
  $("#r-kv").innerHTML = `<div><dt>Version</dt><dd class="mono">${a.ver}</dd></div><div><dt>Category</dt><dd>${a.cat}</dd></div><div><dt>Updated</dt><dd>${fmt(a.upd)}</dd></div><div><dt>App ID</dt><dd class="mono">${a.key}</dd></div>`;
}
function select(id, open) {
  selected = APPS[id];
  $$("#apps-body tr").forEach(r => r.setAttribute("aria-selected", r.dataset.id == id));
  renderRail();
  if (open) location.hash = "application-detail";
}
$("#apps-body").addEventListener("click", e => { const r = e.target.closest("tr[data-id]"); if (r) select(+r.dataset.id); });
$("#apps-body").addEventListener("dblclick", e => { const r = e.target.closest("tr[data-id]"); if (r) select(+r.dataset.id, true); });
$("#apps-body").addEventListener("keydown", e => {
  const r = e.target.closest("tr[data-id]"); if (!r) return;
  if (e.key === "Enter") select(+r.dataset.id, true);
  if (e.key === " ") { e.preventDefault(); select(+r.dataset.id); }
  const sib = e.key === "ArrowDown" ? r.nextElementSibling : e.key === "ArrowUp" ? r.previousElementSibling : null;
  if (sib) { e.preventDefault(); sib.focus(); select(+sib.dataset.id); }
});
$("#search").addEventListener("input", e => { query = e.target.value; page = 0; renderApps(); });
$("#clear-search").addEventListener("click", () => { query = ""; $("#search").value = ""; renderApps(); $("#search").focus(); });
$("#prev").addEventListener("click", () => { page--; renderApps(); });
$("#next").addEventListener("click", () => { page++; renderApps(); });
$$("[data-sort]").forEach(b => b.addEventListener("click", () => {
  sortDir = sortKey === b.dataset.sort ? -sortDir : 1; sortKey = b.dataset.sort; renderApps();
}));
function refresh() {
  $("#apps-error").hidden = true;
  renderApps(true);
  $("#refresh").disabled = true;
  setTimeout(() => {
    $("#refresh").disabled = false;
    refreshes++;
    if (refreshes % 2 === 0) { $("#apps-error").hidden = false; renderApps(); return; } // every second refresh shows the error state
    $("#refreshed").textContent = "Last refreshed 14:41";
    renderApps(); toast("Refreshed 26 applications");
  }, 1100);
}
$("#refresh").addEventListener("click", refresh);
$("#retry").addEventListener("click", refresh);
addEventListener("keydown", e => {
  if (e.key === "/" && location.hash.slice(1) !== "" && location.hash.slice(1) !== "applications") return;
  if (e.key === "/" && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName) && $("[data-screen=applications]").classList.contains("active")) { e.preventDefault(); $("#search").focus(); }
});

/* ---------- application detail ---------- */
const STATUS = [["inst","Installed","var(--ok)"],["pend","Pending","#D9A93A"],["fail","Failed","var(--bad)"],["not","Not installed","var(--faint)"],["na","Not applicable","var(--line-strong)"]];
function renderDetail() {
  const a = selected;
  $("#d-tile").textContent = a.name[0].toUpperCase();
  $("#d-name").textContent = a.name; $("#d-crumb").textContent = a.name;
  $("#d-meta").textContent = `${a.pub} · v${a.ver} · System context · Win32`;
  $("#d-info").innerHTML = [["Publisher",a.pub],["Version",`<span class="mono">${a.ver}</span>`],["Category",a.cat],["Owner","Endpoint engineering"],["Type","Win32 (.intunewin)"],["Size","48.2 MB"],["Install context","System"],["Last modified",fmt(a.upd)]]
    .map(([k, v]) => `<div><dt>${k}</dt><dd>${v}</dd></div>`).join("");
  $("#d-desc").textContent = `${a.name} ${a.ver} from ${a.pub}, wrapped with PSADT v4 for a silent, system-context install. Detection checks the installed version, so a newer build supersedes this one without a reinstall prompt.`;
  $("#s-total").textContent = a.total;
  $("#s-bar").innerHTML = STATUS.map(([k, , c]) => `<i style="flex:${a[k]};background:${c}${a[k] ? "" : ";display:none"}"></i>`).join("");
  $("#s-legend").innerHTML = STATUS.map(([k, l, c]) => `<div><span class="sw" style="background:${c}"></span>${l}<b>${a[k]}</b></div>`).join("");
  $("#s-id").textContent = a.key; $("#s-pub").textContent = fmt(a.upd);
  $("#dlg h2").textContent = `Delete ${a.name} from Intune?`;
}
function showTab(name) {
  $$("[data-tab]").forEach(t => t.setAttribute("aria-selected", t.dataset.tab === name));
  $$("[data-panel]").forEach(p => p.hidden = p.dataset.panel !== name);
  $("#detail-body").scrollTo(0, 0);
}
$$("[data-tab]").forEach(t => t.addEventListener("click", () => showTab(t.dataset.tab)));
$$("[data-goto-tab]").forEach(b => b.addEventListener("click", () => showTab(b.dataset.gotoTab)));
$("#republish").addEventListener("click", () => toast("Republishing content… new package uploaded"));
$("#delete").addEventListener("click", () => $("#dlg").showModal());
$("#dlg").addEventListener("close", e => {
  if ($("#dlg").returnValue !== "ok") return;
  const i = APPS.indexOf(selected); APPS.splice(i, 1); selected = APPS[Math.min(i, APPS.length - 1)];
  location.hash = "applications"; renderApps(); toast("Deleted from Intune");
});

/* ---------- create ---------- */
let step = 1, generated = false;
const HINTS = ["Creates files on your share. Nothing is uploaded yet.", "Nothing is sent to Intune until you publish.", "Publishing uploads content and creates the app in your tenant."];
function syncCreate() {
  const on = app.dataset.conn === "on";
  $$("[data-step-panel]").forEach(p => p.hidden = +p.dataset.stepPanel !== step);
  $$("#stepper .step").forEach(s => { const n = +s.dataset.step; n === step ? s.setAttribute("aria-current", "step") : s.removeAttribute("aria-current"); s.classList.toggle("done", n < step); });
  $("#c-back").disabled = step === 1;
  $("#c-hint").textContent = step === 3 && !on ? "Publish to Intune is off until a tenant is connected." : HINTS[step - 1];
  const next = $("#c-next");
  next.textContent = step === 1 ? (generated ? "Continue to configure" : "Generate package") : step === 2 ? "Continue to review" : "Publish to Intune";
  next.disabled = step === 3 && !on;
  $("#generated").hidden = !(generated && step === 1);
  $("#pkg-state").textContent = generated ? "package ready" : "no package yet";
  $("#create-body").scrollTo(0, 0);
}
$("#c-next").addEventListener("click", () => {
  if (step === 1 && !generated) {
    const b = $("#c-next"); b.disabled = true; b.textContent = "Generating…";
    setTimeout(() => { generated = true; b.disabled = false; syncCreate(); toast("Package generated on the share"); }, 900);
    return;
  }
  if (step < 3) { step++; syncCreate(); return; }
  publish();
});
$("#c-back").addEventListener("click", () => { step--; syncCreate(); });
$$("#stepper .step").forEach(s => s.addEventListener("click", () => { step = +s.dataset.step; syncCreate(); }));
$$("[data-step-go]").forEach(a => a.addEventListener("click", () => { step = +a.dataset.stepGo; syncCreate(); }));
$$("[data-mode]").forEach(b => b.addEventListener("click", () => {
  $$("[data-mode]").forEach(x => x.setAttribute("aria-pressed", x === b));
  toast(b.dataset.mode === "upgrade" ? "Upgrade existing: pick the app to supersede" : "Create new package");
}));
[["#an", "#pv-app"], ["#mf", "#pv-pub"], ["#vr", "#pv-ver"]].forEach(([i, o]) => $(i).addEventListener("input", e => $(o).textContent = e.target.value || "—"));
function publish() {
  const labels = ["Validating launcher and manifest", "Signing the script", "Building the .intunewin", "Uploading content", "Creating the app and assignment"];
  $("#pub-run").hidden = false;
  $("#pub-steps").innerHTML = labels.map(l => `<div><span class="st"></span>${l}</div>`).join("");
  $("#c-next").disabled = true;
  const rows = $$("#pub-steps > div"); let i = 0;
  const tick = () => {
    if (i > 0) { rows[i - 1].className = "done"; rows[i - 1].firstChild.innerHTML = '<svg class="i sm"><use href="#i-check"/></svg>'; }
    if (i < rows.length) { rows[i].className = "run"; $("#pub-bar").style.width = (i + 1) / rows.length * 100 + "%"; i++; setTimeout(tick, 650); }
    else { toast("Published Contoso Reader 4.2.0 to Intune"); $("#c-next").textContent = "Published"; }
  };
  tick();
}

/* ---------- upload / remote test / advanced / settings ---------- */
$("#up-publish").addEventListener("click", () => toast("Publishing… content upload started"));
$$(".seg").forEach(seg => { if (!seg.dataset.mode && seg.id !== "theme-seg" && !seg.querySelector("[data-mode]")) seg.addEventListener("click", e => { const b = e.target.closest("button"); if (b) $$("button", seg).forEach(x => x.setAttribute("aria-pressed", x === b)); }); });
$("#rt-pc").addEventListener("change", e => $("#rt-pcv").textContent = e.target.value);
$("#rt-run").addEventListener("click", () => {
  const out = $("#rt-out"), st = $("#rt-status"), pc = $("#rt-pc").value;
  const lines = [`<span class="dim">[14:41:02]</span> Connecting to ${pc}…`, `<span class="dim">[14:41:03]</span> Copying package (46.9 MB)`, `<span class="dim">[14:41:09]</span> Invoke-AppDeployToolkit.exe Install -DeployMode Interactive`, `<span class="dim">[14:41:11]</span> Start-ADTMsiProcess: ContosoReader-4.2.0-x64.msi`, `<span class="dim">[14:41:24]</span> Detection rule matched: version 4.2.0`, `<span class="okc">[14:41:25] Installation completed with exit code 0</span>`];
  out.innerHTML = ""; st.className = "chip accent"; st.textContent = "Running"; $("#rt-run").disabled = true;
  lines.forEach((l, i) => setTimeout(() => {
    out.insertAdjacentHTML("beforeend", l + "\n"); out.scrollTop = out.scrollHeight;
    if (i === lines.length - 1) { st.className = "chip ok"; st.textContent = "Passed"; $("#rt-exit").textContent = "0"; $("#rt-run").disabled = false; }
  }, i * 420));
});
$$("[data-adv]").forEach(t => t.addEventListener("click", () => { $$("[data-adv]").forEach(x => x.setAttribute("aria-selected", x === t)); $$("[data-adv-panel]").forEach(p => p.hidden = p.dataset.advPanel !== t.dataset.adv); }));
$("#pcs").addEventListener("input", e => { const n = e.target.value.split(/[\s,;]+/).filter(Boolean).length; $("#pcn").textContent = `${n} name${n === 1 ? "" : "s"}`; });
$("#bulk-run").addEventListener("click", () => { $("#bulk-res").hidden = false; toast("3 added, 1 already a member, 1 not found"); });
$$("[data-set]").forEach(t => t.addEventListener("click", () => { $$("[data-set]").forEach(x => x.setAttribute("aria-selected", x === t)); $$("[data-set-panel]").forEach(p => p.hidden = p.dataset.setPanel !== t.dataset.set); }));
$("#test-conn").addEventListener("click", () => {
  const r = $("#test-res"), on = app.dataset.conn === "on";
  r.textContent = "Checking…";
  setTimeout(() => { r.style.color = on ? "var(--ok)" : "var(--bad)"; r.textContent = on ? "Connected. Write permission confirmed from the token’s roles." : "Couldn’t sign in. Check the tenant ID and certificate."; }, 700);
});
$("#save-set").addEventListener("click", () => { $("#save-msg").textContent = "All changes saved · just now"; toast("Settings saved"); });
$$("[data-theme-set]").forEach(b => b.addEventListener("click", () => {
  $$("[data-theme-set]").forEach(x => x.setAttribute("aria-pressed", x === b));
  const dark = b.dataset.themeSet === "dark" || (b.dataset.themeSet === "system" && matchMedia("(prefers-color-scheme:dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}));

/* ---------- nav toggle ---------- */
$("#nav-toggle").addEventListener("click", () => $("#app").classList.toggle("nav-hidden"));
addEventListener("keydown", e => { if ((e.ctrlKey || e.metaKey) && e.key === "b") { e.preventDefault(); $("#app").classList.toggle("nav-hidden"); } });

/* ---------- edit script ---------- */
$("#es-code").addEventListener("input", () => $("#es-dirty").hidden = false);
const saveScript = () => { $("#es-dirty").hidden = true; toast("Invoke-AppDeployToolkit.ps1 saved"); };
$("#es-save").addEventListener("click", saveScript);
addEventListener("keydown", e => { if ((e.ctrlKey || e.metaKey) && e.key === "s" && $("[data-screen=edit-script]").classList.contains("active")) { e.preventDefault(); saveScript(); } });
$$(".editor .files button").forEach(b => b.addEventListener("click", () => { $$(".editor .files button").forEach(x => x.setAttribute("aria-current", x === b)); toast(`Opened ${b.textContent}`); }));

setConn(true, true);
route();
