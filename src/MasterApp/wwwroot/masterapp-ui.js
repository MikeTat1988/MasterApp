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
  pageMode: document.body?.dataset.page || "dashboard"
};

const iconPaths = {
  bell: "M12 22a2.5 2.5 0 0 0 2.45-2h-4.9A2.5 2.5 0 0 0 12 22Zm7-6V11a7 7 0 1 0-14 0v5L3 18v1h18v-1l-2-2Z",
  settings: "M12 8.2a3.8 3.8 0 1 0 0 7.6 3.8 3.8 0 0 0 0-7.6Zm8.2 4.3-.02-.5 1.63-1.27-1.8-3.12-2 .6a7.9 7.9 0 0 0-1.66-.96l-.3-2.05h-3.6l-.3 2.05c-.58.2-1.14.5-1.66.96l-2-.6-1.8 3.12 1.63 1.27-.02.5.02.5-1.63 1.27 1.8 3.12 2-.6c.52.44 1.08.76 1.66.96l.3 2.05h3.6l.3-2.05c.58-.2 1.14-.52 1.66-.96l2 .6 1.8-3.12-1.63-1.27.02-.5Z",
  search: "m20.5 20.5-4.6-4.6m1.6-5.2a6.8 6.8 0 1 1-13.6 0 6.8 6.8 0 0 1 13.6 0Z",
  filter: "M4 6h16M7 12h10m-7 6h4",
  refresh: "M20 11a8 8 0 0 0-14.4-4.6M4 13a8 8 0 0 0 14.4 4.6M5 4v4h4m6 8h4v4",
  inbox: "M4 6.5h16v11H4zM4 14h5l2 2h2l2-2h5",
  tunnel: "M3.5 12 20.5 4.5l-5 15-4.2-5.3-3.7-2.2Z",
  logs: "M6 4h9l3 3v13H6zM9 11h6M9 15h6M9 7h3",
  home: "M4.5 11.5 12 5l7.5 6.5V20h-5.5v-5h-4v5H4.5z",
  library: "M5 6.5A2.5 2.5 0 0 1 7.5 4H20v14.5A1.5 1.5 0 0 0 18.5 17H7.5A2.5 2.5 0 0 0 5 19.5v-13Zm0 13A1.5 1.5 0 0 1 6.5 18H18",
  store: "M5 8h14l-1.1 10.2A2 2 0 0 1 15.9 20H8.1a2 2 0 0 1-1.99-1.8L5 8Zm3-3h8l1 3H7l1-3Zm1.5 7h5",
  play: "M9 7.5v9l7-4.5-7-4.5Z",
  external: "M14 5h5v5M10 14 19 5M18 12v6H6V6h6",
  more: "M12 7.5a1.2 1.2 0 1 0 0-2.4 1.2 1.2 0 0 0 0 2.4Zm0 6a1.2 1.2 0 1 0 0-2.4 1.2 1.2 0 0 0 0 2.4Zm0 6a1.2 1.2 0 1 0 0-2.4 1.2 1.2 0 0 0 0 2.4Z",
  plus: "M12 5v14M5 12h14",
  close: "M6 6l12 12M18 6 6 18",
  globe: "M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17Zm0 0c2.6 2.3 4 5.2 4 8.5s-1.4 6.2-4 8.5m0-17c-2.6 2.3-4 5.2-4 8.5s1.4 6.2 4 8.5M4.3 9.5h15.4M4.3 14.5h15.4",
  export: "M12 4v10m0 0 4-4m-4 4-4-4M5 15.5v1A2.5 2.5 0 0 0 7.5 19h9a2.5 2.5 0 0 0 2.5-2.5v-1",
  trash: "M5 7.5h14M9.5 4.5h5M8 7.5l.8 11h6.4l.8-11M10 10.5v5M14 10.5v5"
};

