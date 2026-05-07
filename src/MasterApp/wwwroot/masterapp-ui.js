const masterAppState = {
  currentTab: "dashboard",
  libraryFilter: "all",
  storeFilter: "all",
  search: "",
  settingsOpen: false,
  latestStatus: null,
  latestStatusKey: "",
  latestApps: [],
  latestAppsKey: "",
  latestLogName: "app",
  latestLogText: "Choose a log to inspect.",
  latestLogKey: "",
  latestCodex: null,
  latestCodexKey: "",
  codexDraft: "",
  codexSelectedMode: "auto",
  codexSelectedWorkspace: "",
  codexSelectedModel: "",
  codexSelectedProvider: "",
  codexSelectedChatId: "",
  codexHistoryScope: "all",
  codexRecentsOpen: false,
  codexDetailsOpen: false,
  codexConnectionState: "connecting",
  codexLastError: "",
  codexEventSource: null,
  codexScrollTop: 0,
  codexStickToBottom: true,
  zoomLockBound: false,
  pageMode: document.body?.dataset.page || "dashboard"
};

const iconNames = new Set([
  "bell",
  "settings",
  "search",
  "filter",
  "refresh",
  "inbox",
  "tunnel",
  "logs",
  "home",
  "library",
  "store",
  "play",
  "external",
  "more",
  "plus",
  "close",
  "globe",
  "export",
  "trash",
  "chat"
]);

const fallbackLogos = [
  { glyph: "store", start: "#8fc3ff", end: "#4f6dff", color: "#f4f7ff" },
  { glyph: "globe", start: "#6fe0c6", end: "#2e9487", color: "#effffd" },
  { glyph: "library", start: "#ffcf86", end: "#d67a31", color: "#fff7ea" },
  { glyph: "home", start: "#ff9fb0", end: "#d55470", color: "#fff2f5" },
  { glyph: "tunnel", start: "#b59cff", end: "#6c52db", color: "#f6f1ff" },
  { glyph: "logs", start: "#7fe1ff", end: "#2f8eb5", color: "#f1fcff" }
];

const quickActions = [
  { label: "Scan inbox", icon: "inbox", action: () => postAction("/api/packages/rescan") },
  { label: "Open tunnel", icon: "tunnel", action: () => openPublic() },
  { label: "Logs", icon: "logs", action: () => setLogFocus() }
];

function initMasterApp() {
  const appRoot = document.getElementById("app");
  if (!appRoot) {
    return;
  }

  initZoomLock();

  if (masterAppState.pageMode === "store") {
    masterAppState.currentTab = "store";
  }

  renderAppShell({ preserveScroll: false });
  connectCodexEvents();
  refreshAll();
  loadLog(masterAppState.latestLogName);
  setInterval(refreshStatus, 5000);
  setInterval(refreshApps, 9000);
  setInterval(refreshCodex, 12000);
}

function renderAppShell(options = {}) {
  const { preserveScroll = true } = options;
  const appRoot = document.getElementById("app");
  if (!appRoot) {
    return;
  }

  const scrollTop = preserveScroll ? window.scrollY : 0;
  const scrollContainer = appRoot.querySelector(".app-content");
  const contentScrollTop = preserveScroll && scrollContainer instanceof HTMLElement ? scrollContainer.scrollTop : 0;
  const activeElement = document.activeElement;
  const activeId = activeElement instanceof HTMLElement ? activeElement.id : "";
  const selectionStart = activeElement && "selectionStart" in activeElement ? activeElement.selectionStart : null;
  const selectionEnd = activeElement && "selectionEnd" in activeElement ? activeElement.selectionEnd : null;

  appRoot.innerHTML = `
    <div class="app-frame">
      <div class="app-content ${masterAppState.currentTab === "codex" ? "app-content--codex" : ""}">
        ${Header()}
        ${SettingsSheet()}
        ${renderDashboardPage()}
        ${renderLibraryPage()}
        ${renderStorePage()}
        ${renderCodexPage()}
      </div>
      ${BottomNav()}
    </div>
  `;

  bindInteractions();

  if (preserveScroll) {
    requestAnimationFrame(() => {
      window.scrollTo({ top: scrollTop, behavior: "auto" });
      const nextScrollContainer = appRoot.querySelector(".app-content");
      if (nextScrollContainer instanceof HTMLElement) {
        nextScrollContainer.scrollTop = contentScrollTop;
      }
    });
  }

  if (activeId) {
    const nextActive = document.getElementById(activeId);
    if (nextActive instanceof HTMLElement) {
      requestAnimationFrame(() => {
        nextActive.focus({ preventScroll: true });
        if (typeof selectionStart === "number" && typeof selectionEnd === "number" && "setSelectionRange" in nextActive) {
          nextActive.setSelectionRange(selectionStart, selectionEnd);
        }
      });
    }
  }
}

function renderCodexUi() {
  if (masterAppState.currentTab !== "codex") {
    renderAppShell();
    return;
  }

  const appRoot = document.getElementById("app");
  const scrollContainer = appRoot?.querySelector(".app-content");
  const contentScrollTop = scrollContainer instanceof HTMLElement ? scrollContainer.scrollTop : 0;
  const previousChatScroll = appRoot?.querySelector(".codex-chat-scroll");
  const scrollState = captureCodexScrollState(previousChatScroll);
  const activeElement = document.activeElement;
  const activeId = activeElement instanceof HTMLElement ? activeElement.id : "";
  const selectionStart = activeElement && "selectionStart" in activeElement ? activeElement.selectionStart : null;
  const selectionEnd = activeElement && "selectionEnd" in activeElement ? activeElement.selectionEnd : null;
  const existingPage = document.getElementById("page-codex");
  if (!(existingPage instanceof HTMLElement)) {
    renderAppShell();
    return;
  }

  const wrapper = document.createElement("div");
  wrapper.innerHTML = renderCodexPage().trim();
  const nextPage = wrapper.firstElementChild;
  if (!(nextPage instanceof HTMLElement)) {
    renderAppShell();
    return;
  }

  existingPage.replaceWith(nextPage);
  bindCodexInteractions();

  requestAnimationFrame(() => {
    const nextScrollContainer = document.getElementById("app")?.querySelector(".app-content");
    if (nextScrollContainer instanceof HTMLElement) {
      nextScrollContainer.scrollTop = contentScrollTop;
    }

    restoreCodexScrollState(scrollState);

    if (activeId) {
      const nextActive = document.getElementById(activeId);
      if (nextActive instanceof HTMLElement) {
        nextActive.focus({ preventScroll: true });
        if (typeof selectionStart === "number" && typeof selectionEnd === "number" && "setSelectionRange" in nextActive) {
          nextActive.setSelectionRange(selectionStart, selectionEnd);
        }
      }
    }
  });
}

function bindInteractions() {
  document.querySelectorAll("[data-tab]").forEach(button => {
    button.addEventListener("click", () => {
      setTab(button.dataset.tab);
    });
  });

  document.querySelectorAll("[data-library-filter]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.libraryFilter = button.dataset.libraryFilter;
      renderAppShell();
    });
  });

  document.querySelectorAll("[data-store-filter]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.storeFilter = button.dataset.storeFilter;
      renderAppShell();
    });
  });

  const searchInput = document.getElementById("library-search");
  if (searchInput) {
    searchInput.value = masterAppState.search;
    searchInput.addEventListener("input", event => {
      masterAppState.search = event.target.value;
      renderAppShell();
    });
  }

  document.querySelectorAll("[data-quick-action]").forEach((button, index) => {
    button.addEventListener("click", () => {
      quickActions[index]?.action();
    });
  });

  document.querySelectorAll("[data-log-name]").forEach(button => {
    button.addEventListener("click", () => {
      loadLog(button.dataset.logName);
    });
  });

  document.querySelectorAll("[data-open-link]").forEach(button => {
    button.addEventListener("click", () => {
      const url = button.dataset.openLink;
      if (url) {
        window.open(url, "_blank", "noopener,noreferrer");
      }
    });
  });

  document.querySelectorAll("[data-toggle-settings]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.settingsOpen = !masterAppState.settingsOpen;
      renderAppShell();
    });
  });

  document.querySelectorAll("[data-close-settings]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.settingsOpen = false;
      renderAppShell();
    });
  });

  bindCodexInteractions();
}

function bindCodexInteractions() {
  const codexPrompt = document.getElementById("codex-prompt");
  if (codexPrompt) {
    codexPrompt.value = masterAppState.codexDraft;
    codexPrompt.addEventListener("input", event => {
      masterAppState.codexDraft = event.target.value;
    });
  }

  const codexWorkspace = document.getElementById("codex-workspace");
  if (codexWorkspace) {
    codexWorkspace.value = masterAppState.codexSelectedWorkspace;
    codexWorkspace.addEventListener("change", event => {
      masterAppState.codexSelectedWorkspace = event.target.value;
      renderCodexUi();
    });
  }

  const codexMode = document.getElementById("codex-mode");
  if (codexMode) {
    codexMode.value = masterAppState.codexSelectedMode || "auto";
    codexMode.addEventListener("change", event => {
      masterAppState.codexSelectedMode = event.target.value || "auto";
      renderCodexUi();
    });
  }

  const codexModel = document.getElementById("codex-model");
  if (codexModel) {
    codexModel.value = encodeCodexModelValue(
      masterAppState.codexSelectedProvider || masterAppState.latestCodex?.currentProvider || "codex",
      masterAppState.codexSelectedModel || masterAppState.latestCodex?.currentModel || ""
    );
    codexModel.addEventListener("change", async event => {
      const parsed = parseCodexModelValue(event.target.value);
      masterAppState.codexSelectedProvider = parsed.provider;
      masterAppState.codexSelectedModel = parsed.slug;
      renderCodexUi();
      await updateCodexModel(event.target.value);
    });
  }

  const codexForm = document.getElementById("codex-form");
  if (codexForm) {
    codexForm.addEventListener("submit", async event => {
      event.preventDefault();
      await submitCodexPrompt();
    });
  }

  document.querySelectorAll("[data-codex-chat-id]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.codexSelectedChatId = button.dataset.codexChatId || "";
      masterAppState.codexRecentsOpen = false;
      renderCodexUi();
    });
  });

  document.querySelectorAll("[data-codex-toggle-recents]").forEach(element => {
    element.addEventListener("toggle", event => {
      masterAppState.codexRecentsOpen = !!event.target.open;
    });
  });

  document.querySelectorAll("[data-codex-history-scope]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.codexHistoryScope = button.dataset.codexHistoryScope || "all";
      renderCodexUi();
    });
  });

  document.querySelectorAll("[data-codex-toggle-details]").forEach(button => {
    button.addEventListener("click", () => {
      masterAppState.codexDetailsOpen = !masterAppState.codexDetailsOpen;
      renderCodexUi();
    });
  });

  document.querySelectorAll("[data-codex-approval]").forEach(button => {
    button.addEventListener("click", async () => {
      await resolveCodexApproval(button.dataset.codexApproval || "");
    });
  });

  document.querySelectorAll("[data-codex-stop]").forEach(button => {
    button.addEventListener("click", async () => {
      await stopCodexSession();
    });
  });

  document.querySelectorAll("[data-codex-new-session]").forEach(button => {
    button.addEventListener("click", async () => {
      await startNewCodexSession();
    });
  });

  bindCodexScrollTracking();
}

