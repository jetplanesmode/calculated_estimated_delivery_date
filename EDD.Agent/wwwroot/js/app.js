const messagesEl = document.getElementById("messages");
const form = document.getElementById("form");
const input = document.getElementById("input");
const sendBtn = document.getElementById("send");

/** @type {{ role: string, content: string }[]} */
const history = [];

/** Matches `{{PICKUP_DATE}}` with optional spaces inside braces. */
const pickupDateToken = /\{\{\s*PICKUP_DATE\s*\}\}/g;

/** Today's UTC calendar date at 17:00 — inserted into sample prompts when the UI renders. */
function pickupDatePhraseTodayUtc() {
  const now = new Date();
  const monthNames = [
    "January",
    "February",
    "March",
    "April",
    "May",
    "June",
    "July",
    "August",
    "September",
    "October",
    "November",
    "December",
  ];
  const y = now.getUTCFullYear();
  const m = now.getUTCMonth();
  const d = now.getUTCDate();
  return `${monthNames[m]} ${d}, ${y} at 17:00 UTC`;
}

/** Client-side only: swap template token for today's pickup phrase whenever text hits the UI or composer. */
function expandPickupDateTemplate(str) {
  if (typeof str !== "string") return "";
  return str.replace(pickupDateToken, pickupDatePhraseTodayUtc());
}

function scrollToBottom() {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

function addBubble(role, text, isError = false) {
  const wrap = document.createElement("div");
  wrap.className = `msg msg--${isError ? "error" : role}`;
  const label = document.createElement("span");
  label.className = "msg__label";
  label.textContent = isError ? "Error" : role === "user" ? "You" : "Assistant";
  const bubble = document.createElement("div");
  bubble.className = "msg__bubble";
  bubble.textContent = text;
  wrap.appendChild(label);
  wrap.appendChild(bubble);
  messagesEl.appendChild(wrap);
  scrollToBottom();
}

function addTyping() {
  const wrap = document.createElement("div");
  wrap.className = "msg msg--assistant";
  wrap.dataset.typing = "1";
  wrap.innerHTML =
    '<span class="msg__label">Assistant</span>' +
    '<div class="msg__bubble"><span class="typing" aria-label="Thinking"><span></span><span></span><span></span></span></div>';
  messagesEl.appendChild(wrap);
  scrollToBottom();
  return wrap;
}

function removeTyping(el) {
  el.remove();
}

/**
 * Explains why `fetch` failed: HTTP line first, then server `error` / `detail`, or raw body
 * (so you do not only see the generic "Request failed" when the body is HTML or not JSON).
 */
function formatApiError(/** @type {Response} */ res, data, rawText, jsonError) {
  const statusLine = `HTTP ${res.status}${res.statusText ? ` ${res.statusText}` : ""}`;

  if (jsonError) {
    return `${statusLine}\n\nThe response was not JSON. First part of the body:\n\n${rawText.slice(0, 2000)}${rawText.length > 2000 ? "…" : ""}`;
  }

  const parts = [statusLine];

  if (typeof data.error === "string" && data.error.length > 0) {
    parts.push(data.error);
  }

  const detail = data.detail;
  if (typeof detail === "string" && detail.length > 0) {
    try {
      const parsed = JSON.parse(detail);
      const inner = parsed?.error?.message ?? parsed?.message;
      if (typeof inner === "string") {
        parts.push(inner);
      } else {
        parts.push(JSON.stringify(parsed, null, 2).slice(0, 4000));
      }
    } catch {
      parts.push(detail.slice(0, 2000));
    }
  }

  if (typeof data.title === "string" && data.title.trim().length > 0) {
    parts.push(data.title.trim());
  }

  if (
    parts.length === 1 &&
    data &&
    typeof data === "object" &&
    Object.keys(data).length > 0
  ) {
    parts.push(JSON.stringify(data, null, 2).slice(0, 2500));
  }

  if (parts.length === 1 && rawText && rawText.length > 0) {
    parts.push(rawText.slice(0, 2000) + (rawText.length > 2000 ? "…" : ""));
  }

  return parts.join("\n\n");
}

function autosize() {
  input.style.height = "auto";
  input.style.height = `${Math.min(input.scrollHeight, 180)}px`;
}

input.addEventListener("input", autosize);

input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    form.requestSubmit();
  }
});

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const text = input.value.trim();
  if (!text) return;

  input.value = "";
  autosize();

  history.push({ role: "user", content: text });
  addBubble("user", text);

  sendBtn.disabled = true;
  const typingEl = addTyping();

  try {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ messages: history }),
    });

    const rawText = await res.text();
    let /** @type {Record<string, unknown>} */ data = {};
    let jsonError = false;
    if (rawText.length > 0) {
      try {
        data = JSON.parse(rawText);
      } catch {
        jsonError = true;
      }
    }

    removeTyping(typingEl);

    if (!res.ok) {
      addBubble(
        "assistant",
        formatApiError(res, data, rawText, jsonError),
        true,
      );
      history.pop();
      return;
    }

    if (jsonError) {
      addBubble(
        "assistant",
        `Expected JSON from chat API.\n\n${rawText.slice(0, 1500)}`,
        true,
      );
      history.pop();
      return;
    }

    const reply = typeof data.message === "string" ? data.message : "";
    history.push({ role: "assistant", content: reply });
    addBubble("assistant", reply);
  } catch (err) {
    removeTyping(typingEl);
    addBubble("assistant", err instanceof Error ? err.message : "Network error", true);
    history.pop();
  } finally {
    sendBtn.disabled = false;
    input.focus();
  }
});

