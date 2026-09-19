"use strict";

(() => {
  const el = (id) => document.getElementById(id);
  const root = document.documentElement;
  const alertBox = el("alert");
  const saveState = el("save-state");
  const views = [...document.querySelectorAll("[data-view]")];
  const navItems = [...document.querySelectorAll("[data-view-target]")];
  const titles = {
    overview: { section: "概要", current: "常駐ステータス" },
    profiles: { section: "設定", current: "プロファイル" },
    "profile-detail": { section: "設定", current: "アプリ別設定", nav: "profiles" },
    safety: { section: "安全", current: "ゲーム保護" },
    settings: { section: "システム", current: "基本設定" },
  };

  let settings = null;
  let revision = null;
  let editingProfileIndex = null;
  let lastStatus = null;

  function showAlert(message) {
    alertBox.hidden = !message;
    alertBox.textContent = message || "";
    // 長いページでは上部の警告が見えないまま操作が止まるため、出したら見える位置まで戻す。
    if (message) alertBox.scrollIntoView({ block: "start", behavior: "smooth" });
  }

  function setSaveState(message, kind) {
    saveState.textContent = message;
    saveState.className = "save-state" + (kind ? ` ${kind}` : "");
  }

  async function api(path, options = {}) {
    const { timeout, ...rest } = options;
    try {
      const response = await fetch(path, {
        ...rest,
        headers: {
          ...(rest.body ? { "Content-Type": "application/json" } : {}),
          ...(rest.headers || {}),
        },
        // 固まった要求で画面や保存が止まらないようにする。
        signal: timeout ? timeoutSignal(timeout) : undefined,
      });

      const text = await response.text();
      let payload = null;
      if (text) {
        try {
          payload = JSON.parse(text);
        } catch (_) {
          payload = { message: text };
        }
      }
      return { status: response.status, payload };
    } catch (error) {
      return { status: 0, payload: { message: error.message || "接続できませんでした。" } };
    }
  }

  function timeoutSignal(ms) {
    try {
      return AbortSignal.timeout(ms);
    } catch (_) {
      // 対応していないブラウザではタイムアウトなしで続ける。
      return undefined;
    }
  }

  function showView(viewName) {
    const title = titles[viewName] || titles.overview;
    views.forEach((view) => view.classList.toggle("is-active", view.dataset.view === viewName));
    // アプリ別設定はプロファイルの下層ページとして扱う。
    const navTarget = title.nav || viewName;
    document.querySelectorAll(".nav-item").forEach((item) => {
      if (item.dataset.viewTarget === navTarget) item.setAttribute("aria-current", "page");
      else item.removeAttribute("aria-current");
    });
    document.body.dataset.view = viewName;
    const section = document.querySelector(".breadcrumbs strong");
    if (section) section.textContent = title.section;
    const current = el("breadcrumb-current");
    if (current) current.textContent = title.current;
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function lines(value) {
    return String(value || "")
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line.length > 0);
  }

  function profileInitials(profile) {
    const source = String(profile.name || profile.id || "?").trim();
    const words = source.split(/\s+/).filter(Boolean);
    if (words.length > 1) return (words[0][0] + words[1][0]).toUpperCase();
    return source.slice(0, 2).toUpperCase();
  }

  function setSwitch(button, enabled) {
    if (!button) return;
    button.setAttribute("aria-checked", enabled ? "true" : "false");
  }

  function profileStatus(profile) {
    return profile.enabled === false ? "無効" : "有効";
  }

  function profileMeta(profile) {
    const processes = (profile.processNames || []).join(" / ") || "実行ファイル未指定";
    const rules = (profile.rules || []).length;
    return `${processes} · ${rules}ルール`;
  }

  // 実行ファイルのアイコン。取得できない場合はモノグラムのままにする。
  function makeAppIcon(processName, fallbackText) {
    const icon = document.createElement("div");
    icon.className = "app-icon";
    icon.setAttribute("aria-hidden", "true");

    if (processName) {
      const image = document.createElement("img");
      image.alt = "";
      image.loading = "lazy";
      image.src = `api/icon?name=${encodeURIComponent(processName)}`;
      image.addEventListener("load", () => {
        icon.classList.add("app-icon-image");
        rememberAppPath(processName);
      });
      image.addEventListener("error", () => {
        image.remove();
        icon.textContent = fallbackText;
      });
      icon.append(image);
    } else {
      icon.textContent = fallbackText;
    }

    return icon;
  }

  function profileIndex(profile) {
    return (settings?.profiles || []).indexOf(profile);
  }

  function makeProfileRow(profile, compact) {
    const row = document.createElement("div");
    row.className = compact ? "list-row" : "profile-row";

    const icon = makeAppIcon((profile.processNames || [])[0], profileInitials(profile));

    const name = document.createElement("strong");
    name.textContent = profile.name || profile.id || "名称未設定";
    const badge = document.createElement("span");
    badge.className = "badge " + (profile.enabled === false ? "" : "badge-success");
    badge.textContent = profileStatus(profile);

    if (compact) {
      const main = document.createElement("div");
      main.className = "list-primary";
      const meta = document.createElement("span");
      meta.textContent = profileMeta(profile);
      main.append(name, meta);
      const side = document.createElement("div");
      side.className = "list-side";
      side.append(badge);
      row.append(icon, main, side);
      return row;
    }

    const identity = document.createElement("div");
    identity.className = "profile-identity";
    const identityText = document.createElement("div");
    const id = document.createElement("span");
    id.className = "profile-id";
    id.textContent = profile.id || "-";
    identityText.append(name, id);
    identity.append(icon, identityText);

    const processes = document.createElement("span");
    processes.className = "profile-processes";
    processes.textContent = (profile.processNames || []).join(" / ") || "実行ファイル未指定";

    const rules = document.createElement("span");
    rules.className = "profile-rule-count";
    rules.textContent = `${(profile.rules || []).length} ルール`;

    const state = document.createElement("div");
    state.className = "profile-state";
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "switch";
    toggle.setAttribute("role", "switch");
    toggle.setAttribute("aria-label", `${profile.name || profile.id || "プロファイル"}を有効にする`);
    setSwitch(toggle, profile.enabled !== false);
    toggle.addEventListener("click", () => {
      profile.enabled = toggle.getAttribute("aria-checked") !== "true";
      setSwitch(toggle, profile.enabled);
      markDirty();
      renderProfiles();
      renderOverview();
    });
    state.append(badge, toggle);

    const actions = document.createElement("div");
    actions.className = "profile-actions";
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "button button-secondary button-small";
    edit.textContent = "編集";
    edit.addEventListener("click", () => openProfileEditor(profileIndex(profile)));

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "icon-button icon-button-danger";
    remove.textContent = "×";
    remove.title = "削除";
    remove.setAttribute("aria-label", `${profile.name || profile.id || "プロファイル"}を削除`);
    remove.addEventListener("click", () => deleteProfile(profileIndex(profile)));

    actions.append(edit, remove);
    row.append(identity, processes, rules, state, actions);
    return row;
  }

  function makeInput(label, id, value = "", type = "text") {
    const field = document.createElement("label");
    field.className = "field";
    const caption = document.createElement("span");
    caption.textContent = label;
    const input = document.createElement("input");
    if (id) input.id = id;
    input.type = type;
    input.value = value;
    field.append(caption, input);
    return { field, input };
  }

  // ルール編集は表形式。フィルタで隠れている行も ruleDraft に保持する。
  let ruleDraft = [];

  function cloneRule(rule) {
    return JSON.parse(JSON.stringify(rule || {}));
  }

  function ruleSearchText(rule) {
    const trigger = rule.trigger || {};
    const action = rule.action || {};
    const modifiers = Array.isArray(trigger.modifiers) ? trigger.modifiers.join(" ") : trigger.modifiers || "";
    const sequence = Array.isArray(action.sequence) ? action.sequence.join(" ") : action.sequence || "";
    return [trigger.prefix, trigger.key, modifiers, action.type, action.modifier, action.releaseOn, sequence]
      .filter(Boolean).join(" ").toLocaleLowerCase();
  }

  function makeRuleInput(datasetKey, label, value, placeholder, className) {
    const input = document.createElement("input");
    input.type = "text";
    input.dataset[datasetKey] = "";
    input.setAttribute("aria-label", label);
    input.placeholder = placeholder || "";
    input.value = value || "";
    if (className) input.className = className;
    return input;
  }

  // 直接入力（実際にキーを押して指定する）。テキスト入力はそのまま残し、欄の右のボタンで切り替える。
  const MODIFIER_KEYS = new Map([
    ["ControlLeft", "Ctrl"], ["ControlRight", "Ctrl"],
    ["ShiftLeft", "Shift"], ["ShiftRight", "Shift"],
    ["AltLeft", "Alt"], ["AltRight", "Alt"],
    ["MetaLeft", "LWin"], ["MetaRight", "RWin"],
  ]);

  const NAMED_KEYS = new Map([
    ["Escape", "Esc"], ["Enter", "Enter"], ["NumpadEnter", "Enter"], ["Tab", "Tab"], ["Space", "Space"],
    ["Backspace", "Backspace"], ["Delete", "Delete"], ["Insert", "Insert"], ["Home", "Home"], ["End", "End"],
    ["PageUp", "PgUp"], ["PageDown", "PgDn"], ["ArrowUp", "Up"], ["ArrowDown", "Down"],
    ["ArrowLeft", "Left"], ["ArrowRight", "Right"], ["Pause", "Pause"], ["PrintScreen", "PrintScreen"],
    ["ContextMenu", "AppsKey"],
  ]);

  let capturing = null;

  // code から正規のキー名を作る。文字キー・数字は Shift の影響を受けない。
  function keyNameOf(event) {
    const code = event.code || "";
    if (/^Key[A-Z]$/.test(code)) return code.slice(3).toLowerCase();
    if (/^Digit[0-9]$/.test(code)) return code.slice(5);
    if (/^Numpad[0-9]$/.test(code)) return code.slice(6);
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
    if (MODIFIER_KEYS.has(code)) return MODIFIER_KEYS.get(code);
    if (NAMED_KEYS.has(code)) return NAMED_KEYS.get(code);
    // 記号はレイアウトの文字を使う（JIS の : など）。
    return event.key && event.key.length === 1 ? event.key : null;
  }

  function heldModifiers(event) {
    const held = [];
    if (event.ctrlKey) held.push("Ctrl");
    if (event.shiftKey) held.push("Shift");
    if (event.altKey) held.push("Alt");
    if (event.metaKey) held.push("Win");
    return held;
  }

  const MODIFIER_ORDER = ["Ctrl", "Shift", "Alt", "Win"];

  // 左右や LWin/RWin の違いを、修飾欄で使う 4 種にまとめる。
  function modifierGroupOf(code) {
    if (code.startsWith("Control")) return "Ctrl";
    if (code.startsWith("Shift")) return "Shift";
    if (code.startsWith("Alt")) return "Alt";
    if (code.startsWith("Meta")) return "Win";
    return null;
  }

  // 入力欄を、押したキーで埋めるボタン付きにする。
  function withKeyCapture(input, kind) {
    const wrap = document.createElement("div");
    wrap.className = "rule-field";
    wrap.append(input, captureButton({ input, kind, wrap }));
    return wrap;
  }

  function captureButton(field) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "rule-capture";
    button.setAttribute("aria-pressed", "false");
    button.setAttribute("aria-label", "押したキーで入力");
    button.title = "押したキーで入力";
    button.innerHTML = '<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="6" width="19" height="12" rx="2.5"/><path d="M6.5 10h.01M10 10h.01M13.5 10h.01M17 10h.01M8 14h8"/></svg>';
    button.addEventListener("click", () => {
      if (capturing && capturing.button === button) disarmCapture();
      else armCapture({ ...field, button });
    });
    return button;
  }

  function armCapture(field) {
    disarmCapture();
    capturing = field;
    capturing.modifiers = new Set();
    field.button.setAttribute("aria-pressed", "true");
    field.wrap.classList.add("is-capturing");
    window.addEventListener("keydown", onCaptureKeyDown, true);
    window.addEventListener("keyup", onCaptureKeyUp, true);
    window.addEventListener("mousedown", onCaptureMouseDown, true);
    window.addEventListener("focusin", onCaptureFocusIn, true);
    window.addEventListener("wheel", onCaptureWheel, { capture: true, passive: false });
  }

  function disarmCapture() {
    if (!capturing) return;
    const field = capturing;
    capturing = null;
    field.button.setAttribute("aria-pressed", "false");
    field.wrap.classList.remove("is-capturing");
    window.removeEventListener("keydown", onCaptureKeyDown, true);
    window.removeEventListener("keyup", onCaptureKeyUp, true);
    window.removeEventListener("mousedown", onCaptureMouseDown, true);
    window.removeEventListener("focusin", onCaptureFocusIn, true);
    window.removeEventListener("wheel", onCaptureWheel, true);
  }

  // 録音中はブラウザのショートカットや入力欄への反映を止める（値は keyup で拾う）。
  function onCaptureKeyDown(event) {
    const group = modifierGroupOf(event.code || "");
    if (group && capturing) capturing.modifiers.add(group);
    event.preventDefault();
    event.stopPropagation();
  }

  function onCaptureKeyUp(event) {
    if (!capturing) return;
    const key = keyNameOf(event);
    const isModifier = MODIFIER_KEYS.has(event.code || "");
    const held = heldModifiers(event);
    let value = "";

    if (capturing.kind === "sequence") {
      // 送信内容は SendNotation の表記で書く。
      if (isModifier || !key) return;
      value = held.length ? `${held.join("+")}+${key}` : key;
    } else if (capturing.kind === "modifiers") {
      if (isModifier) {
        // 修飾キーの組み合わせは、押したものを全部離してから確定する。
        if (held.length) return;
        value = MODIFIER_ORDER.filter((name) => capturing.modifiers.has(name)).join(" + ");
      } else if (held.length) {
        value = held.join(" + ");
      }
    } else if (key) {
      value = key;
    }

    if (!value) return;
    event.preventDefault();
    event.stopPropagation();
    writeCaptured(value);
  }

  // 別の欄やボタンを操作したら録音を終える（録音が残ったまま保存などが動かないように）。
  function onCaptureFocusIn(event) {
    if (capturing && !capturing.wrap.contains(event.target)) disarmCapture();
  }

  function onCaptureMouseDown(event) {
    if (!capturing) return;
    if (event.button !== 1) {
      // 左クリックが録音欄の外なら、その操作を邪魔せず録音だけ終える。
      if (!capturing.wrap.contains(event.target)) disarmCapture();
      return;
    }
    if (capturing.kind === "modifiers") return;
    event.preventDefault();
    event.stopPropagation();
    writeCaptured("MButton");
  }

  function onCaptureWheel(event) {
    if (!capturing || capturing.kind === "modifiers") return;
    event.preventDefault();
    event.stopPropagation();
    writeCaptured(event.deltaY < 0 ? "WheelUp" : "WheelDown");
  }

  function writeCaptured(value) {
    const input = capturing.input;
    if (capturing.kind === "sequence") {
      const current = input.value.trim();
      input.value = current ? `${current}\n${value}` : value;
    } else {
      input.value = value;
    }
    input.dispatchEvent(new Event("input", { bubbles: true }));
    disarmCapture();
  }

  // 送信内容をキーキャップ表示にする。表記は SendNotation と同じで、+ は修飾キー、
  // 空白区切りは順に送る操作、↓ / ↑ は押しっぱなしと離す操作を表す。
  function renderKeycaps(view, text) {
    view.textContent = "";
    String(text || "").split("\n").forEach((line) => {
      const row = document.createElement("div");
      row.className = "keys-line";
      const parts = line.split(/\s+/).filter(Boolean);
      if (!parts.length) {
        row.classList.add("is-empty");
        row.textContent = "—";
      }
      parts.forEach((part, index) => {
        if (index > 0) {
          const step = document.createElement("span");
          step.className = "keys-step";
          row.append(step);
        }
        appendKeyPart(row, part);
      });
      view.append(row);
    });
  }

  function appendKeyPart(row, part) {
    let body = part;
    let mark = "";
    if (body.endsWith("↓")) {
      mark = "↓";
      body = body.slice(0, -1);
    } else if (body.endsWith("↑")) {
      mark = "↑";
      body = body.slice(0, -1);
    }

    // 「+」キーは単独なら +、修飾付きなら Ctrl++ と書く（SendNotation と同じ）。
    const plusKey = body.endsWith("++");
    const segments = (plusKey ? body.slice(0, -2) : body).split("+");
    const key = plusKey ? "+" : segments.pop();
    const modifiers = segments.filter((name) => name.trim());

    modifiers.forEach((name) => {
      row.append(keycap(name, "keycap-mod"));
      row.append(keyJoin());
    });
    row.append(keycap(key || body || part, "", mark));
  }

  function keycap(text, className, mark) {
    const chip = document.createElement("span");
    chip.className = className ? `keycap ${className}` : "keycap";
    chip.textContent = text;
    if (mark) {
      const arrow = document.createElement("span");
      arrow.className = "keycap-mark";
      arrow.textContent = mark;
      chip.append(arrow);
    }
    return chip;
  }

  function keyJoin() {
    const join = document.createElement("span");
    join.className = "keys-join";
    join.textContent = "+";
    return join;
  }

  function renderRuleDetail(detail, type, action) {
    detail.textContent = "";
    if (type === "hold") {
      const modifier = makeRuleInput("ruleModifier", "維持するキー", action.modifier, "維持するキー（例: Ctrl）");
      const release = makeRuleInput("ruleReleaseOn", "解除するキー", action.releaseOn, "解除するキー（例: f13）");
      detail.append(withKeyCapture(modifier, "key"), withKeyCapture(release, "key"));
      return;
    }

    if (type === "passthrough") {
      const note = document.createElement("span");
      note.className = "rule-note";
      note.textContent = "元の入力をそのまま通します。";
      detail.append(note);
      return;
    }

    const sequence = document.createElement("textarea");
    const lines = Array.isArray(action.sequence) ? action.sequence : [];
    sequence.rows = Math.min(6, Math.max(1, lines.length || 1));
    sequence.spellcheck = false;
    sequence.dataset.ruleSequence = "";
    sequence.placeholder = "送るキー（例: Ctrl+z / Esc。1行に1操作）";
    sequence.setAttribute("aria-label", "送るキー列");
    sequence.value = lines.join("\n");
    // 通常はキーキャップで表示し、フォーカス中だけ生テキストを編集する。
    const keys = document.createElement("div");
    keys.className = "rule-keys";
    const view = document.createElement("div");
    view.className = "keys-view";
    view.setAttribute("aria-hidden", "true");
    sequence.addEventListener("focus", () => keys.classList.add("is-editing"));
    sequence.addEventListener("blur", () => keys.classList.remove("is-editing"));
    view.addEventListener("mousedown", (event) => {
      event.preventDefault();
      sequence.focus();
    });
    renderKeycaps(view, sequence.value);
    keys.append(view, sequence, captureButton({ input: sequence, kind: "sequence", wrap: keys }));

    // 2行以上ある割り当て（Snd の第2引数）が隠れないよう高さを合わせる。
    sequence.addEventListener("input", () => {
      const needed = Math.min(6, Math.max(1, sequence.value.split("\n").length));
      if (sequence.rows !== needed) sequence.rows = needed;
      renderKeycaps(view, sequence.value);
    });
    detail.append(keys);
  }

  function renderRuleRow(rule, index) {
    const trigger = rule.trigger || {};
    const action = rule.action || { type: "send" };
    const row = document.createElement("div");
    row.className = "rule-row";
    row.dataset.ruleIndex = String(index);

    const prefixInput = makeRuleInput("rulePrefix", "前置キー", trigger.prefix, "—", "rule-prefix");
    if ((trigger.prefix || "").trim()) prefixInput.classList.add("is-set");
    prefixInput.addEventListener("input", () => prefixInput.classList.toggle("is-set", prefixInput.value.trim() !== ""));
    const prefix = withKeyCapture(prefixInput, "key");

    const key = withKeyCapture(makeRuleInput("ruleKey", "トリガキー", trigger.key, "f13", "rule-key"), "key");
    const modifiers = withKeyCapture(
      makeRuleInput(
        "ruleModifiers",
        "修飾キー",
        Array.isArray(trigger.modifiers) ? trigger.modifiers.join(" + ") : trigger.modifiers,
        "Ctrl + Shift"),
      "modifiers");

    const actionType = document.createElement("select");
    actionType.dataset.ruleAction = "";
    actionType.setAttribute("aria-label", "動作");
    [["send", "キーを送る"], ["hold", "キーを維持"], ["passthrough", "元の入力を通す"]].forEach(([value, label]) => {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      option.selected = (action.type || "send") === value;
      actionType.append(option);
    });

    const detail = document.createElement("div");
    detail.className = "rule-detail";

    const flags = document.createElement("div");
    flags.className = "rule-flags";
    [["ruleAnyModifier", "修飾無視", "修飾キーの有無を問わずに発火する", trigger.anyModifier],
     ["rulePassThrough", "素通し", "元のキーもそのままアプリへ渡す", trigger.passThroughNative]].forEach(([datasetKey, mark, title, checked]) => {
      const field = document.createElement("label");
      field.className = "rule-flag";
      field.title = title;
      const box = document.createElement("input");
      box.type = "checkbox";
      box.dataset[datasetKey] = "";
      box.checked = checked === true;
      box.setAttribute("aria-label", title);
      field.append(box, document.createTextNode(mark));
      flags.append(field);
    });

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "rule-remove";
    remove.textContent = "×";
    remove.setAttribute("aria-label", "このショートカットを削除");
    remove.addEventListener("click", () => {
      commitVisibleRules();
      ruleDraft[index] = null;
      renderRuleList();
    });

    actionType.addEventListener("change", () => {
      const next = actionType.value;
      renderRuleDetail(detail, next, next === (action.type || "send") ? action : {});
    });

    renderRuleDetail(detail, action.type || "send", action);
    row.append(prefix, modifiers, key, actionType, detail, flags, remove);
    return row;
  }

  function snapshotRuleRow(row) {
    const get = (datasetKey) => row.querySelector(`[data-${datasetKey}]`);
    const detail = row.querySelector(".rule-detail");
    const detailValue = (datasetKey) => {
      const node = detail.querySelector(`[data-${datasetKey}]`);
      return node ? node.value : "";
    };

    return {
      trigger: {
        key: get("rule-key").value,
        prefix: get("rule-prefix").value,
        modifiers: get("rule-modifiers").value,
        anyModifier: get("rule-any-modifier").checked,
        passThroughNative: get("rule-pass-through").checked,
      },
      action: {
        type: get("rule-action").value,
        sequence: detailValue("rule-sequence").split("\n"),
        modifier: detailValue("rule-modifier"),
        releaseOn: detailValue("rule-release-on"),
      },
    };
  }

  function commitVisibleRules() {
    el("rule-list").querySelectorAll(".rule-row").forEach((row) => {
      const index = Number(row.dataset.ruleIndex);
      if (Number.isInteger(index)) ruleDraft[index] = snapshotRuleRow(row);
    });
  }

  function renderRuleList() {
    // 行を作り直すと録音中の欄が消えるため、先に録音を終える。
    disarmCapture();
    ruleDraft = ruleDraft.filter(Boolean);
    const query = (el("rule-filter").value || "").trim().toLocaleLowerCase();
    const list = el("rule-list");
    list.textContent = "";

    let shown = 0;
    ruleDraft.forEach((rule, index) => {
      if (!ruleSearchText(rule).includes(query)) return;
      list.append(renderRuleRow(rule, index));
      shown++;
    });

    el("rule-count-label").textContent = query
      ? `${shown} / ${ruleDraft.length} 件を表示`
      : `全${ruleDraft.length} 件（1行が1つの割り当て）`;
    el("rule-empty").hidden = shown > 0;
  }

  function openRuleEditor(rules) {
    ruleDraft = (rules || []).map(cloneRule);
    el("rule-filter").value = "";
    renderRuleList();
  }

  function rulesFromDraft() {
    commitVisibleRules();
    ruleDraft = ruleDraft.filter(Boolean);

    return ruleDraft.map((rule) => {
      const trigger = rule.trigger || {};
      const type = (rule.action || {}).type || "send";
      const key = (trigger.key || "").trim();
      if (!key) throw new Error("トリガキーを入力してください。");

      const parsed = {};
      const prefix = (trigger.prefix || "").trim();
      const modifiers = String(trigger.modifiers || "").split(/[+,\s]+/).filter(Boolean);
      if (prefix) parsed.prefix = prefix;
      parsed.key = key;
      if (modifiers.length) parsed.modifiers = modifiers;
      if (trigger.anyModifier) parsed.anyModifier = true;
      if (trigger.passThroughNative) parsed.passThroughNative = true;

      const action = { type };
      if (type === "send") {
        action.sequence = (rule.action.sequence || []).map((line) => String(line).trim()).filter(Boolean);
        if (!action.sequence.length) throw new Error(`${key} に送るキー列を入力してください。`);
      }
      if (type === "hold") {
        action.modifier = (rule.action.modifier || "").trim();
        action.releaseOn = (rule.action.releaseOn || "").trim();
        if (!action.modifier || !action.releaseOn) throw new Error(`${key} の維持するキーと解除するキーを入力してください。`);
      }

      return { trigger: parsed, action };
    });
  }

  function openProfileEditor(index) {
    if (!settings?.profiles?.[index]) return;
    editingProfileIndex = index;
    const profile = settings.profiles[index];
    el("profile-detail-title").textContent = profile.name || profile.id || "プロファイル";
    el("profile-detail-meta").textContent = `ID: ${profile.id || "-"}`;
    el("profile-name").value = profile.name || "";
    el("profile-processes").value = (profile.processNames || []).join("\n");
    setSwitch(el("profile-enabled"), profile.enabled !== false);
    openRuleEditor(profile.rules);
    showView("profile-detail");
  }

  function closeProfileEditor() {
    editingProfileIndex = null;
    showView("profiles");
  }

  // 追加したアプリは ID も自動で振る（表示名と対象プロセスは編集ページで決める）。
  function addProfile() {
    if (!settings) return;
    if (!settings.profiles) settings.profiles = [];

    const used = new Set(settings.profiles.map((profile) => profile.id));
    let id = "";
    for (let suffix = 1; !id; suffix++) {
      if (!used.has(`app-${suffix}`)) id = `app-${suffix}`;
    }

    settings.profiles.push({ id, name: "新しいアプリ", enabled: true, processNames: [], rules: [] });
    markDirty();
    renderProfiles();
    renderOverview();
    openProfileEditor(settings.profiles.length - 1);
  }

  function deleteProfile(index) {
    const profile = settings?.profiles?.[index];
    if (!profile) return;
    if (!window.confirm(`「${profile.name || profile.id}」を削除します。よろしいですか？`)) return;

    settings.profiles.splice(index, 1);
    markDirty();
    renderProfiles();
    renderOverview();
    if (editingProfileIndex !== null) closeProfileEditor();
  }

  function applyProfileEditor() {
    if (editingProfileIndex === null || !settings?.profiles?.[editingProfileIndex]) return;
    try {
      const profile = settings.profiles[editingProfileIndex];
      const name = el("profile-name").value.trim();
      if (!name) throw new Error("表示名を入力してください。");
      profile.name = name;
      profile.enabled = el("profile-enabled").getAttribute("aria-checked") === "true";
      profile.processNames = lines(el("profile-processes").value);
      profile.rules = rulesFromDraft();
      markDirty();
      renderProfiles();
      renderOverview();
      showAlert("");
      closeProfileEditor();
      detectAppPaths();
    } catch (error) {
      showAlert(error.message || "プロファイルを更新できませんでした。");
    }
  }

  function renderProfiles() {
    if (!settings) return;
    const profiles = settings.profiles || [];
    const enabled = profiles.filter((profile) => profile.enabled !== false).length;
    const countText = `${profiles.length}件のプロファイル · 有効 ${enabled}件`;
    const query = el("profile-search").value.trim().toLocaleLowerCase();
    const visible = profiles.filter((profile) => {
      const haystack = [profile.name, profile.id, ...(profile.processNames || [])].join(" ").toLocaleLowerCase();
      return !query || haystack.includes(query);
    });

    el("nav-profile-count").textContent = String(profiles.length);
    el("profile-count-label").textContent = countText;
    el("profile-results").textContent = query ? `${visible.length} / ${profiles.length} 件を表示` : `${profiles.length} 件を表示`;

    const list = el("profile-list");
    list.textContent = "";
    visible.forEach((profile) => list.append(makeProfileRow(profile, false)));
    const empty = el("profile-empty");
    empty.hidden = visible.length !== 0;
    empty.querySelector("p").textContent = profiles.length === 0 ? "プロファイルがありません。" : "一致するプロファイルがありません。";

    const overview = el("overview-profiles");
    overview.textContent = "";
    profiles.slice(0, 4).forEach((profile) => overview.append(makeProfileRow(profile, true)));
    if (profiles.length === 0) {
      const emptyOverview = document.createElement("div");
      emptyOverview.className = "empty-state";
      emptyOverview.textContent = "プロファイルがありません。";
      overview.append(emptyOverview);
    }
  }

  function processRoles(name) {
    const protection = settings.gameProtection || {};
    const has = (list) => (list || []).some((value) => value.toLocaleLowerCase() === name.toLocaleLowerCase());
    const roles = [];
    if (has(protection.stopTriggerProcessNames)) roles.push(["退避トリガ", "badge-accent"]);
    if (has(protection.resumeProcessNames)) roles.push(["復帰を待つ", "badge-success"]);
    return roles;
  }

  function renderProcessList() {
    if (!settings) return;
    const protection = settings.gameProtection || {};
    const names = [...new Set([...(protection.stopTriggerProcessNames || []), ...(protection.resumeProcessNames || [])])];
    const list = el("process-list");
    list.textContent = "";

    names.forEach((name) => {
      const row = document.createElement("div");
      row.className = "process-row";

      const identity = document.createElement("div");
      identity.className = "process-identity";
      const label = document.createElement("span");
      label.className = "process-name";
      label.textContent = name;
      identity.append(makeAppIcon(name, name.slice(0, 2).toUpperCase()), label);

      const roles = document.createElement("div");
      roles.className = "process-roles";
      processRoles(name).forEach(([text, kind]) => {
        const badge = document.createElement("span");
        badge.className = `badge ${kind}`;
        badge.textContent = text;
        roles.append(badge);
      });

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "icon-button icon-button-danger";
      remove.textContent = "×";
      remove.title = "監視から外す";
      remove.setAttribute("aria-label", `${name}を監視から外す`);
      remove.addEventListener("click", () => removeProcess(name));

      row.append(identity, roles, remove);
      list.append(row);
    });

    if (names.length === 0) {
      const empty = document.createElement("div");
      empty.className = "empty-state";
      empty.textContent = "監視するアプリはまだありません。";
      list.append(empty);
    }

    el("process-count-label").textContent = `${names.length}件`;
    updateProcessWarning();
  }

  // 退避トリガが空だと保存できないため、条件が崩れたら先に知らせる。
  function updateProcessWarning() {
    const protection = settings?.gameProtection || {};
    const warning = el("process-warning");
    if (warning) warning.hidden = protection.enabled === false || (protection.stopTriggerProcessNames || []).length > 0;
  }

  function addProcess() {
    if (!settings) return;
    const input = el("process-add-name");
    // フルパスを貼られても実行ファイル名として扱う（照合は名前で行う）。
    const name = input.value.trim().split(/[\\/]/).pop();
    if (!name) {
      input.focus();
      return;
    }

    const protection = settings.gameProtection || (settings.gameProtection = {});
    const key = el("process-add-role").value === "resume" ? "resumeProcessNames" : "stopTriggerProcessNames";
    if (!protection[key]) protection[key] = [];
    if (!protection[key].some((value) => value.toLocaleLowerCase() === name.toLocaleLowerCase())) protection[key].push(name);

    input.value = "";
    input.focus();
    markDirty();
    renderProcessList();
    renderOverview();
    detectAppPaths();
  }

  function removeProcess(name) {
    const protection = settings?.gameProtection;
    if (!protection) return;

    ["stopTriggerProcessNames", "resumeProcessNames"].forEach((key) => {
      if (!protection[key]) return;
      protection[key] = protection[key].filter((value) => value.toLocaleLowerCase() !== name.toLocaleLowerCase());
    });

    markDirty();
    renderProcessList();
    renderOverview();
  }

  function renderOverview() {
    if (!settings) return;
    const profiles = settings.profiles || [];
    const enabled = profiles.filter((profile) => profile.enabled !== false).length;
    const rules = profiles.reduce((total, profile) => total + (profile.rules || []).length, 0);
    const protection = settings.gameProtection || {};
    const processes = (protection.stopTriggerProcessNames || []).length + (protection.resumeProcessNames || []).length;

    el("summary-profiles").textContent = String(enabled);
    el("summary-profiles-detail").textContent = `${profiles.length}件中 · ${enabled === profiles.length ? "すべて有効" : "一部無効"}`;
    el("summary-rules").textContent = String(rules);
    el("summary-rules-detail").textContent = "登録済みのキー変換ルール";
    el("summary-processes").textContent = String(processes);
    el("summary-processes-detail").textContent = protection.enabled === false ? "ゲーム保護は無効" : "ゲーム保護を監視中";
    el("overview-revision").textContent = revision ? revision.slice(0, 8) : "-";

    const activity = el("overview-activity");
    activity.textContent = "";
    const rows = [
      ["設定の版", revision ? revision.slice(0, 12) : "-"],
      ["ゲーム保護", protection.enabled === false ? "無効" : "有効"],
      ["監視対象", `${processes}件`],
    ];
    rows.forEach(([label, value]) => {
      const item = document.createElement("div");
      item.className = "activity-item";
      const marker = document.createElement("span");
      marker.className = "activity-marker";
      marker.setAttribute("aria-hidden", "true");
      const text = document.createElement("p");
      text.textContent = `${label}: ${value}`;
      item.append(marker, text);
      activity.append(item);
    });
  }

  function renderSafety() {
    if (!settings) return;
    const protection = settings.gameProtection || {};
    setSwitch(el("protection-enabled"), protection.enabled !== false);
    el("poll-interval").value = protection.pollIntervalMs ?? 1000;
    el("resume-delay").value = protection.resumeDelayMs ?? 1000;
    const enabled = protection.enabled !== false;
    el("protection-summary").textContent = enabled
      ? "ゲーム保護が有効です。危険なプロセスを検知すると入力エンジンを停止します。"
      : "ゲーム保護は無効です。危険なプロセスを検知しても退避しません。";
    el("sidebar-protection-title").textContent = enabled ? "安全に常駐中" : "保護は無効";
    el("sidebar-protection-copy").textContent = enabled
      ? "保護対象のゲームを検知すると、入力エンジンを停止して本体を終了します。"
      : "ゲーム保護を有効にすると、対象プロセスを監視します。";
    renderProcessList();
  }

  function renderGeneralSettings() {
    if (!settings) return;
    const input = settings.input || {};
    setSwitch(el("ime-disable"), input.imeDisableBeforeSend !== false);
    el("settings-revision").textContent = revision || "-";
  }

  function renderAll() {
    if (!settings) return;
    renderGeneralSettings();
    renderSafety();
    renderProfiles();
    renderOverview();
  }

  function markDirty() {
    if (settings) setSaveState("未保存の変更があります");
  }

  function syncSettingsFromForms() {
    if (!settings) return;
    if (!settings.input) settings.input = {};
    const input = settings.input;
    input.imeDisableBeforeSend = el("ime-disable").getAttribute("aria-checked") === "true";

    if (!settings.gameProtection) settings.gameProtection = {};
    const protection = settings.gameProtection;
    protection.enabled = el("protection-enabled").getAttribute("aria-checked") === "true";
    protection.pollIntervalMs = Number(el("poll-interval").value);
    protection.resumeDelayMs = Number(el("resume-delay").value);
    markDirty();
    renderOverview();
    updateProcessWarning();
    const summary = protection.enabled
      ? "ゲーム保護が有効です。危険なプロセスを検知すると入力エンジンを停止します。"
      : "ゲーム保護は無効です。危険なプロセスを検知しても退避しません。";
    el("protection-summary").textContent = summary;
  }

  // 実際にアイコンを出せた実行ファイルは、その場でフルパスを設定へ残す。
  const pendingPaths = new Set();
  let pathFlushScheduled = false;
  // 追記要求は直列化する（保存と重なって版がずれるのを避ける）。
  let pathMerge = Promise.resolve();

  function rememberAppPath(processName) {
    if (!processName || (settings?.appPaths || {})[processName]) return;

    pendingPaths.add(processName);
    if (pathFlushScheduled) return;

    // 一覧を描くたびに要求を散らさないよう、少しだけまとめる。
    pathFlushScheduled = true;
    window.setTimeout(() => {
      pathFlushScheduled = false;
      const names = [...pendingPaths];
      pendingPaths.clear();
      mergeAppPaths(names);
    }, 300);
  }

  function mergeAppPaths(names) {
    if (!names.length) return pathMerge;

    pathMerge = pathMerge.then(async () => {
      const result = await api("/api/app-paths", { method: "POST", body: JSON.stringify({ names }), timeout: 5000 });
      if (result.status !== 200 || !result.payload) return;

      settings.appPaths = { ...(settings.appPaths || {}), ...(result.payload.paths || {}) };
      // サーバー側が追記して版が進むため、保持している版も合わせる。
      if (result.payload.revision) revision = result.payload.revision;
    });

    return pathMerge;
  }

  // 起動中のアプリ・App Paths・HKCR Applications からフルパスを拾い、アイコンを次回以降も出せるようにする。
  // 保存済みパスが無効になった場合（アプリの更新・移動）も探し直せるよう、既知の名前もまとめて送る。
  async function detectAppPaths() {
    if (!settings) return;
    const names = new Set();
    (settings.profiles || []).forEach((profile) => (profile.processNames || []).forEach((name) => names.add(name)));
    const protection = settings.gameProtection || {};
    [...(protection.stopTriggerProcessNames || []), ...(protection.resumeProcessNames || [])].forEach((name) => names.add(name));

    await mergeAppPaths([...names].filter(Boolean));
  }

  async function loadSettings() {
    const result = await api("/api/settings");
    if (result.status !== 200 || !result.payload) {
      showAlert("設定を取得できませんでした。");
      setSaveState("読み込み失敗", "error");
      return;
    }

    try {
      settings = JSON.parse(result.payload.json);
      revision = result.payload.revision;
      editingProfileIndex = null;
      // 廃止した backend セクションは読み捨てる（保存時に書き戻さない）。
      delete settings.backend;
      renderAll();
      showAlert("");
      setSaveState("保存済み", "ok");
      // 検知は表示を待たせない（アイコンは要求時に実行ファイルを探すため、後追いでも同じ結果になる）。
      detectAppPaths();
    } catch (error) {
      showAlert(`設定を読み込めませんでした: ${error.message}`);
      setSaveState("読み込み失敗", "error");
    }
  }

  async function loadEventLog() {
    const box = el("event-log");
    if (!box) return;

    const result = await api("/api/log");
    const events = result.payload?.events;
    if (result.status !== 200 || !Array.isArray(events) || events.length === 0) {
      box.textContent = "（まだ入力はありません）";
      return;
    }

    box.textContent = events.slice(-60).join("\n");
    box.scrollTop = box.scrollHeight;
  }

  async function loadStatus() {
    const result = await api("/api/status");
    if (result.status !== 200 || !result.payload) {
      lastStatus = null;
      el("runtime-dot").className = "status-dot error";
      el("runtime-status-title").textContent = "状態を取得できません";
      el("runtime-copy").textContent = "本体との接続を確認してください。";
      el("connection-text").textContent = "Windowsホスト未接続";
      el("status-foreground").textContent = "-";
      el("status-profile").textContent = "-";
      return;
    }

    lastStatus = result.payload;
    const state = result.payload.state || "unknown";
    const running = state === "running";
    const paused = state === "paused";
    el("runtime-dot").className = "status-dot" + (running ? "" : paused ? " offline-dot" : " error");
    el("runtime-status-title").textContent = running ? "入力変換は有効です" : paused ? "入力変換を一時停止中" : "入力変換は停止中です";
    el("runtime-copy").textContent = running
      ? "前面アプリに合わせて、登録済みのプロファイルを適用しています。"
      : paused
        ? "手動停止中です。再開するまでキー変換は行いません。"
        : "入力エンジンが停止しています。";
    el("connection-text").textContent = result.payload.engineInstalled === false ? "ホスト接続済み・フック未設置" : "Windowsホスト接続済み";
    el("status-foreground").textContent = "自動判定";
    el("status-profile").textContent = result.payload.activeProfile || "対象外";

    const fact = el("runtime-copy");
    if (result.payload.engineInstalled === false) fact.textContent = "入力フックを設置できていません。";
    renderOverviewActivityStatus();
  }

  function renderOverviewActivityStatus() {
    if (!lastStatus) return;
    const first = el("overview-activity")?.querySelector(".activity-item p");
    if (first) first.textContent = `入力変換: ${lastStatus.state || "不明"}`;
  }

  async function save() {
    if (!settings) return;
    // 追記が終わってから版を読む（直後に 409 にならないようにする）。
    await pathMerge;
    syncSettingsFromForms();
    const json = JSON.stringify(settings, null, 2);

    setSaveState("保存中…");
    const result = await api("/api/settings", {
      method: "POST",
      body: JSON.stringify({ revision, json }),
    });
    if (result.status === 200) {
      revision = result.payload?.revision || revision;
      await loadSettings();
      await loadStatus();
      setSaveState("保存して反映しました", "ok");
      return;
    }

    showAlert(result.payload?.message || `保存に失敗しました（${result.status}）。`);
    setSaveState("保存していません", "error");
  }

  async function restore() {
    if (!window.confirm("設定を既定値に戻します。よろしいですか？")) return;
    setSaveState("戻しています…");
    const result = await api("/api/settings/restore", { method: "POST", body: "{}" });
    if (result.status === 200) {
      showAlert("");
      await loadSettings();
      setSaveState("既定値に戻しました", "ok");
      return;
    }
    showAlert(result.payload?.message || "既定値へ戻せませんでした。");
    setSaveState("戻していません", "error");
  }

  function updateThemeIcon() {
    const dark = root.dataset.theme === "dark";
    document.querySelectorAll("[data-theme-icon]").forEach((icon) => {
      icon.innerHTML = dark
        ? '<path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M18.4 5.6 17 7M7 17l-1.4 1.4"/><circle cx="12" cy="12" r="4"/>'
        : '<path d="M20.5 15.3A8.5 8.5 0 0 1 8.7 3.5 8.5 8.5 0 1 0 20.5 15.3Z"/>';
    });
    document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
      button.setAttribute("aria-label", dark ? "ライトモードに切り替え" : "ダークモードに切り替え");
    });
  }

  navItems.forEach((item) => item.addEventListener("click", () => showView(item.dataset.viewTarget)));
  el("log-refresh").addEventListener("click", loadEventLog);
  el("profile-search").addEventListener("input", renderProfiles);
  document.querySelectorAll("[data-theme-toggle]").forEach((button) => button.addEventListener("click", () => {
    root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
    try { localStorage.setItem("leafhotkey.theme", root.dataset.theme); } catch (_) { /* 保存できないブラウザもある */ }
    updateThemeIcon();
  }));

  ["protection-enabled", "ime-disable"].forEach((id) => {
    el(id).addEventListener("click", () => {
      const button = el(id);
      setSwitch(button, button.getAttribute("aria-checked") !== "true");
      syncSettingsFromForms();
    });
  });
  ["poll-interval", "resume-delay"].forEach((id) => {
    el(id).addEventListener("input", syncSettingsFromForms);
    el(id).addEventListener("change", syncSettingsFromForms);
  });
  el("profile-enabled").addEventListener("click", () => {
    const button = el("profile-enabled");
    setSwitch(button, button.getAttribute("aria-checked") !== "true");
  });
  el("add-rule").addEventListener("click", () => {
    commitVisibleRules();
    el("rule-filter").value = "";
    ruleDraft.push({ trigger: { key: "" }, action: { type: "send", sequence: [""] } });
    renderRuleList();

    const rows = el("rule-list").querySelectorAll(".rule-row");
    const last = rows[rows.length - 1];
    if (last) last.querySelector("[data-rule-key]").focus();
  });
  el("rule-filter").addEventListener("input", () => {
    commitVisibleRules();
    renderRuleList();
  });
  el("apply-profile").addEventListener("click", applyProfileEditor);
  el("profile-back").addEventListener("click", closeProfileEditor);
  el("profile-cancel").addEventListener("click", closeProfileEditor);
  el("add-profile").addEventListener("click", addProfile);
  el("process-add").addEventListener("click", addProcess);
  el("process-add-name").addEventListener("keydown", (event) => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    addProcess();
  });

  el("save").addEventListener("click", save);
  el("reload").addEventListener("click", () => { loadSettings(); loadStatus(); loadEventLog(); });
  el("restore").addEventListener("click", restore);

  // 既定はダーク（index.html のインラインスクリプトと同じ判定）。
  root.dataset.theme = root.dataset.theme === "light" ? "light" : "dark";
  updateThemeIcon();
  showView("overview");
  loadSettings();
  loadStatus();
  loadEventLog();
  window.setInterval(loadStatus, 5000);
})();