function setTab(tab) {
  masterAppState.currentTab = tab;
  renderAppShell({ preserveScroll: false });
  window.scrollTo({ top: 0, behavior: "smooth" });
}

function setLogFocus() {
  masterAppState.currentTab = "dashboard";
  renderAppShell({ preserveScroll: false });
  const diagnostics = document.getElementById("diagnostics");
  diagnostics?.scrollIntoView({ behavior: "smooth", block: "start" });
}

function Header() {
  if (masterAppState.currentTab === "store") {
    return "";
  }

  return `
    <header class="top-header top-header--minimal">
      <h1 class="brand-wordmark">MasterApp</h1>
      <div class="header-actions">
        <button class="icon-button" type="button" aria-label="Settings" data-toggle-settings>${iconWrap(icon("settings"))}</button>
      </div>
    </header>
  `;
}

function SettingsSheet() {
  const status = masterAppState.latestStatus;
  const codexUsage = masterAppState.latestCodex?.usage || null;

  return `
    <section class="settings-sheet ${masterAppState.settingsOpen ? "is-open" : ""}" aria-hidden="${masterAppState.settingsOpen ? "false" : "true"}">
      <div class="settings-backdrop" data-close-settings></div>
      <div class="settings-panel">
        <div class="settings-head">
          <div>
            <p class="section-kicker">Settings</p>
            <h2 class="card-title">System details</h2>
          </div>
            <button class="icon-button" type="button" aria-label="Close settings" data-close-settings>${iconWrap(icon("close"))}</button>
          </div>

        <details class="settings-group">
          <summary>Runtime details</summary>
          <div class="settings-group-body">
            <div class="details-grid">
              ${renderDetailRows(getStatusDetails(status))}
            </div>
          </div>
        </details>

        <details class="settings-group">
          <summary>Configuration checks</summary>
          <div class="settings-group-body">
            <div class="details-grid">
              ${renderIssueRows(status?.configIssues || [])}
            </div>
          </div>
        </details>

        <details class="settings-group">
          <summary>Codex usage</summary>
          <div class="settings-group-body">
            <div class="details-grid codex-usage-grid">
              ${renderDetailRows(getCodexUsageRows(codexUsage))}
            </div>
          </div>
        </details>

        <details class="settings-group">
          <summary>Logs</summary>
          <div class="settings-group-body">
            <div class="log-actions">
              ${["app", "tunnel", "packages", "ui", "codex"].map(name => `
                <button class="log-button ${masterAppState.latestLogName === name ? "is-active" : ""}" type="button" data-log-name="${name}">${escapeHtml(name)}</button>
              `).join("")}
            </div>
            <pre class="log-viewer">${escapeHtml(masterAppState.latestLogText)}</pre>
          </div>
        </details>

        <details class="settings-group">
          <summary>Package installs</summary>
          <div class="settings-group-body">
            <div class="details-grid">
              ${renderPackageResultRows(status?.lastPackageResult)}
            </div>
          </div>
        </details>
      </div>
    </section>
  `;
}

function BottomNav() {
  const items = [
    { id: "dashboard", label: "Dashboard", icon: "home" },
    { id: "library", label: "Library", icon: "library" },
    { id: "store", label: "Store", icon: "store" },
    { id: "codex", label: "Codex", icon: "chat" }
  ];

  return `
    <nav class="bottom-nav" aria-label="Bottom navigation">
      ${items.map(item => `
        ${item.href ? `<a class="tab-button" href="${item.href}">` : `<button class="tab-button ${masterAppState.currentTab === item.id ? "is-active" : ""}" type="button" data-tab="${item.id}">`}
          <span class="tab-button-icon" aria-hidden="true">${icon(item.icon)}</span>
          <span class="tab-label">${escapeHtml(item.label)}</span>
        ${item.href ? `</a>` : `</button>`}
      `).join("")}
    </nav>
  `;
}
function renderDashboardPage() {
  const status = masterAppState.latestStatus;
  const apps = masterAppState.latestApps;
  const recent = getRecentActivities();

  return `
    <section class="page ${masterAppState.currentTab === "dashboard" ? "is-active" : ""}" id="page-dashboard">
      <div class="page-head">
        <div>
          <h2 class="page-title">Dashboard</h2>
          <p class="page-subtitle">Runtime, tunnel, and installs at a glance.</p>
        </div>
      </div>

      ${HeroStatusCard(status)}

      <div class="stats-grid">
        ${StatTile("Running apps", String(getRunningApps(apps).length), getRunningApps(apps).length ? `${getRunningApps(apps)[0].displayName} live` : "No active apps")}
        ${StatTile("Stopped apps", String(getStoppedApps(apps).length), getStoppedApps(apps).length ? `${getStoppedApps(apps)[0].displayName} ready` : "Everything is running")}
        ${StatTile("Tunnel state", getTunnelStatValue(status), getTunnelStatMeta(status))}
        ${StatTile("Health", getHealthValue(status), getHealthMeta(status))}
      </div>

      <div class="section-head">
        <h3 class="section-title">Quick actions</h3>
      </div>
      <div class="quick-actions-grid">
        ${quickActions.map((item, index) => QuickActionButton(item, index)).join("")}
      </div>

      <details class="activity-panel">
        <summary>
          <span class="section-title">Recent activity</span>
          <span class="section-link">Show</span>
        </summary>
        <div class="activity-compact-list">
          ${recent.length ? recent.map(ActivityItem).join("") : `<div class="empty-state">Recent installs and launches will appear here.</div>`}
        </div>
      </details>
    </section>
  `;
}

function renderLibraryPage() {
  const apps = getFilteredLibraryApps();

  return `
    <section class="page ${masterAppState.currentTab === "library" ? "is-active" : ""}" id="page-library">
      <div class="page-head">
        <div>
          <h2 class="page-title">Library</h2>
          <p class="page-subtitle">Manage installed apps, exports, and cleanup from one place.</p>
        </div>
      </div>

      <div class="search-row">
        <label class="search-field" for="library-search">
          ${icon("search")}
          <input id="library-search" type="search" placeholder="Search installed apps">
        </label>
      </div>

      <div class="filter-row">
        ${FilterChip("All", "all", getVisibleLibraryApps().length, masterAppState.libraryFilter === "all")}
        ${FilterChip("Running", "running", getRunningApps(getVisibleLibraryApps()).length, masterAppState.libraryFilter === "running")}
        ${FilterChip("Stopped", "stopped", getStoppedApps(getVisibleLibraryApps()).length, masterAppState.libraryFilter === "stopped")}
      </div>

      <div class="section-head">
        <h3 class="section-title">Installed apps</h3>
        <button class="icon-button" type="button" aria-label="Rescan packages" onclick="postAction('/api/packages/rescan')">${iconWrap(icon("refresh"))}</button>
      </div>
      <div class="app-grid">
        ${apps.length ? apps.map(AppCard).join("") : `<div class="empty-state">No apps match this filter right now.</div>`}
      </div>
    </section>
  `;
}

function renderStorePage() {
  const apps = getFilteredStoreApps();

  return `
    <section class="page ${masterAppState.currentTab === "store" ? "is-active" : ""}" id="page-store">
      <div class="store-topbar">
        <div class="store-chip-row store-chip-row--top">
          ${StoreFilterChip("All", "all", getStoreVisibleApps().length, masterAppState.storeFilter === "all")}
          ${StoreFilterChip("Recent", "recent", getRecentStoreApps().length, masterAppState.storeFilter === "recent")}
        </div>
        <button class="icon-button store-settings-button" type="button" aria-label="Settings" data-toggle-settings>${iconWrap(icon("settings"))}</button>
      </div>

      ${StoreInstallStatusLine(masterAppState.latestStatus?.lastPackageResult)}

      <div class="store-grid">
        ${apps.length ? apps.map(renderStoreCard).join("") : `<div class="empty-state">No store apps match this filter yet.</div>`}
      </div>
    </section>
  `;
}

