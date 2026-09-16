"use strict";

(() => {
  // トークンは URL から一度だけ取り出し、アドレスバーには残さない。
  const url = new URL(window.location.href);
  const token = url.searchParams.get("token") || "";
  if (url.searchParams.has("token")) {
    url.searchParams.delete("token");
    window.history.replaceState(null, "", url.pathname + url.search);
  }

  const el = (id) => document.getElementById(id);
  const alertBox = el("alert");
  const saveState = el("save-state");

  /** 現在の設定。保存時はこのオブジェクトを書き戻す。 */
  let settings = null;
  let revision = null;
  let editingJson = false;

  function showAlert(message) {
    if (!message) {
      alertBox.hidden = true;
      alertBox.textContent = "";
      return;
    }
    alertBox.hidden = false;
    alertBox.textContent = message;
  }

  function setSaveState(message, kind) {
    saveState.textContent = message;
    saveState.className = "save-state" + (kind ? " " + kind : "");
  }

  async function api(path, options) {
    const response = await fetch(path, {
      ...options,
      headers: {
        "X-LeafHotKey-Token": token,
        ...(options && options.body ? { "Content-Type": "application/json" } : {}),
        ...((options && options.headers) || {}),
      },
    });

    let payload = null;
    const text = await response.text();
    if (text) {
      try {
        payload = JSON.parse(text);
      } catch (_) {
        payload = { message: text };
      }
    }

    return { status: response.status, payload };
  }

  function lines(value) {
    return value
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line.length > 0);
  }

  function renderProfiles() {
    const container = el("profiles");
    container.textContent = "";

    for (const profile of settings.profiles || []) {
      const row = document.createElement("div");
      row.className = "profile";

      const main = document.createElement("div");
      main.className = "profile-main";

      const name = document.createElement("div");
      name.className = "profile-name";
      name.textContent = profile.name || profile.id;

      const meta = document.createElement("div");
      meta.className = "profile-meta";
      const processes = (profile.processNames || []).join(" / ");
      const ruleCount = (profile.rules || []).length;
      meta.textContent = `${processes} · ${ruleCount} ルール`;

      main.append(name, meta);

      const toggle = document.createElement("input");
      toggle.type = "checkbox";
      toggle.checked = profile.enabled !== false;
      toggle.setAttribute("aria-label", `${profile.name || profile.id} を有効にする`);
      toggle.addEventListener("change", () => {
        profile.enabled = toggle.checked;
        syncJsonFromForm();
      });

      row.append(main, toggle);
      container.append(row);
    }
  }

  function renderForm() {
    const protection = settings.gameProtection || {};
    el("protection-enabled").checked = protection.enabled !== false;
    el("poll-interval").value = protection.pollIntervalMs ?? 1000;
    el("resume-delay").value = protection.resumeDelayMs ?? 1000;
    el("stop-triggers").value = (protection.stopTriggerProcessNames || []).join("\n");
    el("resume-processes").value = (protection.resumeProcessNames || []).join("\n");
    renderProfiles();
  }

  /** フォームの内容を設定オブジェクトと JSON 表示へ反映する。 */
  function syncJsonFromForm() {
    if (!settings) return;
    if (!settings.gameProtection) settings.gameProtection = {};

    settings.gameProtection.enabled = el("protection-enabled").checked;
    settings.gameProtection.pollIntervalMs = Number(el("poll-interval").value);
    settings.gameProtection.resumeDelayMs = Number(el("resume-delay").value);
    settings.gameProtection.stopTriggerProcessNames = lines(el("stop-triggers").value);
    settings.gameProtection.resumeProcessNames = lines(el("resume-processes").value);

    if (!editingJson) el("json").value = JSON.stringify(settings, null, 2);
    setSaveState("未保存の変更があります");
  }

  async function loadStatus() {
    const { status, payload } = await api("/api/status");
    if (status !== 200 || !payload) {
      el("state-dot").className = "dot error";
      el("state-text").textContent = "状態を取得できません";
      return;
    }

    const state = payload.state || "unknown";
    el("state-dot").className = "dot " + (state === "running" ? "running" : state === "paused" ? "paused" : "error");
    el("state-text").textContent =
      state === "running" ? "入力変換は有効です" : state === "paused" ? "一時停止中" : "停止中";

    el("fact-state").textContent =
      payload.engineInstalled === false ? "フックを設置できていません" : el("state-text").textContent;
    el("fact-foreground").textContent = payload.activeProfile || "（対象外）";
  }

  async function loadSettings() {
    const { status, payload } = await api("/api/settings");
    if (status !== 200 || !payload) {
      showAlert("設定を取得できませんでした。");
      setSaveState("読み込み失敗", "error");
      return;
    }

    settings = JSON.parse(payload.json);
    revision = payload.revision;
    editingJson = false;

    el("json").value = JSON.stringify(settings, null, 2);
    el("fact-revision").textContent = revision;
    el("fact-profiles").textContent = String((settings.profiles || []).length);
    el("fact-rules").textContent = String(
      (settings.profiles || []).reduce((total, profile) => total + (profile.rules || []).length, 0),
    );

    renderForm();
    showAlert("");
    setSaveState("保存済み", "ok");
  }

  async function save() {
    let json = el("json").value;
    if (!editingJson) {
      syncJsonFromForm();
      json = el("json").value;
    }

    try {
      JSON.parse(json);
    } catch (error) {
      showAlert("JSON として読めません: " + error.message);
      setSaveState("保存していません", "error");
      return;
    }

    setSaveState("保存中…");
    const { status, payload } = await api("/api/settings", {
      method: "POST",
      body: JSON.stringify({ revision, json }),
    });

    if (status === 200) {
      revision = (payload && payload.revision) || revision;
      showAlert("");
      setSaveState("保存して反映しました", "ok");
      await loadSettings();
      await loadStatus();
      return;
    }

    if (status === 409) {
      showAlert("他の更新が先に保存されています。再読み込みしてからやり直してください。");
    } else {
      showAlert((payload && payload.message) || `保存に失敗しました（${status}）。`);
    }

    setSaveState("保存していません", "error");
  }

  async function restore() {
    if (!window.confirm("設定を既定値に戻します。よろしいですか？")) return;

    setSaveState("戻しています…");
    const { status, payload } = await api("/api/settings/restore", { method: "POST", body: "{}" });
    if (status === 200) {
      showAlert("");
      await loadSettings();
      setSaveState("既定値に戻しました", "ok");
      return;
    }

    showAlert((payload && payload.message) || "既定値へ戻せませんでした。");
    setSaveState("戻していません", "error");
  }

  for (const id of ["protection-enabled", "poll-interval", "resume-delay", "stop-triggers", "resume-processes"]) {
    el(id).addEventListener("input", syncJsonFromForm);
    el(id).addEventListener("change", syncJsonFromForm);
  }

  el("json").addEventListener("input", () => {
    editingJson = true;
    setSaveState("JSON を直接編集しています");
  });

  el("save").addEventListener("click", save);
  el("reload").addEventListener("click", () => {
    loadSettings();
    loadStatus();
  });
  el("restore").addEventListener("click", restore);

  loadSettings();
  loadStatus();
  window.setInterval(loadStatus, 5000);
})();