const fallbackLogos = [
  { glyph: "store", start: "#8fc3ff", end: "#4f6dff", color: "#f4f7ff" },
  { glyph: "globe", start: "#6fe0c6", end: "#2e9487", color: "#effffd" },
  { glyph: "library", start: "#ffcf86", end: "#d67a31", color: "#fff7ea" },
  { glyph: "home", start: "#ff9fb0", end: "#d55470", color: "#fff2f5" },
  { glyph: "tunnel", start: "#b59cff", end: "#6c52db", color: "#f6f1ff" },
  { glyph: "logs", start: "#7fe1ff", end: "#2f8eb5", color: "#f1fcff" }
];

const quickActions = [
  { label: "Refresh", icon: "refresh", action: () => refreshAll() },
  { label: "Scan inbox", icon: "inbox", action: () => postAction("/api/packages/rescan") },
  { label: "Open tunnel", icon: "tunnel", action: () => openPublic() },
  { label: "Logs", icon: "logs", action: () => setLogFocus() }
];

function initMasterApp() {
  const appRoot = document.getElementById("app");
  if (!appRoot) {
    return;
  }

  if (masterAppState.pageMode === "store") {
    masterAppState.currentTab = "store";
  }

  renderAppShell({ preserveScroll: false });
  refreshAll();
  loadLog(masterAppState.latestLogName);
  setInterval(refreshStatus, 5000);
  setInterval(refreshApps, 9000);
}