function renderCodexPage() {
  const codex = masterAppState.latestCodex;
  const allRecent = (codex?.recentChats || []).filter(item => item && item.updatedAtUtc);
  const active = codex?.activeRun || null;
  const pendingApproval = codex?.pendingApproval || null;
  const currentSessionId = codex?.currentSessionId || "";
  const workspaces = codex?.configuredWorkspaces || [];
  const models = codex?.availableModels || [];
  const modelChoices = models.length ? models : (codex?.currentModel ? [{ slug: codex.currentModel, displayName: codex.currentModel }] : []);
  const visibleModelChoices = modelChoices.length ? modelChoices : [{ slug: "", displayName: "Default" }];
  const visibleWorkspaces = workspaces.length ? workspaces : [{ path: "", label: "General" }];
  const selectedWorkspace = masterAppState.codexSelectedWorkspace || workspaces[0]?.path || "";
  const selectedProvider = masterAppState.codexSelectedProvider || codex?.currentProvider || "codex";
  const selectedModel = masterAppState.codexSelectedModel || codex?.currentModel || modelChoices[0]?.slug || "";
  const selectedModelKey = encodeCodexModelValue(selectedProvider, selectedModel);
  const scopedRecent = getScopedCodexRecentChats(allRecent, selectedWorkspace);
  const recent = masterAppState.codexHistoryScope === "all" ? allRecent : scopedRecent;
  const selectedRecent = recent.find(item => item.id === masterAppState.codexSelectedChatId)
    || allRecent.find(item => item.id === masterAppState.codexSelectedChatId)
    || (currentSessionId ? allRecent.find(item => item.id === currentSessionId) : null)
    || null;
  const isViewingCurrentSession = !!selectedRecent && selectedRecent.id === currentSessionId;
  const isViewingEmptyCurrentSession = !selectedRecent && !currentSessionId && !masterAppState.codexSelectedChatId;
  const isBusy = !!active && !isCodexRunTerminal(active.status);
  const status = getCodexPrimaryStatus(codex, active, pendingApproval, codex?.cliResolutionStatus, codex?.cliProbe);

  return `
    <section class="page ${masterAppState.currentTab === "codex" ? "is-active" : ""}" id="page-codex">
      <section class="codex-phone-shell ${masterAppState.codexDetailsOpen ? "is-details-open" : ""}">
        <article class="codex-chat-surface">
          <div class="codex-chat-topbar">
            <button class="icon-button codex-settings-button" type="button" aria-label="Open Codex settings" data-toggle-settings>${iconWrap(icon("settings"))}</button>
            <div class="codex-top-status">
              <span class="status-dot ${status.tone === "success" ? "is-success" : status.tone === "danger" ? "is-danger" : status.tone === "warning" ? "is-warning" : ""}"></span>
              <span>${escapeHtml(status.label)}</span>
            </div>
            <div class="codex-top-actions">
              <button class="secondary-button codex-details-button" type="button" data-codex-toggle-details>${escapeHtml(masterAppState.codexDetailsOpen ? "Hide activity" : "Activity")}</button>
              ${renderCodexRecentsPanel(allRecent, recent, selectedRecent, selectedWorkspace)}
            </div>
          </div>
          <div class="codex-chat-scroll">
            <div class="codex-transcript">
              ${renderCodexConversation(selectedRecent, active, pendingApproval, isViewingCurrentSession || isViewingEmptyCurrentSession)}
            </div>
          </div>
        </article>

        ${masterAppState.codexDetailsOpen ? renderCodexActivityPanel(codex, active, pendingApproval, selectedRecent, selectedWorkspace) : ""}

        <form id="codex-form" class="codex-composer-card codex-form codex-form--chat-first">
          <div class="codex-composer-top">
            <label class="codex-field codex-field--composer" for="codex-prompt">
              <textarea id="codex-prompt" class="codex-textarea codex-textarea--chat" rows="3" placeholder="Ask Codex something..."></textarea>
            </label>
            <div class="codex-composer-actions">
              <button class="secondary-button codex-new-chat-button" type="button" data-codex-new-session ${isBusy ? "disabled" : ""}>New chat</button>
              <button class="primary-button codex-send-button" type="submit" ${isBusy ? "disabled" : ""}>Send</button>
            </div>
          </div>

          <div class="codex-control-row">
            <label class="codex-compact-field" for="codex-model">
              <span class="codex-compact-label">Model</span>
              <select id="codex-model" class="codex-select codex-select--compact">
                ${visibleModelChoices.map(item => `
                  <option value="${escapeAttribute(encodeCodexModelValue(item.provider || "codex", item.slug))}" ${selectedModelKey === encodeCodexModelValue(item.provider || "codex", item.slug) ? "selected" : ""}>${escapeHtml(item.displayName || item.slug)}</option>
                `).join("")}
              </select>
            </label>
            <label class="codex-compact-field" for="codex-workspace">
              <span class="codex-compact-label">Context</span>
              <select id="codex-workspace" class="codex-select codex-select--compact">
                ${visibleWorkspaces.map(item => `
                  <option value="${escapeAttribute(item.path)}" ${selectedWorkspace === item.path ? "selected" : ""}>${escapeHtml(formatCodexContextLabel(item))}</option>
                `).join("")}
              </select>
            </label>
            <button class="secondary-button codex-compact-stop" type="button" data-codex-stop ${isBusy ? "" : "disabled"}>
              <span class="codex-compact-label">Stop</span>
              <span class="codex-compact-value">${isBusy ? "Running" : "Idle"}</span>
            </button>
          </div>

          ${masterAppState.codexLastError ? `<div class="codex-error codex-error--inline">${escapeHtml(masterAppState.codexLastError)}</div>` : ""}
        </form>
      </section>
    </section>
  `;
}

function getCodexPrimaryStatus(codex, active, pendingApproval, resolutionState, probe) {
  if (masterAppState.codexLastError) {
    return { tone: "danger", label: "Not working", message: masterAppState.codexLastError };
  }

  if (resolutionState !== "ready") {
    return { tone: resolutionState === "missing" ? "danger" : "warning", label: "Not ready", message: codex?.cliResolutionError || "Checking the local Codex CLI." };
  }

  if (!probe?.isReady) {
    return { tone: probe?.lastError ? "danger" : "warning", label: "Not ready", message: probe?.lastError || "Probing Codex JSON mode." };
  }

  if (pendingApproval) {
    return { tone: "warning", label: "Needs approval", message: pendingApproval.summary || "Codex is waiting for your approval." };
  }

  if (active && !isCodexRunTerminal(active.status)) {
    return { tone: "warning", label: "Working", message: "Codex is working on the current request." };
  }

  return {
    tone: masterAppState.codexConnectionState === "open" ? "success" : "warning",
    label: masterAppState.codexConnectionState === "open" ? "Ready" : "Connecting",
    message: masterAppState.codexConnectionState === "open" ? "Local Codex session is ready." : "Trying to reconnect to Codex events."
  };
}

function renderCodexRecentsPanel(allRecent, recent, selectedRecent, selectedWorkspace) {
  const scopedCount = getScopedCodexRecentChats(allRecent, selectedWorkspace).length;
  const allCount = allRecent.length;
  return `
    <details class="codex-recents-panel" data-codex-toggle-recents ${masterAppState.codexRecentsOpen ? "open" : ""}>
      <summary class="codex-recents-summary">Chat history</summary>
      <div class="codex-recents-body">
        <div class="codex-history-scope-row">
          <button class="codex-scope-button ${masterAppState.codexHistoryScope !== "all" ? "is-active" : ""}" type="button" data-codex-history-scope="context">This context ${scopedCount ? `(${scopedCount})` : ""}</button>
          <button class="codex-scope-button ${masterAppState.codexHistoryScope === "all" ? "is-active" : ""}" type="button" data-codex-history-scope="all">All ${allCount ? `(${allCount})` : ""}</button>
        </div>
        <div class="codex-recents-note">${recent.length ? `${recent.length} visible chats` : "No chats for this context yet"}</div>
        ${recent.length ? recent.map(item => `
          <button class="codex-recent-item ${selectedRecent?.id === item.id ? "is-active" : ""}" type="button" data-codex-chat-id="${escapeAttribute(item.id)}">
            <span class="codex-recent-title">${escapeHtml(item.title || "Untitled chat")}</span>
            <span class="codex-recent-meta">${escapeHtml(formatCodexRecentMeta(item))}</span>
          </button>
        `).join("") : `<div class="empty-state codex-empty-state">Recent chats will show up here.</div>`}
      </div>
    </details>
  `;
}

function HeroStatusCard(status) {
  const tunnel = status?.tunnel;
  const packageResult = status?.lastPackageResult;
  const isRunning = !!tunnel?.isRunning;
  const isStopped = tunnel?.status === "Stopped";
  const chip = isRunning
    ? StatusChip("Active", "success")
    : StatusChip(isStopped ? "Stopped" : tunnel?.lastError ? "Offline" : "Reconnecting", tunnel?.lastError ? "danger" : "warning");

  return `
    <article class="hero-status-card hero-status-card--compact">
      <div class="hero-top hero-top--compact">
        <div class="hero-kicker hero-kicker--tight">${statusDot(isRunning ? "success" : tunnel?.lastError ? "danger" : "warning")} Tunnel</div>
        ${chip}
      </div>
      <div class="hero-actions hero-actions--compact">
        <button class="primary-button" type="button" onclick="postAction('${isRunning ? "/api/tunnel/restart" : "/api/tunnel/start"}')">${isRunning ? "Reconnect" : "Start tunnel"}</button>
        <button class="secondary-button" type="button" ${isRunning ? "" : "disabled"} onclick="postAction('/api/tunnel/stop')">Stop</button>
      </div>
      ${packageResult ? `
        <div class="hero-install-status">
          <div class="hero-top hero-top--compact">
            <div class="hero-kicker hero-kicker--tight">${statusDot(packageResult.success ? "success" : "danger")} Packages</div>
            ${StatusChip(packageResult.success ? "Installed" : "Failed", packageResult.success ? "success" : "danger")}
          </div>
          <div class="details-grid">
            ${renderPackageResultRows(packageResult)}
          </div>
        </div>
      ` : ""}
    </article>
  `;
}

function StatTile(label, value, meta) {
  return `
    <article class="stat-tile">
      <div class="stat-value">${escapeHtml(value)}</div>
      <div class="stat-label">${escapeHtml(label)}</div>
      <div class="stat-meta">${escapeHtml(meta)}</div>
    </article>
  `;
}

