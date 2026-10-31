// PSADT IntelliSense for the script editor: completion, hover, signature help and the
// Functions picker, all fed by psadt-functions.json (see psadt-functions-NOTICE.txt).
// Loaded before Monaco's AMD loader resolves, so nothing here touches `monaco` at top
// level; the providers read it when they run.
(function () {
  let catalog = null;       // parsed JSON, or null while loading / when the fetch failed
  let byName = new Map();   // lower-case name -> function
  let names = [];           // lower-case names, for the prefix test
  let packageVersion = null; // raw ModuleVersion of the open package, or null
  let loading = null;
  const snippets = new Map();

  let editor = null;
  let registered = false;
  const ui = {};            // picker elements, built on first open
  let selected = null;      // name of the highlighted picker row

  // ── Catalog and version ──────────────────────────────────────────────

  function load(url) {
    if (!loading) {
      loading = fetch(url)
        .then(r => { if (!r.ok) throw new Error(r.status); return r.json(); })
        .then(data => {
          catalog = data;
          byName = new Map(data.functions.map(f => [f.name.toLowerCase(), f]));
          names = [...byName.keys()];
        })
        .catch(() => { catalog = null; });
    }
    return loading;
  }

  function setVersion(version) {
    packageVersion = version || null;
  }

  function majorMinor(text) {
    const m = /^(\d+)\.(\d+)/.exec(text || '');
    return m ? [Number(m[1]), Number(m[2])] : null;
  }

  /// 'in_dev' when the package is on the catalog's dev line or newer, 'in_release' below it,
  /// null when either version is unknown (nothing is hidden then).
  function versionLine() {
    const pkg = majorMinor(packageVersion);
    const dev = catalog && majorMinor(catalog.dev);
    if (!pkg || !dev) return null;
    return pkg[0] > dev[0] || (pkg[0] === dev[0] && pkg[1] >= dev[1]) ? 'in_dev' : 'in_release';
  }

  function availability(fn) {
    const line = versionLine();
    if (!line || fn[line]) return 'available';
    return line === 'in_release' ? 'devOnly' : 'removed';
  }

  function badgeText(fn) {
    if (!fn.in_release) return catalog.dev + ' only';
    if (!fn.in_dev) return 'removed in ' + catalog.dev;
    return '';
  }

  function unavailableSentence() {
    return "Not in this package's PSADT " + packageVersion;
  }

  // ── Text helpers ─────────────────────────────────────────────────────

  function shortType(type) {
    if (type.endsWith('SwitchParameter')) return 'switch';
    return type.replace(/\b[A-Za-z_]\w*\./g, '');
  }

  const isSwitch = p => shortType(p.type) === 'switch';

  /// 'required String' / 'String' / 'switch' / 'required switch'; no type text when the catalog has none.
  function parameterDetail(p) {
    return [p.mandatory ? 'required' : '', shortType(p.type)].filter(Boolean).join(' ');
  }

  function orderedParameters(fn) {
    return [...fn.parameters].sort((a, b) => b.mandatory - a.mandatory);
  }

  function signatureLabel(fn) {
    return fn.name + fn.parameters.map(p => {
      const part = '-' + p.name + (isSwitch(p) ? '' : ' <' + (shortType(p.type) || 'value') + '>');
      return ' ' + (p.mandatory ? part : '[' + part + ']');
    }).join('');
  }

  /// Mandatory value parameters common to all parameter sets plus those of the first set, so
  /// the snippet never mixes mutually exclusive parameters. Switches are left for the user.
  function snippetFor(fn) {
    if (snippets.has(fn.name)) return snippets.get(fn.name);
    const firstSet = fn.parameters.flatMap(p => p.parameter_sets)[0];
    let stop = 0;
    const parts = fn.parameters
      .filter(p => p.mandatory && !isSwitch(p) && (p.parameter_sets.length === 0 || p.parameter_sets.includes(firstSet)))
      .map(p => {
        const stopText = '${' + (++stop) + ':' + p.name + '}';
        return '-' + p.name + ' ' + (p.type === 'System.String' ? "'" + stopText + "'" : stopText);
      });
    const text = [fn.name, ...parts].join(' ') + '$0';
    snippets.set(fn.name, text);
    return text;
  }

  function plainText(snippet) {
    return snippet.replace(/\$\{\d+:([^}]*)\}|\$0/g, (_, placeholder) => placeholder || '');
  }

  // ── Where the cursor is ──────────────────────────────────────────────

  /// Whether the cursor sits in a comment or string, where completions only get in the way.
  function inCommentOrString(model, position) {
    const block = model.findPreviousMatch('<#|#>', position, true, false, null, false);
    // A hit that wrapped past the cursor is not before it.
    if (block && block.range.getEndPosition().isBeforeOrEqual(position) && model.getValueInRange(block.range) === '<#') return true;

    const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
    let quote = null;
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (quote) { if (ch === quote) quote = null; }
      else if (ch === "'" || ch === '"') quote = ch;
      else if (ch === '<' && line[i + 1] === '#') {
        const end = line.indexOf('#>', i + 2);
        if (end < 0) return true;
        i = end + 1;
      }
      else if (ch === '#') return true;
    }
    return quote !== null;
  }

  /// The text of the statement up to `column`, following backtick line continuations upward.
  function statementBefore(model, lineNumber, column) {
    let text = model.getLineContent(lineNumber).slice(0, column - 1);
    for (let n = lineNumber - 1, steps = 0; n >= 1 && steps < 50; n--, steps++) {
      const line = model.getLineContent(n);
      if (!line.endsWith('`')) break;
      text = line.slice(0, -1) + ' ' + text;
    }
    return text;
  }

  const withoutStrings = text => text.replace(/'[^']*'|"[^"]*"/g, ' ');

  /// The catalog function whose call ends at `text`, with the argument text after its name.
  /// The last name wins so piped and nested calls resolve to the innermost; a pipe or
  /// semicolon after the name ends the call.
  function callIn(text) {
    const re = /[A-Za-z][A-Za-z0-9]*-[A-Za-z][A-Za-z0-9]*/g;
    let fn = null, end = 0, m;
    while ((m = re.exec(text)) !== null) {
      const hit = byName.get(m[0].toLowerCase());
      if (hit) { fn = hit; end = m.index + m[0].length; }
    }
    if (!fn) return null;
    const tail = withoutStrings(text.slice(end));
    return /[|;]/.test(tail) ? null : { fn, tail };
  }

  const typedParameters = tail => [...tail.matchAll(/(?:^|\s)-([A-Za-z]\w*)/g)].map(m => m[1].toLowerCase());

  function findParameter(fn, name) {
    const lower = name.toLowerCase();
    return fn.parameters.find(p => p.name.toLowerCase() === lower);
  }

  // ── Completion ───────────────────────────────────────────────────────

  function completionsAt(model, position) {
    if (!catalog || inCommentOrString(model, position)) return [];

    const before = statementBefore(model, position.lineNumber, position.column);
    const word = /[-\w]*$/.exec(before)[0];
    if (!word) return [];
    const wordStart = before.length - word.length;
    if (before[wordStart - 1] === '$') return [];

    const range = {
      startLineNumber: position.lineNumber, startColumn: position.column - word.length,
      endLineNumber: position.lineNumber, endColumn: position.column
    };
    const dashed = word[0] === '-';
    const call = dashed ? callIn(before.slice(0, wordStart)) : null;
    if (call) return parameterItems(call, range);

    const lower = word.toLowerCase();
    const rest = dashed ? lower.slice(1) : lower;
    const offersFunctions = dashed
      ? rest !== '' && (rest.startsWith('adt') || 'adt'.startsWith(rest))
      : names.some(n => n.startsWith(lower));
    return offersFunctions ? functionItems(dashed, range) : [];
  }

  function functionItems(dashed, range) {
    const kind = monaco.languages.CompletionItemKind.Function;
    const asSnippet = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;
    return catalog.functions
      .filter(fn => availability(fn) === 'available')
      .map(fn => ({
        label: fn.name,
        kind,
        detail: fn.synopsis,
        insertText: snippetFor(fn),
        insertTextRules: asSnippet,
        filterText: dashed ? '-' + fn.name : undefined,
        range
      }));
  }

  function parameterItems(call, range) {
    const used = new Set(typedParameters(call.tail));
    const kind = monaco.languages.CompletionItemKind.Property;
    return call.fn.parameters
      .filter(p => !used.has(p.name.toLowerCase()))
      .map(p => ({
        label: '-' + p.name,
        kind,
        detail: parameterDetail(p),
        documentation: p.description,
        insertText: '-' + p.name,
        sortText: (p.mandatory ? '0' : '1') + p.name,
        range
      }));
  }

  // ── Hover ────────────────────────────────────────────────────────────

  function hoverAt(model, position) {
    if (!catalog) return null;
    const line = model.getLineContent(position.lineNumber);
    const column = position.column - 1;
    const re = /[A-Za-z][A-Za-z0-9]*-[A-Za-z][A-Za-z0-9]*|-[A-Za-z][A-Za-z0-9]*/g;
    let m;
    while ((m = re.exec(line)) !== null) {
      if (column < m.index || column >= m.index + m[0].length) continue;
      const range = {
        startLineNumber: position.lineNumber, startColumn: m.index + 1,
        endLineNumber: position.lineNumber, endColumn: m.index + m[0].length + 1
      };
      const contents = m[0][0] === '-'
        ? parameterHover(model, position.lineNumber, m.index, m[0].slice(1))
        : functionHover(byName.get(m[0].toLowerCase()));
      return contents && { contents, range };
    }
    return null;
  }

  function functionHover(fn) {
    if (!fn) return null;
    const notes = [fn.description, '[Docs](' + fn.docs_url + ')'];
    if (availability(fn) !== 'available') notes.push('_' + unavailableSentence() + '_');
    return [
      { value: '**' + fn.name + '**\n\n' + fn.synopsis },
      { value: '`' + signatureLabel(fn) + '`\n\n' + notes.join('\n\n') }
    ];
  }

  function parameterHover(model, lineNumber, index, name) {
    const call = callIn(statementBefore(model, lineNumber, index + 1));
    const p = call && findParameter(call.fn, name);
    if (!p) return null;
    const type = [p.type, p.mandatory ? 'required' : ''].filter(Boolean).join(', ');
    return [{ value: '**-' + p.name + '**' + (type ? ' (' + type + ')' : '') + '\n\n' + p.description }];
  }

  // ── Signature help ───────────────────────────────────────────────────

  function signatureAt(model, position) {
    if (!catalog) return null;
    const call = callIn(statementBefore(model, position.lineNumber, position.column));
    if (!call) return null;
    const fn = call.fn;

    // Only a name followed by whitespace counts as typed; the word under the cursor does not.
    const typed = [...call.tail.matchAll(/(?:^|\s)-([A-Za-z]\w*)(?=\s)/g)].map(m => m[1]);
    const last = typed.length ? typed[typed.length - 1].toLowerCase() : '';
    const active = fn.parameters.findIndex(p => p.name.toLowerCase() === last);

    return {
      signatures: [{
        label: signatureLabel(fn),
        documentation: fn.synopsis,
        parameters: fn.parameters.map(p => ({ label: '-' + p.name, documentation: p.description }))
      }],
      activeSignature: 0,
      activeParameter: Math.max(active, 0)
    };
  }

  // ── Picker data ──────────────────────────────────────────────────────

  const byCodeUnit = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

  /// Functions matching `query` (case-insensitive substring of name or synopsis) grouped by verb.
  function search(query) {
    if (!catalog) return [];
    const q = (query || '').trim().toLowerCase();
    const groups = new Map();
    for (const fn of catalog.functions) {
      if (q && !fn.name.toLowerCase().includes(q) && !fn.synopsis.toLowerCase().includes(q)) continue;
      if (!groups.has(fn.verb)) groups.set(fn.verb, []);
      groups.get(fn.verb).push(fn);
    }
    return [...groups.keys()].sort(byCodeUnit).map(verb => ({
      verb,
      functions: groups.get(verb).sort((a, b) => byCodeUnit(a.name, b.name))
    }));
  }

  // ── Picker UI ────────────────────────────────────────────────────────

  const STYLE = `
    #psadt-picker { position: absolute; top: 0; left: 50%; transform: translateX(-50%); display: none; flex-direction: column; box-sizing: border-box; width: min(720px, calc(100vw - 48px));
      height: min(440px, calc(100vh - 64px)); margin-top: 8px; padding: 8px; gap: 8px; z-index: 50;
      font: 13px/1.4 var(--vscode-font-family, 'Segoe UI', sans-serif);
      color: var(--vscode-editorWidget-foreground); background: var(--vscode-editorWidget-background);
      border: 1px solid var(--vscode-editorWidget-border, var(--vscode-widget-border));
      box-shadow: 0 2px 8px var(--vscode-widget-shadow); }
    #psadt-picker.open { display: flex; }
    #psadt-search { box-sizing: border-box; width: 100%; padding: 4px 8px; font: inherit;
      color: var(--vscode-input-foreground); background: var(--vscode-input-background);
      border: 1px solid var(--vscode-input-border, transparent); outline: none; }
    #psadt-search:focus { border-color: var(--vscode-focusBorder); }
    #psadt-body { display: flex; flex: 1; min-height: 0; gap: 8px; }
    #psadt-list { flex: 0 0 42%; margin: 0; padding: 0; overflow-y: auto; list-style: none; }
    #psadt-list li { padding: 2px 8px; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    #psadt-list li.psadt-group { cursor: default; font-weight: 600; padding-top: 6px;
      color: var(--vscode-descriptionForeground); }
    #psadt-list li[role=option]:hover { background: var(--vscode-list-hoverBackground); }
    #psadt-list li[aria-selected=true] { color: var(--vscode-list-activeSelectionForeground);
      background: var(--vscode-list-activeSelectionBackground); }
    #psadt-list li.psadt-unavailable { color: var(--vscode-disabledForeground); }
    .psadt-badge { margin-left: 6px; padding: 0 6px; font-size: 11px; border-radius: 8px;
      color: var(--vscode-badge-foreground); background: var(--vscode-badge-background); }
    .psadt-badge:empty { display: none; }
    #psadt-detail { flex: 1; min-width: 0; overflow-y: auto; }
    #psadt-detail h3 { margin: 0 0 4px; font-size: 13px; }
    #psadt-detail p { margin: 0 0 8px; }
    #psadt-detail ul { margin: 0 0 8px; padding-left: 16px; }
    #psadt-detail .psadt-param-type { color: var(--vscode-descriptionForeground); }
    #psadt-detail a { color: var(--vscode-textLink-foreground); }
    #psadt-detail button { font: inherit; padding: 2px 10px; margin-right: 8px; cursor: pointer;
      color: var(--vscode-button-foreground); background: var(--vscode-button-background); border: none; }
    #psadt-detail button:focus-visible, #psadt-detail a:focus-visible { outline: 1px solid var(--vscode-focusBorder); }
    #psadt-empty { padding: 8px; color: var(--vscode-descriptionForeground); }
    #psadt-empty[hidden] { display: none; }
  `;

  function element(tag, props, ...children) {
    const el = Object.assign(document.createElement(tag), props);
    el.append(...children);
    return el;
  }

  function buildPicker() {
    document.head.append(element('style', { textContent: STYLE }));
    ui.search = element('input', {
      id: 'psadt-search', type: 'text', spellcheck: false, placeholder: 'Search functions'
    });
    ui.search.setAttribute('role', 'combobox');
    ui.search.setAttribute('aria-label', 'Search PSADT functions');
    ui.search.setAttribute('aria-controls', 'psadt-list');
    ui.list = element('ul', { id: 'psadt-list' });
    ui.list.setAttribute('role', 'listbox');
    ui.list.setAttribute('aria-label', 'PSADT functions');
    ui.detail = element('div', { id: 'psadt-detail' });
    ui.empty = element('div', { id: 'psadt-empty', hidden: true });
    ui.root = element('div', { id: 'psadt-picker' },
      ui.search, element('div', { id: 'psadt-body' }, ui.list, ui.detail), ui.empty);
    ui.root.setAttribute('role', 'dialog');
    ui.root.setAttribute('aria-label', 'Insert a PSADT function');

    ui.search.addEventListener('input', renderList);
    ui.search.addEventListener('keydown', onSearchKey);
    // Keep the editor from reacting to keys or wheel turns meant for the picker.
    ui.root.addEventListener('wheel', e => e.stopPropagation());
    ui.root.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'Escape') { e.preventDefault(); closePicker(); }
    });
    ui.list.addEventListener('mousedown', e => e.preventDefault());
    ui.list.addEventListener('click', e => {
      const row = e.target.closest('li[role=option]');
      if (row) select(row.dataset.name);
    });
    ui.list.addEventListener('dblclick', e => {
      const row = e.target.closest('li[role=option]');
      if (row) insertFunction(byName.get(row.dataset.name.toLowerCase()));
    });

    editor.addOverlayWidget({
      getId: () => 'packman.psadtPicker',
      getDomNode: () => ui.root,
      getPosition: () => null
    });
    // Clicks on the picker itself also reach the editor's mouse events; only clicks elsewhere close it.
    editor.onMouseDown(e => { if (!ui.root.contains(e.target.element)) closePicker(); });
  }

  function onSearchKey(e) {
    const rows = [...ui.list.querySelectorAll('li[role=option]')];
    const at = rows.findIndex(r => r.dataset.name === selected);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (rows.length) select(rows[Math.max(0, Math.min(rows.length - 1, at + (e.key === 'ArrowDown' ? 1 : -1)))].dataset.name);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (selected) insertFunction(byName.get(selected.toLowerCase()));
    }
  }

  function renderList() {
    ui.list.replaceChildren();
    const groups = search(ui.search.value);
    const rows = [];
    for (const group of groups) {
      ui.list.append(element('li', { className: 'psadt-group', textContent: group.verb }));
      ui.list.lastChild.setAttribute('role', 'presentation');
      for (const fn of group.functions) {
        const row = element('li', { id: 'psadt-opt-' + fn.name, className: availability(fn) === 'available' ? '' : 'psadt-unavailable' },
          fn.name, element('span', { className: 'psadt-badge', textContent: badgeText(fn) }));
        row.setAttribute('role', 'option');
        row.dataset.name = fn.name;
        row.title = fn.synopsis;
        ui.list.append(row);
        rows.push(row);
      }
    }
    ui.empty.hidden = rows.length > 0;
    ui.empty.textContent = catalog ? 'No functions match' : 'Function catalog not available';
    const keep = rows.some(r => r.dataset.name === selected);
    select(keep ? selected : rows.length ? rows[0].dataset.name : null);
  }

  function select(name) {
    selected = name;
    for (const row of ui.list.querySelectorAll('li[role=option]')) {
      const on = row.dataset.name === name;
      row.setAttribute('aria-selected', String(on));
      if (on) {
        row.scrollIntoView({ block: 'nearest' });
        ui.search.setAttribute('aria-activedescendant', row.id);
      }
    }
    if (!name) ui.search.removeAttribute('aria-activedescendant');
    renderDetail(name && byName.get(name.toLowerCase()));
  }

  function renderDetail(fn) {
    ui.detail.replaceChildren();
    if (!fn) return;
    ui.detail.append(element('h3', { textContent: fn.name }), element('p', { textContent: fn.synopsis }));
    if (availability(fn) !== 'available')
      ui.detail.append(element('p', { textContent: unavailableSentence() }));
    if (fn.parameters.length) {
      ui.detail.append(element('ul', {}, ...orderedParameters(fn).map(p => element('li', {},
        element('strong', { textContent: '-' + p.name }),
        element('span', { className: 'psadt-param-type', textContent: ' ' + parameterDetail(p) }),
        p.description ? ' ' + p.description : ''))));
    }
    const docs = element('a', { href: fn.docs_url, textContent: 'Docs', target: '_blank', rel: 'noopener' });
    const insert = element('button', { type: 'button', textContent: 'Insert' });
    insert.addEventListener('click', () => insertFunction(fn));
    ui.detail.append(element('p', {}, insert, docs));
  }

  function openPicker() {
    if (!editor) return;
    if (!ui.root) buildPicker();
    ui.root.classList.add('open');
    ui.search.value = '';
    renderList();
    ui.search.focus();
  }

  function closePicker(restoreFocus = true) {
    if (!ui.root || !ui.root.classList.contains('open')) return;
    ui.root.classList.remove('open');
    if (restoreFocus) editor.focus();
  }

  function insertFunction(fn) {
    closePicker();
    if (!fn || editor.getOption(monaco.editor.EditorOption.readOnly)) return;
    const snippet = editor.getContribution('snippetController2');
    if (snippet) snippet.insert(snippetFor(fn));
    else editor.executeEdits('packman.psadt', [{ range: editor.getSelection(), text: plainText(snippetFor(fn)), forceMoveMarkers: true }]);
  }

  // ── Monaco wiring ────────────────────────────────────────────────────

  function register(target) {
    editor = target;
    if (registered) return;
    registered = true;

    monaco.languages.registerCompletionItemProvider('powershell', {
      triggerCharacters: ['-'],
      // Incomplete, so Monaco asks again as the word grows and the function rule is re-applied.
      provideCompletionItems: (model, position) => ({ suggestions: completionsAt(model, position), incomplete: true })
    });

    monaco.languages.registerHoverProvider('powershell', {
      provideHover: (model, position) => hoverAt(model, position)
    });

    monaco.languages.registerSignatureHelpProvider('powershell', {
      signatureHelpTriggerCharacters: [' ', '-'],
      provideSignatureHelp: (model, position) => {
        const value = signatureAt(model, position);
        return value && { value, dispose() {} };
      }
    });

    editor.addAction({
      id: 'packman.pickFunction',
      label: 'PSADT: Insert function',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyI],
      run: () => openPicker()
    });
  }

  window.PsadtIntellisense = {
    load, setVersion, availability, snippetFor, completionsAt, hoverAt, signatureAt,
    search, badgeText, register, openPicker, closePicker
  };
})();