function wirePromptBubble(btn) {
  btn.addEventListener("click", () => {
    const raw = btn.dataset.promptTemplate?.trim() ?? "";
    const text = expandPickupDateTemplate(raw);
    if (!text) return;
    input.value = text;
    autosize();
    input.focus();
  });
}

/** @param {HTMLElement} container */
async function loadSampleQuestions(container) {
  const fallbackPlaceholder = "Ask anything…";
  try {
    const res = await fetch("/data-prompt/prompt-display.json", {
      cache: "no-store",
    });
    if (!res.ok) throw new Error(String(res.status));
    const data = await res.json();
    if (typeof data.inputPlaceholder === "string" && data.inputPlaceholder.length > 0) {
      input.placeholder = expandPickupDateTemplate(data.inputPlaceholder);
    } else {
      input.placeholder = fallbackPlaceholder;
    }
    const samples = Array.isArray(data.samples) ? data.samples : [];
    for (const s of samples) {
      const rawPrompt = typeof s.prompt === "string" ? s.prompt.trim() : "";
      const label = typeof s.label === "string" ? s.label.trim() : "";
      if (!rawPrompt || !label) continue;
      const displayPrompt = expandPickupDateTemplate(rawPrompt);
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "prompt-bubble";
      btn.dataset.promptTemplate = rawPrompt;
      if (typeof s.id === "string" && s.id.length > 0) btn.id = `sample-${s.id}`;
      const aria =
        typeof s.ariaLabel === "string" && s.ariaLabel.trim().length > 0
          ? s.ariaLabel.trim()
          : "Insert example question into the message box";
      btn.setAttribute("aria-label", aria);
      const labelEl = document.createElement("span");
      labelEl.className = "prompt-bubble__label";
      labelEl.textContent = label;
      const textEl = document.createElement("span");
      textEl.className = "prompt-bubble__text";
      textEl.textContent = displayPrompt;
      btn.appendChild(labelEl);
      btn.appendChild(textEl);
      container.appendChild(btn);
      wirePromptBubble(btn);
    }
  } catch {
    input.placeholder = fallbackPlaceholder;
  }
}

const composerStarters = document.getElementById("composer-starters");
if (composerStarters) {
  void loadSampleQuestions(composerStarters);
}

autosize();
input.focus();
