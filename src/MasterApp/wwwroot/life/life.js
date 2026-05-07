const lifeState = {
  root: document.getElementById("life-app"),
  mode: window.location.pathname.toLowerCase().includes("/history") ? "history" : "day",
  todayDate: "",
  currentDate: new URLSearchParams(window.location.search).get("date") || "",
  day: null,
  history: [],
  message: "",
  busy: false,
  uploading: false,
  analyzing: false,
  previewPhoto: null
};

initLifeJournal();

async function initLifeJournal() {
  try {
    if (lifeState.mode === "history") {
      await loadHistory();
    } else {
      await loadDay();
    }
  } catch (error) {
    lifeState.message = error.message || "LifeJournal could not open.";
    render();
  }
}

async function loadDay(date = lifeState.currentDate) {
  lifeState.busy = true;
  render();

  const today = await fetchJson("/api/life/today");
  lifeState.todayDate = today.date;

  if (!date) {
    lifeState.currentDate = today.date;
    lifeState.day = today;
  } else if (date === today.date) {
    lifeState.currentDate = today.date;
    lifeState.day = today;
  } else {
    lifeState.currentDate = date;
    lifeState.day = await fetchJson(`/api/life/day?date=${encodeURIComponent(date)}`);
  }

  lifeState.busy = false;
  render();
}

async function loadHistory() {
  lifeState.busy = true;
  render();
  const today = await fetchJson("/api/life/today");
  lifeState.todayDate = today.date;
  lifeState.history = await fetchJson("/api/life/history");
  lifeState.busy = false;
  render();
}

function render() {
  if (!lifeState.root) {
    return;
  }

  lifeState.root.innerHTML = `
    <section class="life-board">
      <div class="life-shell">
        ${renderHeader()}
        ${lifeState.message ? `<div class="life-message">${escapeHtml(lifeState.message)}</div>` : ""}
        ${lifeState.mode === "history" ? renderHistory() : renderDayBoard()}
      </div>
      ${lifeState.mode === "day" ? renderTakePhotoArea() : ""}
      ${lifeState.previewPhoto ? renderPreview() : ""}
    </section>
  `;

  bindLifeInteractions();
}

function renderHeader() {
  const isHistory = lifeState.mode === "history";
  const dateLabel = isHistory ? "Saved days" : formatDisplayDate(lifeState.currentDate || lifeState.todayDate);
  const nextDate = lifeState.currentDate ? addDays(lifeState.currentDate, 1) : "";
  const canGoNext = lifeState.currentDate && lifeState.todayDate && nextDate <= lifeState.todayDate;

  return `
    <header class="life-header">
      <div class="life-title-block">
        <h1 class="life-title">LifeJournal</h1>
        <p class="life-date">${escapeHtml(dateLabel)}</p>
      </div>
      <div class="life-header-actions">
        ${isHistory ? `<a class="life-back-link" href="/life">Today</a>` : `
          <button class="life-nav-button" type="button" aria-label="Previous day" data-life-prev ${lifeState.currentDate ? "" : "disabled"}>‹</button>
          <button class="life-nav-button" type="button" aria-label="Next day" data-life-next ${canGoNext ? "" : "disabled"}>›</button>
        `}
      </div>
    </header>
  `;
}

function renderDayBoard() {
  if (lifeState.busy && !lifeState.day) {
    return `<div class="life-loading">Opening today's board...</div>`;
  }

  const day = lifeState.day || { photos: [], events: [], tags: [] };
  const isToday = day.date === lifeState.todayDate;

  return `
    <div class="life-controls">
      <button class="life-chip-button is-blue" type="button" data-life-event="woke_up" ${isToday ? "" : "disabled"}>Woke Up</button>
      <button class="life-chip-button is-blue" type="button" data-life-event="went_to_sleep" ${isToday ? "" : "disabled"}>Went To Sleep</button>
      <button class="life-chip-button is-red" type="button" data-life-analyze ${lifeState.analyzing ? "disabled" : ""}>${lifeState.analyzing ? "Analyzing..." : "Analyze Now"}</button>
      <a class="life-mini-button" href="/life/history">History</a>
    </div>
    <div class="life-board-grid">
      ${renderStoryNote(day)}
      ${day.photos.length ? day.photos.map(renderPhotoCard).join("") : renderEmptyBoard(isToday)}
    </div>
  `;
}