function QuickActionButton(item, index) {
  return `
    <button class="quick-action-button" type="button" data-quick-action="${index}">
      ${iconWrap(icon(item.icon))}
      <span class="quick-action-label">${escapeHtml(item.label)}</span>
    </button>
  `;
}
function AppCard(app) {
  const running = !!app.runState?.isRunning;
  const canStartStop = app.appType !== "static";
  const installMeta = `Installed ${formatShortDate(app.installedAtUtc)}`;
  const actions = [];

  if (canStartStop) {
    actions.push(`<button class="compact-action-button" type="button" ${running ? "disabled" : ""} onclick="postAction('/api/apps/${encodeURIComponent(app.id)}/start')">Start</button>`);
    actions.push(`<button class="compact-action-button" type="button" ${running ? "" : "disabled"} onclick="postAction('/api/apps/${encodeURIComponent(app.id)}/stop')">Stop</button>`);
  }

  actions.push(`<button class="compact-action-button" type="button" ${app.canPublish ? "" : "disabled"} onclick="exportApp('${app.id}')">${buttonContent("Export", "export")}</button>`);

  return `
    <article class="app-card">
      <div class="app-card-body">
        <button class="app-delete-button" type="button" aria-label="Delete ${escapeAttribute(app.displayName)}" title="Delete ${escapeAttribute(app.displayName)}" onclick="deleteApp('${app.id}')">${icon("trash")}</button>
        <div class="app-card-head app-card-head--library">
          ${appAvatar(app)}
          <div class="app-card-copy">
            <div class="app-title-row">
              <h3 class="app-name">${escapeHtml(app.displayName)}</h3>
              <span class="app-inline-meta">${escapeHtml(installMeta)}</span>
            </div>
            <div class="app-chips">
              ${StatusChip(getInstallVersionLabel(app), "neutral")}
              ${StatusChip(running ? "Running" : "Stopped", running ? "success" : "warning")}
            </div>
          </div>
        </div>

        <div class="card-action-row">
          ${actions.join("")}
        </div>
      </div>
    </article>
  `;
}

function StatusChip(label, tone) {
  const toneClass = tone === "success" ? "is-success" : tone === "warning" ? "is-warning" : tone === "danger" ? "is-danger" : "";
  return `<span class="status-chip ${toneClass}"><span class="status-dot"></span>${escapeHtml(label)}</span>`;
}

function FilterChip(label, value, count, active) {
  return `
    <button class="filter-chip ${active ? "is-active" : ""}" type="button" data-library-filter="${value}">
      <span>${escapeHtml(label)}</span>
      <span class="count">${escapeHtml(String(count))}</span>
    </button>
  `;
}

function StoreFilterChip(label, value, count, active) {
  return `
    <button class="store-chip ${active ? "is-active" : ""}" type="button" data-store-filter="${value}">
      <span>${escapeHtml(label)}</span>
      <span class="count">${escapeHtml(String(count))}</span>
    </button>
  `;
}

function StoreInstallStatusLine(packageResult) {
  if (!packageResult) {
    return `
      <div class="store-install-line">
        ${statusDot("warning")}
        <span>No install activity yet.</span>
      </div>
    `;
  }

  const tone = packageResult.success ? "success" : "danger";
  const state = packageResult.success ? "OK install" : "Install failed";
  const source = packageResult.sourceFileName || packageResult.appId || "package";
  const time = packageResult.timestampUtc ? formatDateTime(packageResult.timestampUtc) : "recently";

  return `
    <div class="store-install-line store-install-line--${tone}">
      ${statusDot(tone)}
      <span>${escapeHtml(state)} - ${escapeHtml(source)} - ${escapeHtml(time)}</span>
    </div>
  `;
}

function FeaturedStoreCard(app) {
  return `
    <article class="featured-store-card">
      <div class="featured-top">
        <span class="featured-label">Featured</span>
        ${app.runState?.isRunning ? StatusChip("Open", "success") : ""}
      </div>
      <div style="margin-top: 16px; display: flex; gap: 14px; align-items: center;">
        ${appAvatar(app)}
        <div>
          <h3 class="store-title">${escapeHtml(app.displayName)}</h3>
          <p class="store-subtitle">${escapeHtml(getStoreSubtitle(app))}</p>
        </div>
      </div>
      <div class="hero-actions" style="padding-top: 28px; margin-top: 18px;">
        <button class="primary-button" type="button" data-open-link="${escapeAttribute(app.launchUrl)}">${escapeHtml(app.runState?.isRunning ? "Open" : "Install / Open")}</button>
      </div>
    </article>
  `;
}

function ActivityItem(item) {
  return `
    <article class="activity-item activity-item--compact">
      <div class="activity-line">
        <div class="activity-line-left">
          <div class="activity-avatar activity-avatar--compact">${escapeHtml(item.badge)}</div>
          <span class="activity-inline-title">${escapeHtml(item.title)}</span>
          <span class="activity-inline-copy">${escapeHtml(item.copy)}</span>
        </div>
        <div class="activity-time">${escapeHtml(item.time)}</div>
      </div>
    </article>
  `;
}

function renderStoreCard(app) {
  const lastUsage = getAppLastUsageUtc(app);
  return `
    <a class="store-card store-card--centered" href="${escapeAttribute(app.launchUrl)}" target="_blank" rel="noreferrer">
      <div class="store-card-main">
        ${appAvatar(app, "store-icon")}
        <div class="store-title-line">
          <h3 class="store-name">${escapeHtml(app.displayName)}</h3>
          <div class="store-version">${escapeHtml(getInstallVersionLabel(app))}</div>
        </div>
        <div class="store-card-meta">${escapeHtml(lastUsage ? `Last used ${timeAgo(lastUsage)}` : `Installed ${formatShortDate(app.installedAtUtc)}`)}</div>
      </div>
    </a>
  `;
}

function appAvatar(app, className = "app-avatar") {
  if (app.iconUrl) {
    return `<div class="${className}"><img src="${escapeAttribute(app.iconUrl)}" alt="${escapeAttribute(app.displayName)} icon"></div>`;
  }

  const fallback = getFallbackLogo(app);
  return `
    <div class="${className} app-avatar-fallback" style="--avatar-start:${fallback.start};--avatar-end:${fallback.end};--avatar-color:${fallback.color};">
      ${icon(fallback.glyph)}
    </div>
  `;
}

function getFilteredLibraryApps() {
  const search = masterAppState.search.trim().toLowerCase();
  return getVisibleLibraryApps().filter(app => {
    const matchesFilter = masterAppState.libraryFilter === "all"
      || (masterAppState.libraryFilter === "running" && app.runState?.isRunning)
      || (masterAppState.libraryFilter === "stopped" && !app.runState?.isRunning);
    const matchesSearch = !search || [app.displayName, app.name, app.id].some(value => String(value || "").toLowerCase().includes(search));
    return matchesFilter && matchesSearch;
  });
}

function getVisibleLibraryApps() {
  return masterAppState.latestApps.filter(app => app.showInLibrary !== false);
}

function getStoreVisibleApps() {
  return sortStoreApps(masterAppState.latestApps.filter(app => app.storeVisible));
}

function getFilteredStoreApps() {
  const apps = getStoreVisibleApps();
  if (masterAppState.storeFilter === "recent") {
    return getRecentStoreApps();
  }
  return apps;
}

function getRecentStoreApps() {
  return getStoreVisibleApps().slice(0, 4);
}

function getFeaturedApp() {
  return [...getStoreVisibleApps()]
    .sort((left, right) => Number(!!right.runState?.isRunning) - Number(!!left.runState?.isRunning)
      || new Date(right.installedAtUtc || 0) - new Date(left.installedAtUtc || 0))[0] || null;
}

function sortStoreApps(apps) {
  return [...apps].sort((left, right) =>
    getTimeValue(right.installedAtUtc) - getTimeValue(left.installedAtUtc)
    || getTimeValue(getAppLastUsageUtc(right)) - getTimeValue(getAppLastUsageUtc(left))
    || String(left.displayName || left.id || "").localeCompare(String(right.displayName || right.id || ""))
  );
}

function getAppLastUsageUtc(app) {
  const candidates = [
    app.lastUsedAtUtc,
    app.lastUsageAtUtc,
    app.runState?.startedAtUtc,
    app.runState?.stoppedAtUtc
  ];

  return candidates
    .filter(Boolean)
    .sort((left, right) => getTimeValue(right) - getTimeValue(left))[0] || "";
}

