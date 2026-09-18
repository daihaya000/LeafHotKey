"use strict";

(() => {
  const pageUrl = new URL(window.location.href);
  const token = pageUrl.searchParams.get("token") || "";
  // HTML 自体も認証必須。URLから消すとF5で401になるため保持する。
  // トークンは起動ごとに失効する。URLを共有せず、no-referrerを維持する。

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
          "X-LeafHotKey-Token": token,
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
      image.src = `api/icon?name=${encodeURIComponent(processName)}&token=${encodeURIComponent(token)}`;
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

  function renderRuleActionFields(rule, action) {
    const fields = rule.querySelector(".rule-action-fields");
    fields.textContent = "";
    if (action === "send") {
      const sequence = document.createElement("label");
      sequence.className = "field field-full";
      sequence.textContent = "送るキー列（1行に1操作）";
      const input = document.createElement("textarea");
      input.rows = 2;
      input.dataset.ruleSequence = "";
      input.value = rule.dataset.sequence || "";
      sequence.append(input);
      fields.append(sequence);
      return;
    }
    if (action === "hold") {
      const modifier = makeInput("維持するキー", "", rule.dataset.modifier || "");
      modifier.input.dataset.ruleModifier = "";
      const release = makeInput("解除するキー", "", rule.dataset.releaseOn || "");
      release.input.dataset.ruleReleaseOn = "";
      const blind = document.createElement("label");
      blind.className = "check-field";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = rule.dataset.blind !== "false";
      checkbox.dataset.ruleBlind = "";
      blind.append(checkbox, document.createTextNode("他の修飾キーを維持する"));
      fields.append(modifier.field, release.field, blind);
    }
  }

  function renderRuleEditor(ruleData = {}) {
    const rule = document.createElement("section");
    rule.className = "rule-editor";
    const trigger = ruleData.trigger || {};
    const action = ruleData.action || { type: "send", sequence: [] };
    rule.dataset.sequence = (action.sequence || []).join("\n");
    rule.dataset.modifier = action.modifier || "";
    rule.dataset.releaseOn = action.releaseOn || "";
    rule.dataset.blind = action.blind === false ? "false" : "true";

    const header = document.createElement("div");
    header.className = "rule-header";
    const title = document.createElement("strong");
    title.textContent = "ショートカット";
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "text-link rule-remove";
    remove.textContent = "削除";
    remove.addEventListener("click", () => rule.remove());
    header.append(title, remove);

    const grid = document.createElement("div");
    grid.className = "rule-grid";
    const key = makeInput("トリガキー", "", trigger.key || "");
    key.input.required = true;
    key.input.dataset.ruleKey = "";
    const prefix = makeInput("前置キー", "", trigger.prefix || "");
    prefix.input.dataset.rulePrefix = "";
    const modifiers = makeInput("修飾キー", "", (trigger.modifiers || []).join(" + "));
    modifiers.input.dataset.ruleModifiers = "";
    modifiers.field.querySelector("input").placeholder = "Ctrl + Shift";
    const actionType = document.createElement("label");
    actionType.className = "field";
    actionType.textContent = "動作";
    const select = document.createElement("select");
    select.dataset.ruleAction = "";
    [["send", "キーを送る"], ["hold", "キーを維持"], ["passthrough", "元の入力を通す"]].forEach(([value, label]) => {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      option.selected = action.type === value;
      select.append(option);
    });
    actionType.append(select);

    const flags = document.createElement("div");
    flags.className = "rule-flags";
    [["any", "修飾キーを問わない", trigger.anyModifier], ["pass", "元の入力も通す", trigger.passThroughNative]].forEach(([name, label, checked]) => {
      const field = document.createElement("label");
      field.className = "check-field";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = checked === true;
      checkbox.dataset[name === "any" ? "ruleAnyModifier" : "rulePassThrough"] = "";
      field.append(checkbox, document.createTextNode(label));
      flags.append(field);
    });

    const actionFields = document.createElement("div");
    actionFields.className = "rule-action-fields";
    grid.append(key.field, prefix.field, modifiers.field, actionType, flags, actionFields);
    rule.append(header, grid);
    select.addEventListener("change", () => renderRuleActionFields(rule, select.value));
    renderRuleActionFields(rule, action.type || "send");
    return rule;
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
    const list = el("rule-list");
    list.textContent = "";
    (profile.rules || []).forEach((rule) => list.append(renderRuleEditor(rule)));
    el("profile-dialog").showModal();
  }

  function readRuleEditors() {
    return [...el("rule-list").querySelectorAll(".rule-editor")].map((row) => {
      const get = (name) => row.querySelector(`[data-${name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`)}]`);
      const key = get("ruleKey").value.trim();
      const type = get("ruleAction").value;
      if (!key) throw new Error("トリガキーを入力してください。");
      const trigger = { key };
      const prefix = get("rulePrefix").value.trim();
      const modifiers = get("ruleModifiers").value.split(/[+,\s]+/).filter(Boolean);
      if (prefix) trigger.prefix = prefix;
      if (modifiers.length) trigger.modifiers = modifiers;
      if (get("ruleAnyModifier").checked) trigger.anyModifier = true;
      if (get("rulePassThrough").checked) trigger.passThroughNative = true;
      const action = { type };
      if (type === "send") {
        action.sequence = lines(get("ruleSequence").value);
        if (!action.sequence.length) throw new Error(`${key} に送るキー列を入力してください。`);
      }
      if (type === "hold") {
        action.modifier = get("ruleModifier").value.trim();
        action.releaseOn = get("ruleReleaseOn").value.trim();
        action.blind = get("ruleBlind").checked;
        if (!action.modifier || !action.releaseOn) throw new Error(`${key} の維持するキーと解除するキーを入力してください。`);
      }
      return { trigger, action };
    });
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
      profile.rules = readRuleEditors();
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
    if (!settings) return;
    const input = settings.input || {};
    setSwitch(el("ime-disable"), input.imeDisableBeforeSend !== false);
    el("send-delay").value = input.sendDelayMs ?? 2;
    setSwitch(el("default-blind"), input.defaultBlind !== false);
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
    input.sendDelayMs = Number(el("send-delay").value);
    input.defaultBlind = el("default-blind").getAttribute("aria-checked") === "true";

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
  el("profile-search").addEventListener("input", renderProfiles);
  document.querySelectorAll("[data-theme-toggle]").forEach((button) => button.addEventListener("click", () => {
    root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
    try { localStorage.setItem("leafhotkey.theme", root.dataset.theme); } catch (_) { /* 保存できないブラウザもある */ }
    updateThemeIcon();
  }));

  ["protection-enabled", "ime-disable", "default-blind"].forEach((id) => {
    el(id).addEventListener("click", () => {
      const button = el(id);
      setSwitch(button, button.getAttribute("aria-checked") !== "true");
      syncSettingsFromForms();
    });
  });
  ["poll-interval", "resume-delay", "stop-triggers", "resume-processes", "send-delay"].forEach((id) => {
    el(id).addEventListener("input", syncSettingsFromForms);
    el(id).addEventListener("change", syncSettingsFromForms);
  });
  el("profile-enabled").addEventListener("click", () => {
    const button = el("profile-enabled");
    setSwitch(button, button.getAttribute("aria-checked") !== "true");
  });
  el("add-rule").addEventListener("click", () => el("rule-list").append(renderRuleEditor()));
  el("apply-profile").addEventListener("click", applyProfileEditor);
  el("profile-dialog").addEventListener("close", () => { editingProfileIndex = null; });

  el("save").addEventListener("click", save);
  el("reload").addEventListener("click", () => { loadSettings(); loadStatus(); });
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
  window.setInterval(loadStatus, 5000);
})();
