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
    settings: { section: "システム", current: "設定 JSON" },
  };

  let settings = null;
  let revision = null;
  let editingJson = false;
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
    row.className = compact ? "list-row" : "settings-row";

    const icon = document.createElement("div");
    icon.className = "app-icon";
    icon.setAttribute("aria-hidden", "true");
    icon.textContent = profileInitials(profile);

    const main = document.createElement("div");
    main.className = compact ? "list-primary" : "settings-row-main";
    const name = document.createElement(compact ? "strong" : "strong");
    name.textContent = profile.name || profile.id || "名称未設定";
    const meta = document.createElement("span");
    meta.textContent = profileMeta(profile);
    if (compact) main.append(name, meta);
    else {
      const detail = document.createElement("p");
      detail.textContent = profileMeta(profile);
      main.append(name, detail);
    }

    const badge = document.createElement("span");
    badge.className = "badge " + (profile.enabled === false ? "" : "badge-success");
    badge.textContent = profileStatus(profile);

    if (compact) {
      const side = document.createElement("div");
      side.className = "list-side";
      side.append(badge);
      row.append(icon, main, side);
    } else {
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "switch";
      toggle.setAttribute("role", "switch");
      toggle.setAttribute("aria-label", `${profile.name || profile.id || "プロファイル"}を有効にする`);
      setSwitch(toggle, profile.enabled !== false);
      toggle.addEventListener("click", () => {
        profile.enabled = toggle.getAttribute("aria-checked") !== "true";
        setSwitch(toggle, profile.enabled);
        syncJsonFromSettings();
        renderProfiles();
        renderOverview();
      });
      row.append(icon, main, badge, toggle);
    }
    return row;
  }

  function renderProfiles() {
    if (!settings) return;
    const profiles = settings.profiles || [];
    const enabled = profiles.filter((profile) => profile.enabled !== false).length;
    const countText = `${profiles.length}件のプロファイル · 有効 ${enabled}件`;

    el("nav-profile-count").textContent = String(profiles.length);
    el("profile-count-label").textContent = countText;

    const list = el("profile-list");
    list.textContent = "";
    profiles.forEach((profile) => list.append(makeProfileRow(profile, false)));
    el("profile-empty").hidden = profiles.length !== 0;

    const overview = el("overview-profiles");
    overview.textContent = "";
    profiles.slice(0, 4).forEach((profile) => overview.append(makeProfileRow(profile, true)));
    if (profiles.length === 0) {
      const empty = document.createElement("div");
      empty.className = "empty-state";
      empty.textContent = "プロファイルがありません。";
      overview.append(empty);
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

  function renderAll() {
    if (!settings) return;
    if (!editingJson) el("json").value = JSON.stringify(settings, null, 2);
    el("settings-revision").textContent = revision || "-";
    renderSafety();
    renderProfiles();
    renderOverview();
  }

  function syncJsonFromSettings() {
    if (!settings) return;
    if (!editingJson) el("json").value = JSON.stringify(settings, null, 2);
    setSaveState("未保存の変更があります");
  }

  function syncJsonFromForm() {
    if (!settings) return;
    if (!settings.gameProtection) settings.gameProtection = {};
    const protection = settings.gameProtection;
    protection.enabled = el("protection-enabled").getAttribute("aria-checked") === "true";
    protection.pollIntervalMs = Number(el("poll-interval").value);
    protection.resumeDelayMs = Number(el("resume-delay").value);
    protection.stopTriggerProcessNames = lines(el("stop-triggers").value);
    protection.resumeProcessNames = lines(el("resume-processes").value);
    syncJsonFromSettings();
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
      editingJson = false;
      renderAll();
      showAlert("");
      setSaveState("保存済み", "ok");
    } catch (error) {
      showAlert(`設定JSONを読み込めませんでした: ${error.message}`);
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
    let json = el("json").value;
    if (!editingJson) {
      syncJsonFromForm();
      json = el("json").value;
    }

    try {
      JSON.parse(json);
    } catch (error) {
      showAlert(`JSON として読めません: ${error.message}`);
      setSaveState("保存していません", "error");
      return;
    }

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
  document.querySelectorAll("[data-theme-toggle]").forEach((button) => button.addEventListener("click", () => {
    root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
    try { localStorage.setItem("leafhotkey.theme", root.dataset.theme); } catch (_) { /* 保存できないブラウザもある */ }
    updateThemeIcon();
  }));

  el("protection-enabled").addEventListener("click", () => {
    const button = el("protection-enabled");
    setSwitch(button, button.getAttribute("aria-checked") !== "true");
    syncJsonFromForm();
  });
  ["poll-interval", "resume-delay", "stop-triggers", "resume-processes"].forEach((id) => {
    el(id).addEventListener("input", syncJsonFromForm);
    el(id).addEventListener("change", syncJsonFromForm);
  });
  el("json").addEventListener("input", () => {
    editingJson = true;
    setSaveState("JSON を直接編集しています");
  });
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