function getTimeValue(value) {
  const timestamp = new Date(value || 0).getTime();
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function getRunningApps(apps) {
  return apps.filter(app => app.runState?.isRunning);
}

function getStoppedApps(apps) {
  return apps.filter(app => !app.runState?.isRunning);
}
function getTunnelStatValue(status) {
  if (!status?.tunnel) {
    return "Checking";
  }
  if (status.tunnel.isRunning) {
    return "Active";
  }
  if (status.tunnel.status === "Stopped") {
    return "Stopped";
  }
  return status.tunnel.lastError ? "Offline" : "Waiting";
}

function getTunnelStatMeta(status) {
  if (!status?.tunnel) {
    return "Awaiting state";
  }
  return status.tunnel.isRunning
    ? (status.publicHostname || "Public hostname ready")
    : (status.tunnel.lastMessage || "Restart to reconnect");
}

function getHealthValue(status) {
  const issues = status?.configIssues || [];
  if (issues.length) {
    return "Needs setup";
  }
  return status?.packageWatcher?.isRunning ? "Healthy" : "Watching off";
}

function getHealthMeta(status) {
  const issues = status?.configIssues || [];
  if (issues.length) {
    return `${issues.length} checks to fix`;
  }
  const lastScan = status?.lastPackageScanAtUtc ? timeAgo(status.lastPackageScanAtUtc) : "No scan yet";
  return `Last scan ${lastScan}`;
}

function getStatusDetails(status) {
  if (!status) {
    return [{ label: "Status", value: "Loading..." }];
  }

  return [
    { label: "Local URL", value: status.localUrl || "-" },
    { label: "Public URL", value: status.publicUrl || "-" },
    { label: "Hostname", value: status.publicHostname || "-" },
    { label: "Settings", value: status.settingsFile || "-" },
    { label: "Logs", value: status.logsDirectory || "-" },
    { label: "Published", value: status.publishedDirectory || "-" }
  ];
}

function renderDetailRows(rows) {
  return rows.map(row => `
    <div class="detail-row">
      <span class="field-label">${escapeHtml(row.label)}</span>
      <span class="value">${escapeHtml(row.value)}</span>
    </div>
  `).join("");
}

function renderPackageResultRows(result) {
  if (!result) {
    return `<div class="detail-row"><span class="field-label">Result</span><span class="value">No package activity yet.</span></div>`;
  }

  const target = [result.appId, result.version].filter(Boolean).join(" ");
  const rows = [
    { label: "Source zip", value: result.sourceFileName || "-" },
    { label: "Outcome", value: result.success ? "Installed" : "Failed" },
    { label: "Target", value: target || "No app activated" },
    { label: "Time", value: result.timestampUtc ? timeAgo(result.timestampUtc) : "-" },
    { label: "Message", value: result.message || "-" }
  ];

  return renderDetailRows(rows);
}

function renderIssueRows(issues) {
  if (!issues.length) {
    return `<div class="detail-row"><span class="field-label">Result</span><span class="value">All required values look configured.</span></div>`;
  }

  return issues.map((issue, index) => `
    <div class="detail-row">
      <span class="field-label">Check ${index + 1}</span>
      <span class="value">${escapeHtml(issue)}</span>
    </div>
  `).join("");
}

function renderCodexConversation(selectedRecent, active, pendingApproval, isViewingCurrentSession) {
  const messages = selectedRecent?.messages || [];
  const parts = [];

  if (!messages.length) {
    if (isViewingCurrentSession && active?.prompt) {
      parts.push(renderCodexMessage({
        role: "user",
        text: active.prompt
      }));
    } else {
      parts.push(renderCodexWelcomeCard());
    }
  } else {
    if (selectedRecent) {
      parts.push(renderCodexSessionMarker(selectedRecent, isViewingCurrentSession));
    }

    messages.forEach(message => {
      parts.push(renderCodexMessage(message));
    });
  }

  if (isViewingCurrentSession) {
    if (pendingApproval) {
      parts.push(renderCodexApprovalCard(pendingApproval));
    }
    parts.push(renderCodexProcessing(active, pendingApproval));
    const activeFinalAlreadyVisible = messages.some(message =>
      message?.runId && active?.id &&
      message.runId === active.id &&
      (message.role || "").toLowerCase() === "assistant");
    if (!activeFinalAlreadyVisible) {
      parts.push(renderCodexFinalResponse(active));
    }
  }

  return parts.join("");
}

function renderCodexWelcomeCard() {
  return `
    <article class="codex-message-card codex-message-card--preview codex-message-card--hint">
      <div class="codex-message-meta">
        <span>Start here</span>
      </div>
      <div class="codex-message-assistant">Type a request below and your reply will appear here. Chat history stays available from the button above.</div>
    </article>
  `;
}

function renderCodexSessionMarker(session, isViewingCurrentSession) {
  return `
    <article class="codex-message-card codex-message-card--preview codex-history-preview">
      <div class="codex-history-preview-head">
        <div>
          <div class="codex-message-meta">
            <span>${isViewingCurrentSession ? "Current session" : "From chat history"}</span>
          </div>
          <h3 class="section-title">${escapeHtml(session.title || "Untitled chat")}</h3>
        </div>
        <div class="codex-history-preview-meta">${escapeHtml(timeAgo(session.updatedAtUtc))}</div>
      </div>
    </article>
  `;
}

function renderCodexMessage(message) {
  const role = (message?.role || "assistant").toLowerCase();
  const text = message?.text || "";
  const status = (message?.status || "completed").toLowerCase();
  const cardClass = role === "user"
    ? "codex-message-card codex-message-card--user"
    : status === "failed" || status === "stopped"
      ? "codex-message-card codex-message-card--assistant codex-message-card--failure"
      : "codex-message-card codex-message-card--assistant";

  return `
    <article class="${cardClass}">
      ${role === "assistant" && status !== "completed" ? `<div class="codex-message-meta"><span>${escapeHtml(status)}</span></div>` : ""}
      <div class="${role === "user" ? "codex-message-user" : "codex-message-assistant"}">${escapeHtml(text)}</div>
    </article>
  `;
}

function renderCodexProcessing(active, pendingApproval) {
  if (!active || pendingApproval || isCodexRunTerminal(active.status)) {
    return "";
  }

  const latest = getCodexLatestActivity(active);
  return `
    <article class="codex-message-card codex-message-card--assistant codex-message-card--processing">
      <div class="codex-message-meta">
        <span>Working on it</span>
      </div>
      <div class="codex-processing">
        <span class="codex-spinner" aria-hidden="true"></span>
        <span>${escapeHtml(latest || "Processing...")}</span>
      </div>
    </article>
  `;
}

function renderCodexActivityPanel(codex, active, pendingApproval, selectedRecent, selectedWorkspace) {
  const activity = getCodexActivityItems(active, pendingApproval);
  const changedFiles = active?.changedFiles || [];
  return `
    <aside class="codex-activity-panel" aria-label="Codex activity">
      <div class="codex-activity-head">
        <div>
          <div class="codex-message-meta"><span>On demand</span></div>
          <h3 class="section-title">Activity</h3>
        </div>
        <button class="icon-button" type="button" aria-label="Close activity" data-codex-toggle-details>${iconWrap(icon("close"))}</button>
      </div>
      <div class="codex-activity-section">
        <div class="codex-activity-label">Current context</div>
        <div class="codex-activity-value">${escapeHtml(trimPath(selectedWorkspace || selectedRecent?.cwd || "General"))}</div>
      </div>
      ${renderCodexSummaryChips(active)}
      <div class="codex-activity-section">
        <div class="codex-activity-label">Timeline</div>
        ${activity.length ? activity.map(renderCodexActivityItem).join("") : `<div class="empty-state codex-empty-state">Activity will appear while Codex works.</div>`}
      </div>
      <details class="codex-activity-section">
        <summary class="codex-activity-summary">Changed files</summary>
        ${renderCodexList(changedFiles, "No changed files reported yet.")}
      </details>
      <details class="codex-activity-section">
        <summary class="codex-activity-summary">Build and restart</summary>
        ${renderBuildAndRestart(active, codex?.lastRelaunch)}
      </details>
      <details class="codex-activity-section">
        <summary class="codex-activity-summary">Raw live log</summary>
        ${active?.logLines?.length ? `<pre class="codex-command-preview codex-log-preview">${escapeHtml(active.logLines.join("\n"))}</pre>` : `<div class="empty-state codex-empty-state">No live log lines yet.</div>`}
      </details>
    </aside>
  `;
}

function renderCodexActivityItem(item) {
  return `
    <div class="codex-activity-item">
      <span class="codex-activity-dot"></span>
      <div>
        <div class="codex-activity-title">${escapeHtml(item.title)}</div>
        ${item.detail ? `<div class="codex-activity-detail">${escapeHtml(item.detail)}</div>` : ""}
      </div>
    </div>
  `;
}

function renderCodexApprovalCard(approval) {
  if (!approval) {
    return "";
  }

  const canTrustWorkspace = !!approval.canTrustWorkspace && !!approval.trustWorkspacePath;
  const approvalButtons = canTrustWorkspace
    ? `
        <button class="primary-button" type="button" data-codex-approval="approve-once">Continue once</button>
        <button class="secondary-button" type="button" data-codex-approval="trust">Trust workspace</button>
        <button class="secondary-button" type="button" data-codex-approval="reject">Not now</button>
      `
    : `
        <button class="primary-button" type="button" data-codex-approval="approve">Continue</button>
        <button class="secondary-button" type="button" data-codex-approval="reject">Not now</button>
      `;

  return `
    <article class="codex-message-card codex-message-card--approval">
      <div class="codex-message-meta">
        <span>Needs your permission</span>
      </div>
      <div class="codex-approval-copy">${escapeHtml(approval.summary || "Codex wants to continue with the next step.")}</div>
      ${canTrustWorkspace ? `<div class="codex-approval-copy">Working directory is outside trusted workspaces: ${escapeHtml(approval.trustWorkspacePath)}</div>` : ""}
      ${approval.command ? `<pre class="codex-command-preview">${escapeHtml(approval.command || "")}</pre>` : ""}
      <div class="codex-form-actions">
        ${approvalButtons}
      </div>
    </article>
  `;
}

function renderCodexFinalResponse(active) {
  if (!active) {
    return "";
  }

  if (active.status === "stopped") {
    return `
      <article class="codex-message-card codex-message-card--assistant codex-message-card--failure">
        <div class="codex-message-meta">
          <span>Stopped</span>
        </div>
        <div class="codex-message-assistant">${escapeHtml(active.failureMessage || "The session was stopped before Codex returned a final answer.")}</div>
      </article>
    `;
  }

  if (active.status === "failed") {
    return `
      <article class="codex-message-card codex-message-card--assistant codex-message-card--failure">
        <div class="codex-message-meta">
          <span>Failed</span>
        </div>
        <div class="codex-message-assistant">${escapeHtml(active.failureMessage || "The run failed before Codex returned a final answer.")}</div>
      </article>
    `;
  }

  if (!active.responseText) {
    return "";
  }

  return `
    <article class="codex-message-card codex-message-card--assistant">
      <div class="codex-message-assistant">${escapeHtml(active.responseText || "")}</div>
    </article>
  `;
}

function formatCodexContextLabel(workspace) {
  const raw = (workspace?.label || trimPath(workspace?.path || "") || "General").trim();
  if (!raw) {
    return "General";
  }

  if (/masterapp/i.test(raw)) {
    return "MasterApp";
  }

  if (/apps?/i.test(raw)) {
    return "Installed apps";
  }

  return raw
    .replace(/[-_]+/g, " ")
    .replace(/\b\w/g, character => character.toUpperCase());
}

function renderCodexSummaryChips(active) {
  if (!active) {
    return "";
  }

  const chips = [];
  const taskModeChip = getCodexTaskModeChip(active);
  if (taskModeChip) {
    chips.push(StatusChip(taskModeChip.label, taskModeChip.tone));
  }
  if (active.changedFiles?.length) {
    chips.push(StatusChip(`${active.changedFiles.length} files changed`, "neutral"));
  }
  if (active.buildResult?.status && active.buildResult.status !== "not-requested") {
    chips.push(StatusChip(active.buildResult.success ? "Build passed" : active.buildResult.status === "running" ? "Build running" : "Build failed", active.buildResult.success ? "success" : active.buildResult.status === "running" ? "warning" : "danger"));
  }
  if (active.restartStatus?.status) {
    chips.push(StatusChip(active.restartStatus.status === "scheduled" || active.restartStatus.status === "launched" ? "Restart scheduled" : "Restart failed", active.restartStatus.status === "scheduled" || active.restartStatus.status === "launched" ? "success" : "danger"));
  }

  return chips.length ? `<div class="codex-chip-row codex-chip-row--summary">${chips.join("")}</div>` : "";
}

function getCodexTaskModeChip(active) {
  if (!active?.taskMode) {
    return null;
  }

  const label = formatCodexModeLabel(active.taskMode);
  const source = active.taskModeSource === "manual"
    ? "manual"
    : active.taskModeConfidence > 0
      ? `auto ${Math.round(active.taskModeConfidence * 100)}%`
      : "auto";

  return {
    label: `${label} mode (${source})`,
    tone: active.taskModeSource === "manual" ? "success" : "neutral"
  };
}

function formatCodexTaskMode(active) {
  return active?.taskMode ? formatCodexModeLabel(active.taskMode) : "-";
}

function formatCodexTaskModeSource(active) {
  if (!active?.taskModeSource) {
    return "-";
  }

  return active.taskModeSource === "manual" ? "Manual override" : "Auto";
}

function formatCodexTaskModeConfidence(active) {
  return typeof active?.taskModeConfidence === "number" && active.taskModeConfidence > 0
    ? `${Math.round(active.taskModeConfidence * 100)}%`
    : "-";
}

function formatCodexModeLabel(mode) {
  switch ((mode || "").toLowerCase()) {
    case "action":
      return "Action";
    case "investigate":
      return "Investigate";
    case "code":
      return "Code";
    case "ask":
      return "Ask";
    default:
      return "Auto";
  }
}

function renderCodexDetail(label, value) {
  return `
    <div class="detail-row">
      <span class="field-label">${escapeHtml(label)}</span>
      <span class="value">${escapeHtml(value)}</span>
    </div>
  `;
}

function renderCodexList(items, emptyMessage) {
  if (!items || !items.length) {
    return `<div class="empty-state">${escapeHtml(emptyMessage)}</div>`;
  }

  return `
    <div class="codex-bullet-list">
      ${items.map(item => `<div class="codex-bullet-item">${escapeHtml(item)}</div>`).join("")}
    </div>
  `;
}

function renderBuildAndRestart(active, lastRelaunch) {
  const build = active?.buildResult;
  const restart = active?.restartStatus || lastRelaunch;
  const rows = [
    { label: "Task mode", value: formatCodexTaskMode(active) },
    { label: "Mode source", value: formatCodexTaskModeSource(active) },
    { label: "Mode confidence", value: formatCodexTaskModeConfidence(active) },
    { label: "Build status", value: build?.status || "Not requested" },
    { label: "Build summary", value: build?.summary || "-" },
    { label: "Restart status", value: restart?.status || "Not requested" },
    { label: "Restart message", value: restart?.message || "-" },
    { label: "Backup", value: restart?.backupDirectory || "-" }
  ];

  return `<div class="details-grid">${renderDetailRows(rows)}</div>`;
}

function getScopedCodexRecentChats(chats, selectedWorkspace) {
  if (masterAppState.codexHistoryScope === "all") {
    return chats;
  }

  const selected = normalizePathForCompare(selectedWorkspace);
  if (!selected) {
    return chats.filter(item => !item.cwd);
  }

  return chats.filter(item => normalizePathForCompare(item.cwd) === selected);
}

function getSharedCodexSessionIdForSubmit() {
  const chats = masterAppState.latestCodex?.recentChats || [];
  const selectedId = masterAppState.codexSelectedChatId || masterAppState.latestCodex?.currentSessionId || "";
  if (!selectedId) {
    return "";
  }

  const selected = chats.find(item => item.id === selectedId);
  if (!selected) {
    return "";
  }

  return isSharedCodexChat(selected) ? selected.id : "";
}

function isSharedCodexChat(chat) {
  return !!chat && (
    (chat.source || "").toLowerCase() === "codex" ||
    !!chat.sessionPath
  );
}

function normalizePathForCompare(path) {
  return String(path || "").trim().replace(/\//g, "\\").replace(/\\+$/g, "").toLowerCase();
}

function formatCodexRecentMeta(item) {
  const parts = [];
  if (item?.source) {
    parts.push(item.source);
  }
  if (item?.cwd) {
    parts.push(trimPath(item.cwd));
  }
  parts.push(timeAgo(item?.updatedAtUtc));
  return parts.filter(Boolean).join(" / ");
}

function getCodexLatestActivity(active) {
  const items = getCodexActivityItems(active, null);
  return items[0]?.detail || items[0]?.title || "";
}

function getCodexActivityItems(active, pendingApproval) {
  const items = [];
  if (pendingApproval) {
    items.push({
      title: "Waiting for approval",
      detail: pendingApproval.summary || pendingApproval.command || "Codex needs your permission to continue."
    });
  }

  if (active?.approvalHistory?.length) {
    active.approvalHistory.slice(-6).reverse().forEach(record => {
      items.push({
        title: formatCodexApprovalTitle(record),
        detail: record.outputSummary || record.summary || record.command || ""
      });
    });
  }

  if (active?.logLines?.length) {
    active.logLines.slice(-10).reverse().forEach(line => {
      items.push(formatCodexLogActivity(line));
    });
  }

  if (active?.status && !items.length) {
    items.push({
      title: formatCodexRunStatus(active.status),
      detail: active.summary || active.failureMessage || active.taskModeReason || ""
    });
  }

  return items.slice(0, 14);
}

function formatCodexApprovalTitle(record) {
  const kind = record?.kind ? record.kind.charAt(0).toUpperCase() + record.kind.slice(1) : "Action";
  const decision = record?.decision ? ` / ${record.decision}` : "";
  return `${kind}${decision}`;
}

function formatCodexLogActivity(line) {
  const text = String(line || "");
  const match = text.match(/^\[[^\]]+\]\s+\[([^\]]+)\]\s+(.*)$/);
  if (!match) {
    return { title: "Log", detail: text };
  }

  return {
    title: match[1].charAt(0).toUpperCase() + match[1].slice(1),
    detail: match[2]
  };
}

