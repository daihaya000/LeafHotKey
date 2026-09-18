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
  }

  function setSaveState(message, kind) {
    saveState.textContent = message;
    saveState.className = "save-state" + (kind ? ` ${kind}` : "");
  }

  async function api(path, options = {}) {
    try {
      const response = await fetch(path, {
        ...options,
        headers: {
          ...(options.body ? { "Content-Type": "application/json" } : {}),
          ...(options.headers || {}),
        },
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

  function showView(viewName) {
    const title = titles[viewName] || titles.overview;
    views.forEach((view) => view.classList.toggle("is-active", view.dataset.view === viewName));
    document.querySelectorAll(".nav-item").forEach((item) => {
      if (item.dataset.viewTarget === viewName) item.setAttribute("aria-current", "page");
      else item.removeAttribute("aria-current");
    });
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

  function makeProfileRow(profile, compact) {
    const row = document.createElement("div");
    row.className = compact ? "list-row" : "profile-row";

    const icon = document.createElement("div");
    icon.className = "app-icon";
    icon.setAttribute("aria-hidden", "true");
    // 対象実行ファイルのアイコンを出す。取得できない場合はモノグラムのままにする。
    const processName = (profile.processNames || [])[0];
    if (processName) {
      const image = document.createElement("img");
      image.alt = "";
      image.loading = "lazy";
      image.src = `api/icon?name=${encodeURIComponent(processName)}`;
      image.addEventListener("load", () => icon.classList.add("app-icon-image"));
      image.addEventListener("error", () => {
        image.remove();
        icon.textContent = profileInitials(profile);
      });
      icon.append(image);
    } else {
      icon.textContent = profileInitials(profile);
    }

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

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "button button-secondary button-small";
    edit.textContent = "編集";
    edit.addEventListener("click", () => openProfileEditor((settings.profiles || []).indexOf(profile)));
    row.append(identity, processes, rules, state, edit);
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

  function renderRuleDetail(detail, type, action) {
    detail.textContent = "";
    if (type === "hold") {
      const modifier = makeRuleInput("ruleModifier", "維持するキー", action.modifier, "維持するキー（例: Ctrl）");
      const release = makeRuleInput("ruleReleaseOn", "解除するキー", action.releaseOn, "解除するキー（例: f13）");
      detail.append(modifier, release);
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
    sequence.placeholder = "送るキー（例: ^z / {Esc}。1行に1操作）";
    sequence.setAttribute("aria-label", "送るキー列");
    sequence.value = lines.join("\n");
    // 2行以上ある割り当て（Snd の第2引数）が隠れないよう高さを合わせる。
    sequence.addEventListener("input", () => {
      const needed = Math.min(6, Math.max(1, sequence.value.split("\n").length));
      if (sequence.rows !== needed) sequence.rows = needed;
    });
    detail.append(sequence);
  }

  function renderRuleRow(rule, index) {
    const trigger = rule.trigger || {};
    const action = rule.action || { type: "send" };
    const row = document.createElement("div");
    row.className = "rule-row";
    row.dataset.ruleIndex = String(index);
    // blind（AHK の {Blind}）は編集対象ではないが、保存時に消えないよう保持する。
    row.dataset.ruleBlind = action.blind === false ? "false" : "true";

    const prefix = makeRuleInput("rulePrefix", "前置キー", trigger.prefix, "—", "rule-prefix");
    if ((trigger.prefix || "").trim()) prefix.classList.add("is-set");
    prefix.addEventListener("input", () => prefix.classList.toggle("is-set", prefix.value.trim() !== ""));

    const key = makeRuleInput("ruleKey", "トリガキー", trigger.key, "f13", "rule-key");
    const modifiers = makeRuleInput(
      "ruleModifiers",
      "修飾キー",
      Array.isArray(trigger.modifiers) ? trigger.modifiers.join(" + ") : trigger.modifiers,
      "Ctrl + Shift");

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
    [["ruleAnyModifier", "*", "修飾キーの有無を問わない（AHK の *）", trigger.anyModifier],
     ["rulePassThrough", "~", "元の入力を通す（AHK の ~）", trigger.passThroughNative]].forEach(([datasetKey, mark, title, checked]) => {
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
    row.append(prefix, key, modifiers, actionType, detail, flags, remove);
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
        blind: row.dataset.ruleBlind !== "false",
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
        action.blind = rule.action.blind !== false;
        if (!action.modifier || !action.releaseOn) throw new Error(`${key} の維持するキーと解除するキーを入力してください。`);
      }

      return { trigger: parsed, action };
    });
  }

  function openProfileEditor(index) {
    if (!settings?.profiles?.[index]) return;
    editingProfileIndex = index;
    const profile = settings.profiles[index];
    el("profile-dialog-title").textContent = `${profile.name || profile.id || "プロファイル"}を編集`;
    el("profile-dialog-meta").textContent = `ID: ${profile.id || "-"}`;
    el("profile-name").value = profile.name || "";
    el("profile-processes").value = (profile.processNames || []).join("\n");
    setSwitch(el("profile-enabled"), profile.enabled !== false);
    openRuleEditor(profile.rules);
    el("profile-dialog").showModal();
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
      el("profile-dialog").close();
      markDirty();
      renderProfiles();
      renderOverview();
      showAlert("");
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

  function renderProcessList() {
    if (!settings) return;
    const protection = settings.gameProtection || {};
    const stop = protection.stopTriggerProcessNames || [];
    const resume = protection.resumeProcessNames || [];
    const list = el("process-list");
    list.textContent = "";

    for (const [name, role] of stop.map((value) => [value, "退避トリガ"]).concat(resume.map((value) => [value, "復帰待ち"]))) {
      const row = document.createElement("div");
      row.className = "process-row";
      const processName = document.createElement("span");
      processName.className = "process-name";
      processName.textContent = name;
      const note = document.createElement("span");
      note.className = "process-note";
      note.textContent = role;
      const badge = document.createElement("span");
      badge.className = "badge badge-success";
      badge.textContent = "監視対象";
      row.append(processName, note, badge);
      list.append(row);
    }

    if (stop.length + resume.length === 0) {
      const empty = document.createElement("div");
      empty.className = "empty-state";
      empty.textContent = "監視対象プロセスはありません。";
      list.append(empty);
    }
    el("process-count-label").textContent = `${stop.length + resume.length}件`;
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
    el("stop-triggers").value = (protection.stopTriggerProcessNames || []).join("\n");
    el("resume-processes").value = (protection.resumeProcessNames || []).join("\n");
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
    if (!settings) return;    const input = settings.input || {};
    setSwitch(el("ime-disable"), input.imeDisableBeforeSend !== false);

    const backend = settings.backend || {};
    el("backend-mode").value = backend.mode === "ahk" ? "ahk" : "builtin";
    el("backend-script").value = backend.ahkScript || "";
    el("backend-executable").value = backend.ahkExecutable || "";
    setSwitch(el("backend-generate"), backend.generateScript !== false);
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

    if (!settings.backend) settings.backend = {};
    const backend = settings.backend;
    backend.mode = el("backend-mode").value === "ahk" ? "ahk" : "builtin";
    backend.ahkScript = el("backend-script").value.trim();
    backend.ahkExecutable = el("backend-executable").value.trim();
    backend.generateScript = el("backend-generate").getAttribute("aria-checked") === "true";

    if (!settings.gameProtection) settings.gameProtection = {};
    const protection = settings.gameProtection;
    protection.enabled = el("protection-enabled").getAttribute("aria-checked") === "true";
    protection.pollIntervalMs = Number(el("poll-interval").value);
    protection.resumeDelayMs = Number(el("resume-delay").value);
    protection.stopTriggerProcessNames = lines(el("stop-triggers").value);
    protection.resumeProcessNames = lines(el("resume-processes").value);
    markDirty();
    renderProcessList();
    renderOverview();
    const summary = protection.enabled
      ? "ゲーム保護が有効です。危険なプロセスを検知すると入力エンジンを停止します。"
      : "ゲーム保護は無効です。危険なプロセスを検知しても退避しません。";
    el("protection-summary").textContent = summary;
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
      renderAll();
      showAlert("");
      setSaveState("保存済み", "ok");
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

    const backendState = el("backend-state");
    if (backendState) {
      const ahk = result.payload.backend === "ahk";
      backendState.className = "badge" + (running ? " badge-success" : "");
      backendState.textContent = ahk
        ? `AutoHotkey·${result.payload.backendStatus || "-"}`
        : `内蔵エンジン·${result.payload.engineInstalled === false ? "未設置" : "動作中"}`;
    }

    // AHK 使用中は、この画面のプロファイルが使われないことを明示する。
    const backendNotice = el("profile-backend-notice");
    if (backendNotice) backendNotice.hidden = result.payload.backend !== "ahk";

    const backendDetail = el("backend-detail");
    if (backendDetail) {
      const note = result.payload.backendNote || "";
      backendDetail.textContent = result.payload.backend === "ahk"
        ? `PID ${result.payload.backendPid ?? "-"} · 再起動 ${result.payload.backendRestarts ?? 0} 回${note ? " · " + note : ""}`
        : "内蔵フックを設置しています。";
    }

    const generated = el("backend-generated");
    if (generated) generated.textContent = result.payload.backendGenerated || "（書き出しなし）";

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

  ["protection-enabled", "ime-disable", "backend-generate"].forEach((id) => {
    el(id).addEventListener("click", () => {
      const button = el(id);
      setSwitch(button, button.getAttribute("aria-checked") !== "true");
      syncSettingsFromForms();
    });
  });
  ["poll-interval", "resume-delay", "stop-triggers", "resume-processes", "backend-script", "backend-executable"].forEach((id) => {
    el(id).addEventListener("input", syncSettingsFromForms);
    el(id).addEventListener("change", syncSettingsFromForms);
  });
  el("backend-mode").addEventListener("change", syncSettingsFromForms);
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
  el("profile-dialog").addEventListener("close", () => { editingProfileIndex = null; });

  el("save").addEventListener("click", save);
  el("reload").addEventListener("click", () => { loadSettings(); loadStatus(); loadEventLog(); });
  el("restore").addEventListener("click", restore);

  root.dataset.theme = (() => {
    try {
      const saved = localStorage.getItem("leafhotkey.theme");
      return saved === "dark" || saved === "light" ? saved : "light";
    } catch (_) {
      return "light";
    }
  })();
  updateThemeIcon();
  showView("overview");
  loadSettings();
  loadStatus();
  loadEventLog();
  window.setInterval(loadStatus, 5000);
})();
