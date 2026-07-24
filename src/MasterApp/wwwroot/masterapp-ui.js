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
  "trash"
]);

const fallbackLogos = [
  { glyph: "store", start: "#8fc3ff", end: "#4f6dff", color: "#f4f7ff" },
  { glyph: "globe", start: "#6fe0c6", end: "#2e9487", color: "#effffd" },
  { glyph: "library", start: "#ffcf86", end: "#d67a31", color: "#fff7ea" },
  { glyph: "home", start: "#ff9fb0", end: "#d55470", color: "#fff2f5" },
  { glyph: "tunnel", start: "#b59cff", end: "#6c52db", color: "#f6f1ff" },
  { glyph: "logs", start: "#7fe1ff", end: "#2f8eb5", color: "#f1fcff" }
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
  const scrollContainer = appRoot.querySelector(".app-content");
  const contentScrollTop = preserveScroll && scrollContainer instanceof HTMLElement ? scrollContainer.scrollTop : 0;
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
      getQuickActions()[index]?.action();
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

function getQuickActions() {
  const status = masterAppState.latestStatus;
  return [
    { label: "Scan inbox", icon: "inbox", action: () => postAction("/api/packages/rescan") },
    isLazyLocalStatus(status)
      ? { label: "Phone QR", icon: "globe", action: () => openPhoneQr() }
      : { label: "Open tunnel", icon: "tunnel", action: () => openPublic() },
    { label: "Logs", icon: "logs", action: () => setLogFocus() }
  ];
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
    { id: "store", label: "Store", icon: "store" }
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
  const isLazyLocal = isLazyLocalStatus(status);

  return `
    <section class="page ${masterAppState.currentTab === "dashboard" ? "is-active" : ""}" id="page-dashboard">
      <div class="page-head">
        <div>
          <h2 class="page-title">Dashboard</h2>
          <p class="page-subtitle">${escapeHtml(isLazyLocal ? "Runtime, local Wi-Fi access, and installs at a glance." : "Runtime, tunnel, and installs at a glance.")}</p>
        </div>
      </div>

      ${HeroStatusCard(status)}

      <div class="stats-grid">
        ${StatTile("Running apps", String(getRunningApps(apps).length), getRunningApps(apps).length ? `${getRunningApps(apps)[0].displayName} live` : "No active apps")}
        ${StatTile("Stopped apps", String(getStoppedApps(apps).length), getStoppedApps(apps).length ? `${getStoppedApps(apps)[0].displayName} ready` : "Everything is running")}
        ${StatTile(isLazyLocal ? "Access mode" : "Tunnel state", getAccessStatValue(status), getAccessStatMeta(status))}
        ${StatTile("Health", getHealthValue(status), getHealthMeta(status))}
      </div>

      <div class="section-head">
        <h3 class="section-title">Quick actions</h3>
      </div>
      <div class="quick-actions-grid">
        ${getQuickActions().map((item, index) => QuickActionButton(item, index)).join("")}
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

function HeroStatusCard(status) {
  if (isLazyLocalStatus(status)) {
    return LazyLocalStatusCard(status);
  }

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

function LazyLocalStatusCard(status) {
  const packageResult = status?.lastPackageResult;
  const lanUrl = status?.lanUrl || status?.localUrl || "";
  const wifiReady = !!status?.wifiAvailable;
  const chip = StatusChip(wifiReady ? "Wi-Fi ready" : "Local only", wifiReady ? "success" : "warning");
  const detailRows = [
    { label: "Phone URL", value: lanUrl || "-" },
    { label: "Port", value: status?.activeLocalPort ? String(status.activeLocalPort) : "-" },
    { label: "Network", value: status?.wifiNetwork || status?.wifiInterface || "-" },
    { label: "Sessions", value: String(status?.activeRemoteSessions ?? 0) }
  ];

  return `
    <article class="hero-status-card hero-status-card--compact">
      <div class="hero-top hero-top--compact">
        <div class="hero-kicker hero-kicker--tight">${statusDot(wifiReady ? "success" : "warning")} Local Wi-Fi</div>
        ${chip}
      </div>
      <div class="hero-actions hero-actions--compact">
        <button class="primary-button" type="button" onclick="openPhoneQr()">Show phone QR</button>
        <button class="secondary-button" type="button" ${lanUrl ? "" : "disabled"} data-open-link="${escapeAttribute(lanUrl)}">Open local URL</button>
      </div>
      <div class="details-grid">
        ${renderDetailRows(detailRows)}
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

function isLazyLocalStatus(status) {
  return String(status?.runtimeMode || "").toLowerCase() === "lazy-local";
}

function getAccessStatValue(status) {
  if (isLazyLocalStatus(status)) {
    return status?.wifiAvailable ? "Wi-Fi" : "Local";
  }

  return getTunnelStatValue(status);
}

function getAccessStatMeta(status) {
  if (isLazyLocalStatus(status)) {
    return status?.lanUrl || status?.localUrl || "Waiting for local URL";
  }

  return getTunnelStatMeta(status);
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

  const rows = [
    { label: "Runtime mode", value: status.runtimeMode || "standard" },
    { label: "Local URL", value: status.localUrl || "-" },
    ...(isLazyLocalStatus(status) ? [
      { label: "Phone URL", value: status.lanUrl || "-" },
      { label: "Active port", value: status.activeLocalPort ? String(status.activeLocalPort) : "-" },
      { label: "Wi-Fi", value: status.wifiAvailable ? (status.wifiInterface || "Available") : "Not detected" },
      { label: "Remote sessions", value: String(status.activeRemoteSessions ?? 0) }
    ] : [
      { label: "Public URL", value: status.publicUrl || "-" },
      { label: "Hostname", value: status.publicHostname || "-" }
    ]),
    { label: "Settings", value: status.settingsFile || "-" },
    { label: "Logs", value: status.logsDirectory || "-" },
    { label: "Published", value: status.publishedDirectory || "-" }
  ];

  return rows;
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

function openPhoneQr() {
  window.open("/api/phone-qr.svg", "_blank", "noopener,noreferrer");
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
    runtimeMode: status.runtimeMode,
    localUrl: status.localUrl,
    loopbackUrl: status.loopbackUrl,
    lanUrl: status.lanUrl,
    publicUrl: status.publicUrl,
    publicHostname: status.publicHostname,
    activeLocalPort: status.activeLocalPort,
    wifiAvailable: status.wifiAvailable,
    wifiOnly: status.wifiOnly,
    wifiInterface: status.wifiInterface,
    wifiAddress: status.wifiAddress,
    wifiNetwork: status.wifiNetwork,
    remoteSessionRequired: status.remoteSessionRequired,
    activeRemoteSessions: status.activeRemoteSessions,
    pendingQrTickets: status.pendingQrTickets,
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
window.openPublic = openPublic;
window.openPhoneQr = openPhoneQr;
window.loadLog = loadLog;
window.exportApp = exportApp;
window.deleteApp = deleteApp;

document.addEventListener("DOMContentLoaded", initMasterApp);