function formatCodexRunStatus(status) {
  switch ((status || "").toLowerCase()) {
    case "queued":
      return "Queued";
    case "processing":
      return "Thinking through next step";
    case "running-command":
      return "Running command";
    case "waiting-approval":
      return "Waiting for approval";
    case "completed":
      return "Completed";
    case "failed":
      return "Failed";
    case "stopped":
      return "Stopped";
    default:
      return status || "Idle";
  }
}

function getCodexConnectionLabel() {
  if (masterAppState.codexConnectionState === "open") {
    return "Live";
  }
  if (masterAppState.codexConnectionState === "error") {
    return "Offline";
  }
  return "Connecting";
}

function getRecentActivities() {
  const latestByApp = new Map();

  masterAppState.latestApps.forEach(app => {
    const events = [];

    if (app.installedAtUtc) {
      events.push({
        sortTime: new Date(app.installedAtUtc).getTime(),
        badge: getBadgeLetter(app.displayName),
        title: app.displayName,
        copy: `Installed - ${getInstallVersionLabel(app)}`,
        time: timeAgo(app.installedAtUtc)
      });
    }

    if (app.runState?.startedAtUtc) {
      events.push({
        sortTime: new Date(app.runState.startedAtUtc).getTime(),
        badge: getBadgeLetter(app.displayName),
        title: app.displayName,
        copy: `Running - ${app.runState.message || "Live now"}`,
        time: timeAgo(app.runState.startedAtUtc)
      });
    }

    if (app.runState?.stoppedAtUtc) {
      events.push({
        sortTime: new Date(app.runState.stoppedAtUtc).getTime(),
        badge: getBadgeLetter(app.displayName),
        title: app.displayName,
        copy: "Stopped",
        time: timeAgo(app.runState.stoppedAtUtc)
      });
    }

    const latest = events
      .filter(item => Number.isFinite(item.sortTime))
      .sort((left, right) => right.sortTime - left.sortTime)[0];

    if (latest) {
      latestByApp.set(app.id, latest);
    }
  });

  return [...latestByApp.values()]
    .sort((left, right) => right.sortTime - left.sortTime)
    .slice(0, 5);
}

function getStoreSubtitle(app) {
  if (app.runState?.message) {
    return app.runState.message;
  }
  if (app.appType === "static") {
    return "Launch instantly through MasterApp.";
  }
  return app.runState?.isRunning ? "Open the live app." : "Launch through MasterApp.";
}

function getDefaultCodexModes() {
  return [
    { slug: "auto", displayName: "Auto" },
    { slug: "action", displayName: "Action" },
    { slug: "investigate", displayName: "Investigate" },
    { slug: "code", displayName: "Code" },
    { slug: "ask", displayName: "Ask" }
  ];
}

async function getJson(url) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return response.json();
}

async function postJson(url, body) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body || {})
  });
  const data = await response.json();
  if (!response.ok || data.ok === false) {
    throw new Error(data.message || `${response.status} ${response.statusText}`);
  }
  return data;
}

async function postAction(url) {
  try {
    const response = await fetch(url, { method: "POST" });
    const data = await response.json();
    alert(data.message || (data.ok ? "Done" : "Completed"));
    await refreshAll();
  } catch (error) {
    alert(error.message);
  }
}

async function refreshStatus() {
  const status = await getJson("/api/status");
  if (setLatestStatus(status)) {
    renderAppShell();
  }
}

