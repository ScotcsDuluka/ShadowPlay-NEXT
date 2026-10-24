"use strict";
(() => {
  const el = (id) => document.getElementById(id);
  const tokenKey = "duluka.staging.session.v1";
  const deviceKeyName = "duluka.staging.device.v1";
  const state = { token: "", username: "", accepting: false, application: null, busy: false };
  try { state.token = sessionStorage.getItem(tokenKey) || ""; } catch { /* private browsing */ }

  function notify(id, text, kind = "") {
    const node = el(id);
    node.textContent = text;
    node.className = "message" + (kind ? " " + kind : "");
  }
  function storeToken(token) {
    state.token = token || "";
    try {
      if (token) sessionStorage.setItem(tokenKey, token);
      else sessionStorage.removeItem(tokenKey);
    } catch { /* browser storage disabled */ }
  }
  function deviceKey() {
    let key = "";
    try { key = localStorage.getItem(deviceKeyName) || ""; } catch { /* ignored */ }
    if (/^[A-Za-z0-9_-]{43}$/.test(key)) return key;
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    key = btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
    try { localStorage.setItem(deviceKeyName, key); } catch { /* ephemeral browser */ }
    return key;
  }
  async function api(path, method = "GET", payload) {
    const headers = { Accept: "application/json", "X-ReqId": crypto.randomUUID() };
    if (payload !== undefined) headers["Content-Type"] = "application/json";
    if (state.token) headers.Authorization = "Bearer " + state.token;
    const options = { method, headers, credentials: "omit", cache: "no-store", redirect: "error" };
    if (payload !== undefined) options.body = JSON.stringify(payload);
    const response = await fetch(path, options);
    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.ok === false) {
      const error = new Error(result.message || ("Request failed (" + response.status + ")."));
      error.status = response.status;
      throw error;
    }
    return result;
  }
  function setBusy(busy) {
    state.busy = busy;
    document.querySelectorAll("form button[type=submit]").forEach((button) => { button.disabled = busy; });
  }
  function render() {
    const loggedIn = Boolean(state.token);
    el("auth-panel").hidden = loggedIn;
    el("signed-in-panel").hidden = !loggedIn;
    el("account-state").textContent = loggedIn ? "SIGNED IN" : "NOT SIGNED IN";
    el("account-state").classList.toggle("active", loggedIn);
    el("current-username").textContent = state.username || "Tester";
    el("cbt-state").textContent = state.accepting ? "APPLICATIONS OPEN" : "CLOSED";
    el("cbt-state").classList.toggle("active", state.accepting);
    el("cbt-explanation").textContent = state.accepting
      ? "Accepting applications from signed-in testers."
      : "Applications are not open. You can still test username sign-in.";
    const hasApplication = loggedIn && !!state.application;
    el("application-summary").hidden = !hasApplication;
    if (hasApplication) {
      el("application-decision").textContent = String(state.application.decision || "pending").toUpperCase();
      el("application-description").textContent = "Application received for " +
        (state.application.alias || state.username || "Tester") + ".";
    }
    el("apply-form").hidden = !loggedIn || !state.accepting || hasApplication;
    if (!state.accepting) notify("cbt-message", "CBT1 registrations are closed on this staging server.");
    else if (!loggedIn) notify("cbt-message", "Sign in to access the application form.");
    else if (hasApplication) notify("cbt-message", "Your application has been recorded.", "success");
    else notify("cbt-message", "Use test-only information while this environment is in staging.");
  }
  function selectAuth(which) {
    const login = which === "login";
    el("login-form").hidden = !login;
    el("register-form").hidden = login;
    el("show-login").classList.toggle("active", login);
    el("show-register").classList.toggle("active", !login);
    el("show-login").setAttribute("aria-pressed", String(login));
    el("show-register").setAttribute("aria-pressed", String(!login));
    notify("auth-message", "");
  }
  async function refreshApplication() {
    if (!state.token) { state.application = null; render(); return; }
    try {
      const response = await api("/v1/cbt1/me");
      state.username = response.username || state.username;
      state.application = response.application || null;
    } catch (error) {
      if (error.status === 401 || error.status === 403) {
        storeToken("");
        state.application = null;
        state.username = "";
      } else {
        notify("cbt-message", "Unable to refresh application: " + error.message, "error");
      }
    }
    render();
  }
  async function signIn(event, mode) {
    event.preventDefault();
    if (state.busy) return;
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const username = form.elements.username.value.trim();
    const password = form.elements.password.value;
    setBusy(true);
    notify("auth-message", mode === "register" ? "Creating your test account…" : "Signing in…");
    try {
      const reply = await api("/v1/auth/" + mode, "POST", {
        username, password, deviceName: "Tester Web Portal", deviceKey: deviceKey()
      });
      if (!reply.resource || !reply.resource.sessionToken) throw new Error("Server did not return a session.");
      storeToken(reply.resource.sessionToken);
      state.username = reply.resource.username || username;
      form.elements.password.value = "";
      notify("auth-message", "Signed in successfully.", "success");
      await refreshApplication();
    } catch (error) {
      notify("auth-message", error.message, "error");
    } finally {
      setBusy(false);
    }
  }
  async function apply(event) {
    event.preventDefault();
    if (state.busy || !state.token || !state.accepting) return;
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const payload = {
      alias: form.elements.alias.value.trim(),
      email: form.elements.email.value.trim(),
      windowsVersion: form.elements.windowsVersion.value,
      gpuVendor: form.elements.gpuVendor.value,
      testingFocus: form.elements.testingFocus.value,
      notes: form.elements.notes.value.trim(),
      website: form.elements.website.value,
      consent: form.elements.consent.checked
    };
    setBusy(true);
    notify("cbt-message", "Submitting your application…");
    try {
      const response = await api("/v1/cbt1/apply", "POST", payload);
      state.application = response.application || null;
      notify("cbt-message", "Application submitted.", "success");
      await refreshApplication();
    } catch (error) {
      notify("cbt-message", error.message, "error");
    } finally {
      setBusy(false);
    }
  }
  async function signOut() {
    const oldToken = state.token;
    if (oldToken) {
      try { await api("/v1/auth/session/revoke", "POST"); } catch { /* revoke best-effort */ }
    }
    storeToken("");
    state.username = "";
    state.application = null;
    render();
    selectAuth("login");
    notify("auth-message", "Signed out of this browser tab.", "success");
  }
  el("show-login").addEventListener("click", () => selectAuth("login"));
  el("show-register").addEventListener("click", () => selectAuth("register"));
  el("login-form").addEventListener("submit", (event) => signIn(event, "login"));
  el("register-form").addEventListener("submit", (event) => signIn(event, "register"));
  el("apply-form").addEventListener("submit", apply);
  el("sign-out").addEventListener("click", signOut);

  (async function init() {
    render();
    try {
      const result = await api("/v1/cbt1/status");
      state.accepting = result.accepting === true;
    } catch (error) {
      notify("cbt-message", "Unable to check CBT1 status: " + error.message, "error");
    }
    if (state.token) {
      try {
        const me = await api("/v1/account/me");
        state.username = me.resource?.username || state.username;
        await refreshApplication();
      } catch {
        storeToken("");
        state.username = "";
      }
    }
    render();
  })();
})();