function renderAppShell(options = {}) {
  const { preserveScroll = true } = options;
  const appRoot = document.getElementById("app");
  if (!appRoot) {
    return;
  }

  const scrollTop = preserveScroll ? window.scrollY : 0;
  const activeElement = document.activeElement;
  const activeId = activeElement instanceof HTMLElement ? activeElement.id : "";
  const selectionStart = activeElement && "selectionStart" in activeElement ? activeElement.selectionStart : null;
  const selectionEnd = activeElement && "selectionEnd" in activeElement ? activeElement.selectionEnd : null;

  appRoot.innerHTML = `
    <div class="app-frame">
      <div class="app-content">
        ${Header()}
        ${SettingsSheet()}
        ${renderDashboardPage()}
        ${renderLibraryPage()}
        ${renderStorePage()}
      </div>
      ${BottomNav()}
    </div>
  `;

  bindInteractions();

  if (preserveScroll) {
    requestAnimationFrame(() => {
      window.scrollTo({ top: scrollTop, behavior: "auto" });
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
          <summary>Logs</summary>
          <div class="settings-group-body">
            <div class="log-actions">
              ${["app", "tunnel", "packages", "ui"].map(name => `
                <button class="log-button ${masterAppState.latestLogName === name ? "is-active" : ""}" type="button" data-log-name="${name}">${escapeHtml(name)}</button>
              `).join("")}
            </div>
            <pre class="log-viewer">${escapeHtml(masterAppState.latestLogText)}</pre>
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
    { id: "store", label: "Store", icon: "store" }
  ];

  return `
    <nav class="bottom-nav" aria-label="Bottom navigation">
      ${items.map(item => `
        <button class="tab-button ${masterAppState.currentTab === item.id ? "is-active" : ""}" type="button" data-tab="${item.id}">
          ${icon(item.icon)}
          <span class="tab-label">${escapeHtml(item.label)}</span>
        </button>
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
        <div class="page-tools">
          <button class="search-button" type="button" aria-label="Refresh" onclick="refreshAll()">${iconWrap(icon("refresh"))}</button>
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
        <button class="icon-button" type="button" aria-label="Rescan packages" onclick="postAction('/api/packages/rescan')">${iconWrap(icon("plus"))}</button>
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
      <div class="page-head">
        <div>
          <h2 class="page-title">Store</h2>
          <p class="page-subtitle">A cleaner storefront with one tap to launch each app.</p>
        </div>
      </div>

      <div class="store-chip-row">
        ${StoreFilterChip("All", "all", getStoreVisibleApps().length, masterAppState.storeFilter === "all")}
        ${StoreFilterChip("Recent", "recent", getRecentStoreApps().length, masterAppState.storeFilter === "recent")}
      </div>

      <div class="section-head">
        <h3 class="section-title">Browse apps</h3>
      </div>
      <div class="store-grid">
        ${apps.length ? apps.map(renderStoreCard).join("") : `<div class="empty-state">No store apps match this filter yet.</div>`}
      </div>
    </section>
  `;
}

function HeroStatusCard(status) {
  const tunnel = status?.tunnel;
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

  actions.push(`<button class="compact-action-button" type="button" ${app.canPublish ? "" : "disabled"} onclick="exportApp('${app.id}')">${icon("export")} Export</button>`);

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

function FeaturedStoreCard(app) {
  return `
    <article class="featured-store-card">
      <div class="featured-top">
        <span class="featured-label">Featured</span>
        ${StatusChip(app.runState?.isRunning ? "Open" : "Ready", app.runState?.isRunning ? "success" : "warning")}
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
  return `
    <a class="store-card store-card--centered" href="${escapeAttribute(app.launchUrl)}" target="_blank" rel="noreferrer">
      <div class="store-card-main">
        ${appAvatar(app, "store-icon")}
        <div class="store-title-line">
          <h3 class="store-name">${escapeHtml(app.displayName)}</h3>
          <div class="store-version">${escapeHtml(getInstallVersionLabel(app))}</div>
        </div>
      </div>
      <div class="store-footer store-footer--centered">
        <div class="status-chip-row status-chip-row--centered">${StatusChip(app.runState?.isRunning ? "Live" : "Ready", app.runState?.isRunning ? "success" : "warning")}</div>
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
  return masterAppState.latestApps.filter(app => app.storeVisible);
}

function getFilteredStoreApps() {
  const apps = getStoreVisibleApps();
  if (masterAppState.storeFilter === "recent") {
    return getRecentStoreApps();
  }
  return apps;
}

function getRecentStoreApps() {
  return [...getStoreVisibleApps()]
    .sort((left, right) => new Date(right.installedAtUtc || 0) - new Date(left.installedAtUtc || 0))
    .slice(0, 4);
}

function getFeaturedApp() {
  return [...getStoreVisibleApps()]
    .sort((left, right) => Number(!!right.runState?.isRunning) - Number(!!left.runState?.isRunning)
      || new Date(right.installedAtUtc || 0) - new Date(left.installedAtUtc || 0))[0] || null;
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
  return app.runState?.isRunning ? "Open the live app." : "Ready to launch when you are.";
}

async function getJson(url) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return response.json();
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
    const [status, apps] = await Promise.all([
      getJson("/api/status"),
      getJson("/api/apps")
    ]);

    const statusChanged = setLatestStatus(status);
    const appsChanged = setLatestApps(normalizeApps(apps));
    if (statusChanged || appsChanged) {
      renderAppShell();
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

function serializeValue(value) {
  return JSON.stringify(value ?? null);
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
      appId: status.lastPackageResult.appId,
      version: status.lastPackageResult.version,
      message: status.lastPackageResult.message
    } : null,
    lastPublishResult: status.lastPublishResult ? {
      success: status.lastPublishResult.success,
      appId: status.lastPublishResult.appId,
      version: status.lastPublishResult.version,
      message: status.lastPublishResult.message
    } : null
  };
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
function icon(name) {
  const path = iconPaths[name] || iconPaths.globe;
  return `<svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="${path}" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"></path></svg>`;
}

function iconWrap(inner) {
  return `<span class="icon-wrap">${inner}</span>`;
}

function statusDot(tone) {
  const klass = tone === "success" ? "is-success" : tone === "warning" ? "is-warning" : tone === "danger" ? "is-danger" : "";
  return `<span class="status-chip ${klass}"><span class="status-dot"></span></span>`;
}

function timeAgo(value) {
  if (!value) {
    return "just now";
  }

  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000));
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

  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
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
window.openPublic = openPublic;
window.loadLog = loadLog;
window.exportApp = exportApp;
window.deleteApp = deleteApp;

document.addEventListener("DOMContentLoaded", initMasterApp);