function renderStoryNote(day) {
  const hasAnalysis = Boolean(day.analysisMarkdown || day.shortSummary || day.generatedSummary);
  const copy = hasAnalysis
    ? (day.shortSummary || day.generatedSummary || "Summary saved.")
    : "No summary yet - analyze now or wait for tonight.";
  const title = hasAnalysis ? "Today's story" : "Story note";
  const tags = Array.isArray(day.tags) ? day.tags : [];

  return `
    <article class="life-note">
      <h2 class="life-note-title">${escapeHtml(title)}</h2>
      <p class="life-note-copy">${escapeHtml(copy)}</p>
      ${tags.length ? `<div class="life-tags">${tags.map(tag => `<span class="life-tag">${escapeHtml(tag)}</span>`).join("")}</div>` : ""}
    </article>
  `;
}

function renderEmptyBoard(isToday) {
  return `
    <div class="life-empty">
      <div>
        <strong>${isToday ? "Clean board for today" : "No photos on this board"}</strong>
        <span>${isToday ? "Take the first photo and pin it here." : "This day has no saved photo cards."}</span>
      </div>
    </div>
  `;
}

function renderPhotoCard(photo, index) {
  const pin = index % 2 === 0 ? "pin_red.png" : "pin_blue.png";
  const useStaple = index % 3 === 1;
  return `
    <button class="life-photo-card" type="button" data-life-preview="${escapeAttribute(photo.id)}">
      ${useStaple ? `<img class="life-staple" src="/life/assets/staple.png" alt="">` : `<img class="life-pin" src="/life/assets/${pin}" alt="">`}
      <span class="life-photo-image-wrap">
        <img class="life-photo-image" src="${photoUrl(lifeState.day.date, photo.fileName)}" alt="Photo captured at ${escapeAttribute(formatTime(photo.localTimestamp))}" loading="lazy">
      </span>
      <span class="life-photo-time">${escapeHtml(formatTime(photo.localTimestamp))}</span>
    </button>
  `;
}

function renderTakePhotoArea() {
  const isToday = lifeState.day?.date === lifeState.todayDate;
  return `
    <div class="life-take-wrap">
      ${isToday ? `
        <input class="life-hidden-input" id="life-photo-input" type="file" accept="image/*" capture="environment">
        <button class="life-take-button" type="button" data-life-take ${lifeState.uploading ? "disabled" : ""}>
          ${lifeState.uploading ? "Adding photo..." : "Take Photo"}
        </button>
      ` : `
        <button class="life-back-today-button" type="button" data-life-today>Back to Today to add photos</button>
      `}
    </div>
  `;
}

function renderHistory() {
  if (lifeState.busy && lifeState.history.length === 0) {
    return `<div class="life-loading">Finding saved days...</div>`;
  }

  if (!lifeState.history.length) {
    return `
      <div class="life-board-grid">
        <div class="life-empty"><div><strong>No saved days yet</strong><span>Your first board will appear here after photos are saved.</span></div></div>
      </div>
    `;
  }

  return `
    <div class="life-history-list">
      ${lifeState.history.map(item => `
        <a class="life-history-card" href="/life?date=${encodeURIComponent(item.date)}">
          <h2 class="life-history-date">${escapeHtml(formatDisplayDate(item.date))}</h2>
          <p class="life-history-meta">${item.photoCount} photos · ${item.eventCount} events · ${item.hasAnalysis ? "summary saved" : "no summary"}</p>
          <p class="life-history-summary">${escapeHtml(item.shortSummary || "No summary yet.")}</p>
          ${item.tags?.length ? `<div class="life-tags">${item.tags.map(tag => `<span class="life-tag">${escapeHtml(tag)}</span>`).join("")}</div>` : ""}
        </a>
      `).join("")}
    </div>
  `;
}

function renderPreview() {
  const photo = lifeState.previewPhoto;
  return `
    <div class="life-modal" data-life-close-preview>
      <div class="life-modal-card" role="dialog" aria-modal="true" aria-label="Photo preview">
        <img src="${photoUrl(lifeState.day.date, photo.fileName)}" alt="Photo preview">
        <div class="life-modal-row">
          <span>${escapeHtml(formatTime(photo.localTimestamp))}</span>
          <button class="life-mini-button" type="button" data-life-close-preview>Close</button>
        </div>
      </div>
    </div>
  `;
}