async function refreshApps() {
  const apps = normalizeApps(await getJson("/api/apps"));
  if (setLatestApps(apps)) {
    renderAppShell();
  }
}

async function refreshCodex() {
  try {
    const codex = await getJson("/api/codex");
    if (setLatestCodex(codex)) {
      renderCodexUi();
    }
  } catch (error) {
    masterAppState.codexLastError = error.message;
    renderCodexUi();
  }
}

async function loadLog(name) {
  try {
    const data = await getJson(`/api/logs/${name}?lines=200`);
    if (setLatestLog(name, (data.lines || []).join("\n") || "No lines yet.")) {
      renderAppShell();
    }
  } catch (error) {
    if (setLatestLog(name, error.message)) {
      renderAppShell();
    }
  }
}

function openPublic() {
  const url = masterAppState.latestStatus?.publicUrl;
  if (url) {
    window.open(url, "_blank", "noopener,noreferrer");
  }
}

async function refreshAll() {
  try {
    const [status, apps, codex] = await Promise.all([
      getJson("/api/status"),
      getJson("/api/apps"),
      getJson("/api/codex").catch(() => masterAppState.latestCodex || null)
    ]);

    const statusChanged = setLatestStatus(status);
    const appsChanged = setLatestApps(normalizeApps(apps));
    const codexChanged = codex ? setLatestCodex(codex) : false;
    if (statusChanged || appsChanged || codexChanged) {
      codexChanged && !statusChanged && !appsChanged ? renderCodexUi() : renderAppShell();
    }
  } catch (error) {
    masterAppState.latestStatus = {
      tunnel: {
        status: "Disconnected",
        isRunning: false,
        lastError: "UNREACHABLE",
        lastMessage: "Phone connection lost. Tunnel may be stopped."
      }
    };
    masterAppState.latestStatusKey = serializeValue(masterAppState.latestStatus);
    masterAppState.latestLogText = error.message;
    renderAppShell();
  }
}

async function submitCodexPrompt() {
  const prompt = masterAppState.codexDraft.trim();
  if (!prompt) {
    masterAppState.codexLastError = "Enter a prompt first.";
    renderCodexUi();
    return;
  }

  const sessionId = getSharedCodexSessionIdForSubmit();
  const workspacePath = masterAppState.codexSelectedWorkspace || masterAppState.latestCodex?.configuredWorkspaces?.[0]?.path || "";
  const model = masterAppState.codexSelectedModel || masterAppState.latestCodex?.currentModel || "";
  const provider = masterAppState.codexSelectedProvider || masterAppState.latestCodex?.currentProvider || "codex";
  const mode = masterAppState.codexSelectedMode || "auto";
  const optimisticRun = {
    id: `pending-${Date.now()}`,
    sharedSessionId: sessionId,
    prompt,
    provider,
    requestedMode: mode,
    taskMode: mode === "auto" ? "" : mode,
    taskModeSource: mode === "auto" ? "auto" : "manual",
    taskModeConfidence: mode === "auto" ? 0 : 1,
    workspacePath,
    model,
    status: "processing",
    responseText: "",
    changedFiles: [],
    approvalHistory: [],
    buildResult: null,
    restartStatus: null,
    logLines: [],
    startedAtUtc: new Date().toISOString()
  };

  try {
    masterAppState.codexLastError = "";
    masterAppState.codexStickToBottom = true;
    setLatestCodex({
      ...(masterAppState.latestCodex || {}),
      activeRun: optimisticRun,
      pendingApproval: null
    });
    renderCodexUi();

    const result = await postJson("/api/codex/messages", {
      prompt,
      sessionId,
      workspacePath,
      provider,
      model,
      mode
    });
    if (result?.run) {
      setLatestCodex({
        ...(masterAppState.latestCodex || {}),
        activeRun: result.run,
        pendingApproval: null
      });
    }
    masterAppState.codexDraft = "";
    renderCodexUi();
    void refreshCodex();
  } catch (error) {
    masterAppState.codexLastError = error.message;
    setLatestCodex({
      ...(masterAppState.latestCodex || {}),
      activeRun: {
        ...optimisticRun,
        status: "failed",
        failureMessage: error.message
      }
    });
    renderCodexUi();
  }
}

async function updateCodexModel(model) {
  const parsed = parseCodexModelValue(model);
  if (!parsed.slug) {
    return;
  }

  try {
    masterAppState.codexLastError = "";
    masterAppState.codexSelectedProvider = parsed.provider;
    masterAppState.codexSelectedModel = parsed.slug;
    await postJson("/api/codex/model", { provider: parsed.provider, model: parsed.slug });
    await refreshCodex();
  } catch (error) {
    masterAppState.codexLastError = error.message;
    renderCodexUi();
  }
}

async function resolveCodexApproval(decision) {
  const approval = masterAppState.latestCodex?.pendingApproval;
  if (!approval) {
    return;
  }

  try {
    masterAppState.codexLastError = "";
    await postJson("/api/codex/approval", {
      runId: approval.runId,
      approvalId: approval.id,
      decision
    });
    await refreshCodex();
  } catch (error) {
    masterAppState.codexLastError = error.message;
    renderCodexUi();
  }
}

async function stopCodexSession() {
  const active = masterAppState.latestCodex?.activeRun;
  if (!active || isCodexRunTerminal(active.status)) {
    return;
  }

  try {
    masterAppState.codexLastError = "";
    await postJson("/api/codex/stop", { runId: active.id });
    await refreshCodex();
  } catch (error) {
    masterAppState.codexLastError = error.message;
    renderCodexUi();
  }
}

async function startNewCodexSession() {
  const active = masterAppState.latestCodex?.activeRun;

  try {
    masterAppState.codexLastError = "";
    await postJson("/api/codex/session/new", { runId: active?.id || "" });
    masterAppState.codexDraft = "";
    masterAppState.codexSelectedChatId = "";
    masterAppState.codexRecentsOpen = false;
    masterAppState.codexScrollTop = 0;
    masterAppState.codexStickToBottom = true;
    setLatestCodex({
      ...(masterAppState.latestCodex || {}),
      currentSessionId: "",
      activeRun: null,
      pendingApproval: null
    });
    renderCodexUi();
    await refreshCodex();
  } catch (error) {
    masterAppState.codexLastError = error.message;
    renderCodexUi();
  }
}

function normalizeApps(apps) {
  return (apps || []).map(app => ({
    ...app,
    displayName: app.displayName || app.shortName || app.name || app.id,
    shortName: app.shortName || app.displayName || app.name || app.id
  }));
}

function setLatestStatus(status) {
  const key = serializeValue(getRenderableStatus(status));
  if (key === masterAppState.latestStatusKey) {
    masterAppState.latestStatus = status;
    return false;
  }

  masterAppState.latestStatus = status;
  masterAppState.latestStatusKey = key;
  return true;
}

function setLatestApps(apps) {
  const key = serializeValue(getRenderableApps(apps));
  if (key === masterAppState.latestAppsKey) {
    masterAppState.latestApps = apps;
    return false;
  }

  masterAppState.latestApps = apps;
  masterAppState.latestAppsKey = key;
  return true;
}

function setLatestLog(name, text) {
  const key = `${name}\n${text}`;
  if (key === masterAppState.latestLogKey) {
    return false;
  }

  masterAppState.latestLogName = name;
  masterAppState.latestLogText = text;
  masterAppState.latestLogKey = key;
  return true;
}

function setLatestCodex(codex) {
  const normalized = normalizeCodex(codex);
  const key = serializeValue(normalized);
  if (key === masterAppState.latestCodexKey) {
    masterAppState.latestCodex = normalized;
    if (!masterAppState.codexSelectedWorkspace && normalized.configuredWorkspaces?.length) {
      masterAppState.codexSelectedWorkspace = normalized.configuredWorkspaces[0].path;
    }
    if (!masterAppState.codexSelectedModel && normalized.currentModel) {
      masterAppState.codexSelectedModel = normalized.currentModel;
    }
    if (!masterAppState.codexSelectedProvider && normalized.currentProvider) {
      masterAppState.codexSelectedProvider = normalized.currentProvider;
    }
    if (masterAppState.codexSelectedChatId && normalized.recentChats?.length && !normalized.recentChats.some(item => item.id === masterAppState.codexSelectedChatId)) {
      masterAppState.codexSelectedChatId = "";
    }
    if (!masterAppState.codexSelectedChatId && normalized.currentSessionId && normalized.recentChats?.some(item => item.id === normalized.currentSessionId)) {
      masterAppState.codexSelectedChatId = normalized.currentSessionId;
    }
    return false;
  }

  masterAppState.latestCodex = normalized;
  masterAppState.latestCodexKey = key;
  if (!masterAppState.codexSelectedWorkspace && normalized.configuredWorkspaces?.length) {
    masterAppState.codexSelectedWorkspace = normalized.configuredWorkspaces[0].path;
  }
  if (!masterAppState.codexSelectedModel && normalized.currentModel) {
    masterAppState.codexSelectedModel = normalized.currentModel;
  }
  if (!masterAppState.codexSelectedProvider && normalized.currentProvider) {
    masterAppState.codexSelectedProvider = normalized.currentProvider;
  }
  if (masterAppState.codexSelectedChatId && normalized.recentChats?.length && !normalized.recentChats.some(item => item.id === masterAppState.codexSelectedChatId)) {
    masterAppState.codexSelectedChatId = "";
  }
  if (!masterAppState.codexSelectedChatId && normalized.currentSessionId && normalized.recentChats?.some(item => item.id === normalized.currentSessionId)) {
    masterAppState.codexSelectedChatId = normalized.currentSessionId;
  }
  return true;
}

