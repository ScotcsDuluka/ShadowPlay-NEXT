"use strict";
(() => {
  const el = (id) => document.getElementById(id);
  // The same HTTPS hostname may switch from disposable staging to real accounts.
  // Separate credentials and device IDs so a staging browser token is never
  // mistakenly offered to the real account backend.
  let tokenKey = "duluka.staging.session.v1";
  let deviceKeyName = "duluka.staging.device.v1";
  const state = { token: "", username: "", accepting: false, invited: false, invitesEnabled: false, application: null, busy: false, mode: "staging", registrationEnabled: false, configReady: false, account: null, devices: [] };

  function setAccountMode(mode, allowRegistration) {
    state.mode = mode === "main" ? "main" : "staging";
    state.registrationEnabled = state.mode !== "main" || allowRegistration === true;
    el("show-register").hidden = !state.registrationEnabled;
    el("register-form").hidden = !state.registrationEnabled;
    tokenKey = "duluka." + state.mode + ".session.v1";
    deviceKeyName = "duluka." + state.mode + ".device.v1";
    try { state.token = sessionStorage.getItem(tokenKey) || ""; } catch { state.token = ""; }
    if (state.mode !== "main") return;
    el("environment-label").textContent = "CONNECTED ACCOUNT SERVER";
    el("hero-desc").textContent =
      "Sign in with your existing Duluka Account. This portal uses the same account database as the ShadowPlay NEXT desktop client.";
    el("account-supporting").textContent =
      state.registrationEnabled ? "One account for ShadowPlay NEXT and the web portal." :
      "Existing accounts only. Public registrations are not open yet.";
    el("register-submit").firstChild.textContent = "CREATE ACCOUNT ";
    el("session-notice").textContent =
      "This browser tab keeps its own session. Your Duluka Account and username remain the same across supported clients.";
    el("portal-warning").textContent =
      "CONNECTED ACCOUNT SERVER. This login uses the main Duluka Account database. Keep your password private. CBT1 applications remain closed until officially enabled.";
    el("footer-environment").textContent = "DULUKA ACCOUNT · CBT1 PREVIEW";
  }

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
    renderAccountDetails();
    const canApply = state.accepting || (state.invitesEnabled && state.invited);
    el("cbt-state").textContent = state.accepting ? "APPLICATIONS OPEN" : state.invited ? "INVITED" : state.invitesEnabled ? "INVITE ONLY" : "CLOSED";
    el("cbt-state").classList.toggle("active", canApply);
    el("cbt-explanation").textContent = state.accepting
      ? "Accepting applications from signed-in testers."
      : state.invited ? "Your invitation is active. Submit your CBT1 application." :
        state.invitesEnabled ? "Invite-only testing: sign in and redeem a code." :
        "Applications are not open. You can still test username sign-in.";
    const hasApplication = loggedIn && !!state.application;
    el("application-summary").hidden = !hasApplication;
    if (hasApplication) {
      el("application-decision").textContent = String(state.application.decision || "pending").toUpperCase();
      el("application-description").textContent = "Application received for " +
        (state.application.alias || state.username || "Tester") + ".";
    }
    el("invite-form").hidden = !loggedIn || !state.invitesEnabled || state.invited || hasApplication;
    el("apply-form").hidden = !loggedIn || !canApply || hasApplication;
    if (!canApply && state.invitesEnabled) notify("cbt-message", loggedIn ?
      "Enter a valid invitation to unlock your application." : "Sign in to redeem your CBT1 invitation.");
    else if (!canApply) notify("cbt-message", "CBT1 applications are currently closed.");
    else if (!loggedIn) notify("cbt-message", "Sign in to access the application form.");
    else if (hasApplication) notify("cbt-message", "Your application has been recorded.", "success");
    else notify("cbt-message", state.mode === "main" ? "Review your application carefully before submitting." : "Use test-only information while this environment is in staging.");
  }
  function selectAuth(which) {
    const login = which === "login";
    if (!state.configReady || (which === "register" && !state.registrationEnabled)) return;
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
      state.invited = response.invited === true;
    } catch (error) {
      if (error.status === 401 || error.status === 403) {
        storeToken("");
        state.application = null;
        state.account = null;
        state.devices = [];
        state.username = "";
      } else {
        notify("cbt-message", "Unable to refresh application: " + error.message, "error");
      }
    }
    render();
  }
  async function signIn(event, mode) {
    event.preventDefault();
    if (state.busy || !state.configReady) return;
    const form = event.currentTarget;
    if (!form.reportValidity() || (mode === "register" && !state.registrationEnabled)) return;
    const username = form.elements.username.value.trim();
    const password = form.elements.password.value;
    if (mode === "register" && form.elements.confirmPassword.value !== password) {
      notify("auth-message", "Passwords do not match. Please try again.", "error");
      form.elements.confirmPassword.setAttribute("aria-invalid", "true");
      form.elements.confirmPassword.focus();
      return;
    }
    if (mode === "register") form.elements.confirmPassword.removeAttribute("aria-invalid");
    setBusy(true);
    notify("auth-message", mode === "register" ? "Creating your account…" : "Signing in…");
    try {
      const reply = await api("/v1/auth/" + mode, "POST", {
        username, password, deviceName: "Tester Web Portal", deviceKey: deviceKey()
      });
      if (!reply.resource || !reply.resource.sessionToken) throw new Error("Server did not return a session.");
      storeToken(reply.resource.sessionToken);
      state.username = reply.resource.username || username;
      form.elements.password.value = "";
      if (mode === "register") form.elements.confirmPassword.value = "";
      notify("auth-message", "Signed in successfully.", "success");
      await refreshAccount();
      if (state.token) await refreshApplication();
    } catch (error) {
      notify("auth-message", error.message, "error");
    } finally {
      setBusy(false);
    }
  }
  async function apply(event) {
    event.preventDefault();
    if (state.busy || !state.token || !(state.accepting || (state.invitesEnabled && state.invited))) return;
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
  // Account management is always session-scoped. Values are displayed with
  // textContent, never inserted as HTML or copied into browser storage.
  function renderAccountDetails() {
    const a = state.account;
    el("account-display-name").textContent = a?.displayName || a?.username || "—";
    el("account-id-short").textContent = a?.accountId
      ? a.accountId.slice(0, 16) + "…" + a.accountId.slice(-6) : "—";
    const created = a?.createdAt ? new Date(a.createdAt) : null;
    el("account-created-at").textContent = created && !Number.isNaN(created.getTime())
      ? created.toLocaleDateString() : "—";
    el("account-current-device").textContent = a?.currentDevice?.deviceName || "—";
    const list = el("account-device-list");
    list.replaceChildren();
    if (!state.token || !a) {
      const p = document.createElement("p");
      p.className = "micro-copy";
      p.textContent = state.token ? "Loading your account…" : "Sign in to view your devices.";
      list.append(p);
      return;
    }
    if (!state.devices.length) {
      const p = document.createElement("p");
      p.className = "micro-copy";
      p.textContent = "No devices available.";
      list.append(p);
      return;
    }
    for (const device of state.devices) {
      const row = document.createElement("div");
      row.className = "device-row";
      const copy = document.createElement("div");
      copy.className = "device-copy";
      const title = document.createElement("strong");
      title.textContent = device.deviceName || "Unknown device";
      const detail = document.createElement("small");
      const lastSeen = device.lastSeenAt ? new Date(device.lastSeenAt) : null;
      detail.textContent = device.revokedAt
        ? "Disconnected"
        : lastSeen && !Number.isNaN(lastSeen.getTime())
          ? "Last seen " + lastSeen.toLocaleString() : "Connected device";
      copy.append(title, detail);
      const actions = document.createElement("div");
      actions.className = "device-actions";
      if (device.deviceId === a.currentDevice?.deviceId) {
        const tag = document.createElement("span");
        tag.className = "device-current";
        tag.textContent = "THIS DEVICE";
        actions.append(tag);
      } else if (!device.revokedAt) {
        const disconnect = document.createElement("button");
        disconnect.type = "button";
        disconnect.className = "device-remove";
        disconnect.textContent = "DISCONNECT";
        disconnect.addEventListener("click", () => revokeDevice(device, disconnect));
        actions.append(disconnect);
      }
      row.append(copy, actions);
      list.append(row);
    }
  }

  async function refreshAccount() {
    if (!state.token) return;
    const currentToken = state.token;
    el("refresh-devices").disabled = true;
    try {
      const [me, devices] = await Promise.all([
        api("/v1/account/me"),
        api("/v1/account/devices")
      ]);
      if (state.token !== currentToken) return;
      state.account = me.resource || null;
      state.devices = Array.isArray(devices.resource?.devices) ? devices.resource.devices : [];
      state.username = state.account?.username || state.username;
      notify("device-message", "");
    } catch (error) {
      if (state.token !== currentToken) return;
      if (error.status === 401 || error.status === 403) {
        storeToken("");
        state.username = "";
        state.account = null;
        state.devices = [];
        state.application = null;
        notify("auth-message", "Session expired. Please sign in again.", "error");
      } else {
        notify("device-message", "Could not load account devices: " + error.message, "error");
      }
    } finally {
      el("refresh-devices").disabled = false;
      render();
    }
  }

  async function revokeDevice(device, button) {
    if (!state.token || state.busy || device.deviceId === state.account?.currentDevice?.deviceId) return;
    if (!window.confirm("Disconnect " + (device.deviceName || "this device") +
      "? Its signed-in sessions will be revoked. This cannot be undone.")) return;
    button.disabled = true;
    try {
      await api("/v1/account/devices/" + encodeURIComponent(device.deviceId) + "/revoke", "POST", {});
      notify("device-message", "Device disconnected.", "success");
      await refreshAccount();
    } catch (error) {
      notify("device-message", "Unable to disconnect device: " + error.message, "error");
      button.disabled = false;
    }
  }

  async function changePassword(event) {
    event.preventDefault();
    if (!state.token || state.busy) return;
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const currentPassword = form.elements.currentPassword.value;
    const newPassword = form.elements.newPassword.value;
    if (newPassword !== form.elements.confirmPassword.value) {
      notify("password-message", "New passwords do not match.", "error");
      return;
    }
    if (currentPassword === newPassword) {
      notify("password-message", "Choose a different new password.", "error");
      return;
    }
    setBusy(true);
    try {
      await api("/v1/account/password", "POST", { currentPassword, newPassword });
      form.reset();
      el("password-details").open = false;
      notify("password-message", "Password updated successfully.", "success");
    } catch (error) {
      notify("password-message", "Password change failed: " + error.message, "error");
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
    state.invited = false;
    state.account = null;
    state.devices = [];
    el("change-password-form").reset();
    el("password-details").open = false;
    notify("password-message", "");
    notify("device-message", "");
    render();
    selectAuth("login");
    notify("auth-message", "Signed out of this browser tab.", "success");
  }
  el("show-login").addEventListener("click", () => selectAuth("login"));
  el("show-register").addEventListener("click", () => selectAuth("register"));
  el("login-form").addEventListener("submit", (event) => signIn(event, "login"));
  el("register-form").addEventListener("submit", (event) => signIn(event, "register"));
  el("apply-form").addEventListener("submit", apply);
  el("invite-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (!state.token || !state.invitesEnabled || state.busy) return;
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    setBusy(true);
    try {
      await api("/v1/cbt1/invite/redeem", "POST", { code:form.elements.code.value.trim() });
      form.reset();
      await refreshApplication();
      notify("cbt-message", "Invitation accepted! You can now apply for CBT1.", "success");
    } catch (error) {
      notify("cbt-message", "Invitation not accepted: " + error.message, "error");
    } finally { setBusy(false); }
  });
  el("sign-out").addEventListener("click", signOut);
  el("refresh-devices").addEventListener("click", refreshAccount);
  el("change-password-form").addEventListener("submit", changePassword);

  (async function init() {
    // Fail closed: never load a real-account token before knowing backend mode.
    try {
      const config = await api("/portal/config");
      if (config.accountMode !== "main" && config.accountMode !== "staging") {
        throw new Error("Unknown account mode");
      }
      setAccountMode(config.accountMode, config.registrationEnabled);
      state.configReady = true;
    } catch {
      // Fail closed: do not silently fall back to a staging identity when
      // the same hostname may actually be backed by the main account store.
      state.registrationEnabled = false;
      el("login-form").hidden = true;
      el("register-form").hidden = true;
      el("show-login").disabled = true;
      el("show-register").hidden = true;
      notify("auth-message", "Unable to verify which account server is connected. Please refresh.", "error");
      return;
    }
    render();
    try {
      const result = await api("/v1/cbt1/status");
      state.accepting = result.accepting === true;
      state.invitesEnabled = result.inviteEnabled === true;
    } catch (error) {
      notify("cbt-message", "Unable to check CBT1 status: " + error.message, "error");
    }
    if (state.token) {
      await refreshAccount();
      if (state.token) await refreshApplication();
    }
    render();
  })();
})();