function bindLifeInteractions() {
  document.querySelector("[data-life-prev]")?.addEventListener("click", () => navigateDay(addDays(lifeState.currentDate, -1)));
  document.querySelector("[data-life-next]")?.addEventListener("click", () => navigateDay(addDays(lifeState.currentDate, 1)));
  document.querySelector("[data-life-today]")?.addEventListener("click", () => {
    window.location.href = "/life";
  });

  document.querySelector("[data-life-take]")?.addEventListener("click", () => {
    document.getElementById("life-photo-input")?.click();
  });

  document.getElementById("life-photo-input")?.addEventListener("change", event => {
    const file = event.target.files?.[0];
    if (file) {
      uploadPhoto(file);
    }
  });

  document.querySelectorAll("[data-life-event]").forEach(button => {
    button.addEventListener("click", () => saveEvent(button.dataset.lifeEvent));
  });

  document.querySelector("[data-life-analyze]")?.addEventListener("click", analyzeCurrentDay);

  document.querySelectorAll("[data-life-preview]").forEach(button => {
    button.addEventListener("click", () => {
      const id = button.dataset.lifePreview;
      lifeState.previewPhoto = lifeState.day?.photos?.find(photo => photo.id === id) || null;
      render();
    });
  });

  document.querySelectorAll("[data-life-close-preview]").forEach(element => {
    element.addEventListener("click", event => {
      if (event.target === element || element.tagName === "BUTTON") {
        lifeState.previewPhoto = null;
        render();
      }
    });
  });
}

async function navigateDay(date) {
  window.history.replaceState(null, "", `/life?date=${encodeURIComponent(date)}`);
  lifeState.currentDate = date;
  lifeState.message = "";
  await loadDay(date);
}

async function uploadPhoto(file) {
  if (lifeState.day?.date !== lifeState.todayDate) {
    lifeState.message = "Go back to today before adding photos.";
    render();
    return;
  }

  lifeState.uploading = true;
  lifeState.message = "Preparing photo...";
  render();

  try {
    const compressed = await compressImage(file);
    const body = new FormData();
    body.append("photo", compressed, "life-photo.jpg");
    body.append("date", lifeState.todayDate);

    lifeState.message = "Pinning photo to the board...";
    render();
    const result = await fetchJson("/api/life/photo", { method: "POST", body });
    lifeState.day = result.day;
    lifeState.currentDate = result.day.date;
    lifeState.message = "Photo added.";
  } catch (error) {
    lifeState.message = error.message || "Photo upload failed.";
  } finally {
    lifeState.uploading = false;
    render();
  }
}

async function saveEvent(type) {
  if (!type) {
    return;
  }

  lifeState.message = "Saving event...";
  render();
  try {
    const result = await fetchJson("/api/life/event", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type })
    });
    lifeState.day = result.day;
    lifeState.message = type === "woke_up" ? "Woke Up saved." : "Went To Sleep saved.";
  } catch (error) {
    lifeState.message = error.message || "Event save failed.";
  } finally {
    render();
  }
}

async function analyzeCurrentDay() {
  if (!lifeState.day?.date) {
    return;
  }

  lifeState.analyzing = true;
  lifeState.message = "Analyzing the day...";
  render();

  try {
    const result = await fetchJson("/api/life/analyze", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ date: lifeState.day.date })
    });
    lifeState.day = result.day;
    lifeState.message = "Summary saved.";
  } catch (error) {
    lifeState.message = error.message || "Analysis failed.";
  } finally {
    lifeState.analyzing = false;
    render();
  }
}

async function compressImage(file) {
  if (!file.type.startsWith("image/")) {
    throw new Error("Only images can be added.");
  }

  const url = URL.createObjectURL(file);
  try {
    const image = await loadImage(url);
    const maxWidth = 900;
    const scale = image.naturalWidth > maxWidth ? maxWidth / image.naturalWidth : 1;
    const width = Math.max(1, Math.round(image.naturalWidth * scale));
    const height = Math.max(1, Math.round(image.naturalHeight * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    context.drawImage(image, 0, 0, width, height);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", 0.75));
    if (!blob) {
      return file;
    }

    return new File([blob], "life-photo.jpg", { type: "image/jpeg" });
  } finally {
    URL.revokeObjectURL(url);
  }
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error("Could not read this image."));
    image.src = url;
  });
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = null;
    }
  }

  if (!response.ok || payload?.ok === false) {
    throw new Error(payload?.message || `Request failed: ${response.status}`);
  }

  return payload;
}

function photoUrl(date, filename) {
  return `/api/life/photo/${encodeURIComponent(date)}/${encodeURIComponent(filename)}`;
}

function formatTime(value) {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  return new Intl.DateTimeFormat("en", { hour: "2-digit", minute: "2-digit" }).format(date);
}

function formatDisplayDate(value) {
  if (!value) {
    return "";
  }

  const date = new Date(`${value}T12:00:00`);
  return new Intl.DateTimeFormat("en", {
    weekday: "short",
    day: "numeric",
    month: "short"
  }).format(date);
}

function addDays(value, delta) {
  const date = new Date(`${value}T12:00:00`);
  date.setDate(date.getDate() + delta);
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replace(/`/g, "&#096;");
}