function normalizeCodex(codex) {
  return {
    ...(codex || {}),
    availableModes: codex?.availableModes || getDefaultCodexModes(),
    configuredWorkspaces: codex?.configuredWorkspaces || [],
    availableModels: codex?.availableModels || [],
    recentChats: codex?.recentChats || [],
    currentSessionId: codex?.currentSessionId || "",
    activeRun: codex?.activeRun || null,
    pendingApproval: codex?.pendingApproval || null,
    currentProvider: codex?.currentProvider || "codex",
    currentModel: codex?.currentModel || "",
    usage: codex?.usage || null,
    autoApproveReadOnlyCommands: !!codex?.autoApproveReadOnlyCommands,
    cliProbe: codex?.cliProbe || null,
    lastRelaunch: codex?.lastRelaunch || null
  };
}

function isCodexRunTerminal(status) {
  return ["completed", "failed", "restart-scheduled", "stopped"].includes((status || "").toLowerCase());
}

function connectCodexEvents() {
  if (masterAppState.codexEventSource) {
    return;
  }

  const source = new EventSource("/api/codex/events");
  masterAppState.codexEventSource = source;

  source.onopen = () => {
    masterAppState.codexConnectionState = "open";
    renderCodexUi();
  };

  source.onerror = () => {
    masterAppState.codexConnectionState = "error";
    renderCodexUi();
  };

  source.onmessage = event => {
    try {
      const message = JSON.parse(event.data);
      applyCodexEvent(message);
    } catch (error) {
      masterAppState.codexLastError = error.message;
      renderCodexUi();
    }
  };
}

function applyCodexEvent(message) {
  const { type, payload } = message || {};
  if (type === "codex.snapshot") {
    if (setLatestCodex(payload)) {
      renderCodexUi();
    }
    return;
  }
}

function serializeValue(value) {
  return JSON.stringify(value ?? null);
}

function encodeCodexModelValue(provider, slug) {
  return `${provider || "codex"}::${slug || ""}`;
}

function parseCodexModelValue(value) {
  const [provider, ...rest] = String(value || "").split("::");
  return {
    provider: provider || "codex",
    slug: rest.join("::")
  };
}

function getRenderableStatus(status) {
  if (!status) {
    return null;
  }

  return {
    localUrl: status.localUrl,
    publicUrl: status.publicUrl,
    publicHostname: status.publicHostname,
    tokenPresent: status.tokenPresent,
    configIssues: status.configIssues,
    tunnel: status.tunnel ? {
      status: status.tunnel.status,
      isRunning: status.tunnel.isRunning,
      lastError: status.tunnel.lastError,
      lastMessage: status.tunnel.lastMessage
    } : null,
    packageWatcher: status.packageWatcher ? {
      isRunning: status.packageWatcher.isRunning,
      status: status.packageWatcher.status
    } : null,
    lastPackageResult: status.lastPackageResult ? {
      success: status.lastPackageResult.success,
      sourceFileName: status.lastPackageResult.sourceFileName,
      appId: status.lastPackageResult.appId,
      version: status.lastPackageResult.version,
      message: status.lastPackageResult.message,
      timestampUtc: status.lastPackageResult.timestampUtc
    } : null,
    lastPublishResult: status.lastPublishResult ? {
      success: status.lastPublishResult.success,
      appId: status.lastPublishResult.appId,
      version: status.lastPublishResult.version,
      message: status.lastPublishResult.message
    } : null
  };
}

function getCodexUsageRows(usage) {
  if (!usage) {
    return [{ label: "Usage", value: "Loading..." }];
  }

  const rows = [
    { label: "Provider", value: usage.provider || "-" },
    { label: "Model", value: usage.model || "-" }
  ];

  (usage.items || []).forEach(item => {
    rows.push({
      label: item.label || "Usage",
      value: item.display || "-"
    });
  });

  return rows;
}

function captureCodexScrollState(container) {
  if (!(container instanceof HTMLElement)) {
    return {
      top: masterAppState.codexScrollTop,
      stickToBottom: masterAppState.codexStickToBottom
    };
  }

  const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
  const top = Math.max(0, Math.min(container.scrollTop, maxScrollTop));
  const distanceFromBottom = Math.max(0, container.scrollHeight - container.clientHeight - top);
  const stickToBottom = distanceFromBottom <= 48;

  masterAppState.codexScrollTop = top;
  masterAppState.codexStickToBottom = stickToBottom;

  return {
    top,
    stickToBottom
  };
}

function restoreCodexScrollState(state) {
  const container = document.querySelector(".codex-chat-scroll");
  if (!(container instanceof HTMLElement)) {
    return;
  }

  const shouldStick = !!state?.stickToBottom;
  if (shouldStick) {
    container.scrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
    masterAppState.codexScrollTop = container.scrollTop;
    masterAppState.codexStickToBottom = true;
    return;
  }

  const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
  container.scrollTop = Math.max(0, Math.min(state?.top ?? 0, maxScrollTop));
  masterAppState.codexScrollTop = container.scrollTop;
}

function bindCodexScrollTracking() {
  const container = document.querySelector(".codex-chat-scroll");
  if (!(container instanceof HTMLElement)) {
    return;
  }

  container.addEventListener("scroll", () => {
    captureCodexScrollState(container);
  }, { passive: true });
}

function initZoomLock() {
  if (masterAppState.zoomLockBound) {
    return;
  }

  const shouldBlockZoomHotkey = event => {
    const key = String(event.key || "").toLowerCase();
    return (event.ctrlKey || event.metaKey) && ["+", "=", "-", "_", "0"].includes(key);
  };

  document.addEventListener("wheel", event => {
    if (event.ctrlKey || event.metaKey) {
      event.preventDefault();
    }
  }, { passive: false });

  document.addEventListener("keydown", event => {
    if (shouldBlockZoomHotkey(event)) {
      event.preventDefault();
    }
  });

  document.addEventListener("gesturestart", event => event.preventDefault());
  document.addEventListener("gesturechange", event => event.preventDefault());
  document.addEventListener("gestureend", event => event.preventDefault());

  masterAppState.zoomLockBound = true;
}

function getRenderableApps(apps) {
  return (apps || []).map(app => ({
    id: app.id,
    displayName: app.displayName,
    shortName: app.shortName,
    activeVersion: app.activeVersion,
    versions: app.versions,
    iconUrl: app.iconUrl,
    launchUrl: app.launchUrl,
    canPublish: app.canPublish,
    storeVisible: app.storeVisible,
    showInLibrary: app.showInLibrary,
    installedAtUtc: app.installedAtUtc,
    lastPublishedArtifact: app.lastPublishedArtifact ? {
      artifactKind: app.lastPublishedArtifact.artifactKind,
      outputPath: app.lastPublishedArtifact.outputPath,
      zipPath: app.lastPublishedArtifact.zipPath
    } : null,
    runState: app.runState ? {
      status: app.runState.status,
      isRunning: app.runState.isRunning,
      processId: app.runState.processId,
      port: app.runState.port,
      url: app.runState.url,
      message: app.runState.message,
      startedAtUtc: app.runState.startedAtUtc,
      stoppedAtUtc: app.runState.stoppedAtUtc
    } : null
  }));
}

function getInstallVersionNumber(app) {
  return Math.max(1, Array.isArray(app.versions) ? app.versions.length : 1);
}

function getInstallVersionLabel(app) {
  return `v${getInstallVersionNumber(app)}`;
}

function getFallbackLogo(app) {
  const key = app.id || app.displayName || app.name || "app";
  return fallbackLogos[hashString(key) % fallbackLogos.length];
}

function hashString(value) {
  return [...String(value)].reduce((hash, char) => ((hash << 5) - hash + char.charCodeAt(0)) >>> 0, 0);
}

async function exportApp(appId) {
  await postAction(`/api/apps/${encodeURIComponent(appId)}/publish`);
}

async function deleteApp(appId) {
  const app = masterAppState.latestApps.find(item => item.id === appId);
  const appName = app?.displayName || appId;
  const ok = window.confirm(`Delete "${appName}" from Library?\n\nThis removes the installed app from MasterApp.`);
  if (!ok) {
    return;
  }

  await postAction(`/api/apps/${encodeURIComponent(appId)}/delete`);
}

function icon(name, className = "") {
  const iconName = iconNames.has(name) ? name : "globe";
  const classAttribute = className ? ` ${className}` : "";
  return `<span class="app-icon${classAttribute}" aria-hidden="true" style="--app-icon-mask: url('/icons/${iconName}.svg');"></span>`;
}

function iconWrap(inner) {
  return `<span class="icon-wrap">${inner}</span>`;
}

function buttonContent(label, iconName) {
  return `
    <span class="button-content">
      ${iconName ? `<span class="button-icon" aria-hidden="true">${icon(iconName)}</span>` : ""}
      <span class="button-label">${escapeHtml(label)}</span>
    </span>
  `;
}

function statusDot(tone) {
  const klass = tone === "success" ? "is-success" : tone === "warning" ? "is-warning" : tone === "danger" ? "is-danger" : "";
  return `<span class="status-chip ${klass}"><span class="status-dot"></span></span>`;
}

function timeAgo(value) {
  if (!value) {
    return "just now";
  }

  const parsed = new Date(value);
  const timestamp = parsed.getTime();
  if (!Number.isFinite(timestamp)) {
    return "recently";
  }

  const seconds = Math.max(0, Math.round((Date.now() - timestamp) / 1000));
  if (seconds < 60) return `${seconds || 1}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
}

function formatDateTime(value) {
  if (!value) {
    return "-";
  }

  const parsed = new Date(value);
  if (!Number.isFinite(parsed.getTime())) {
    return "-";
  }

  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(parsed);
}

function formatShortDate(value) {
  if (!value) {
    return "recently";
  }

  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric"
  }).format(new Date(value));
}

function trimPath(value) {
  const normalized = String(value || "").replace(/\\/g, "/");
  const parts = normalized.split("/");
  return parts.slice(-2).join("/") || normalized;
}

function getBadgeLetter(value) {
  return String(value || "?").trim().slice(0, 1).toUpperCase() || "?";
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value);
}

window.postAction = postAction;
window.refreshAll = refreshAll;
window.refreshCodex = refreshCodex;
window.openPublic = openPublic;
window.loadLog = loadLog;
window.exportApp = exportApp;
window.deleteApp = deleteApp;

document.addEventListener("DOMContentLoaded", initMasterApp);




