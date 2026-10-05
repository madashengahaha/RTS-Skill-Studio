const state = {
  providers: [],
  selectedProvider: null,
  skills: [],
  selectedSkillId: null,
  sending: false,
};

const elements = {
  providerSelect: document.querySelector("#providerSelect"),
  providerStatus: document.querySelector("#providerStatus"),
  refreshProviders: document.querySelector("#refreshProviders"),
  messages: document.querySelector("#messages"),
  emptyState: document.querySelector("#emptyState"),
  conversation: document.querySelector("#conversation"),
  composer: document.querySelector("#composer"),
  messageInput: document.querySelector("#messageInput"),
  sendButton: document.querySelector("#sendButton"),
  inspectorMeta: document.querySelector("#inspectorMeta"),
  inspectorContent: document.querySelector("#inspectorContent"),
  skillCount: document.querySelector("#skillCount"),
  skillSearch: document.querySelector("#skillSearch"),
  skillList: document.querySelector("#skillList"),
  workspaceState: document.querySelector("#workspaceState"),
  workspacePath: document.querySelector("#workspacePath"),
  workspaceRevision: document.querySelector("#workspaceRevision"),
  toast: document.querySelector("#toast"),
};

const providerLabels = {
  ollama: "Ollama 本地",
  ccswitch: "CC Switch",
  openai: "OpenAI",
};

function icon(name) {
  return `<i data-lucide="${name}"></i>`;
}

function renderIcons() {
  if (window.lucide) {
    window.lucide.createIcons();
  }
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function showToast(message, kind = "info") {
  elements.toast.textContent = message;
  elements.toast.classList.toggle("is-error", kind === "error");
  elements.toast.classList.add("is-visible");
  window.clearTimeout(showToast.timeoutId);
  showToast.timeoutId = window.setTimeout(() => {
    elements.toast.classList.remove("is-visible");
  }, 3200);
}

function providerLabel(provider) {
  return providerLabels[provider.name] || provider.name;
}

function setProviderStatus(provider, kind, text) {
  const className =
    kind === "ready"
      ? "is-ready"
      : kind === "error"
        ? "is-error"
        : "is-pending";

  elements.providerStatus.innerHTML = `
    <span class="status-dot ${className}"></span>
    <span>${escapeHtml(text)}</span>
  `;
}

async function loadProviders() {
  elements.providerSelect.disabled = true;
  elements.providerSelect.innerHTML = "<option>正在读取...</option>";
  setProviderStatus(null, "pending", "检查连接中");

  try {
    const response = await fetch("/api/v1/llm/providers");
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    state.providers = await response.json();
    elements.providerSelect.innerHTML = state.providers
      .map(
        (provider) =>
          `<option value="${escapeHtml(provider.name)}">${escapeHtml(
            providerLabel(provider),
          )}</option>`,
      )
      .join("");

    const saved = window.localStorage.getItem("studio.provider");
    const savedProvider = state.providers.find((item) => item.name === saved);
    const defaultProvider =
      savedProvider ||
      state.providers.find((item) => item.isDefault) ||
      state.providers[0];

    if (defaultProvider) {
      state.selectedProvider = defaultProvider.name;
      elements.providerSelect.value = defaultProvider.name;
      setProviderStatus(
        defaultProvider,
        "ready",
        defaultProvider.model || "未配置模型",
      );
    }

    elements.providerSelect.disabled = state.providers.length === 0;
  } catch (error) {
    setProviderStatus(null, "error", "无法读取 Provider");
    showToast(`模型状态读取失败：${error.message}`, "error");
  }
}

async function loadWorkspace() {
  elements.workspaceState.textContent = "读取中";

  try {
    const response = await fetch("/api/v1/workspace/status");
    const status = await response.json();
    elements.workspacePath.textContent = status.excelDataRoot || "未配置";
    elements.workspacePath.title = status.excelDataRoot || "";

    if (!response.ok || !status.configured) {
      elements.workspaceState.textContent = "不可用";
      elements.workspaceRevision.textContent = "";
      elements.skillCount.textContent = "0";
      elements.skillList.innerHTML = `<div class="skill-empty">${escapeHtml(
        status.errors?.[0] || "工作区不可用",
      )}</div>`;
      return;
    }

    elements.workspaceState.textContent = "已连接";
    elements.workspaceRevision.textContent = `rev ${status.revision.slice(0, 10)}`;
    await loadSkills();
  } catch (error) {
    elements.workspaceState.textContent = "读取失败";
    elements.skillList.innerHTML = `<div class="skill-empty">${escapeHtml(
      error.message,
    )}</div>`;
  }
}

async function loadSkills() {
  elements.skillCount.textContent = "读取中";
  const response = await fetch("/api/v1/skills?limit=500");
  if (!response.ok) {
    throw new Error(`技能列表读取失败：HTTP ${response.status}`);
  }

  state.skills = await response.json();
  elements.skillCount.textContent = String(state.skills.length);
  renderSkillList();
  renderIcons();
}

function renderSkillList() {
  const query = elements.skillSearch.value.trim().toLowerCase();
  const items = state.skills.filter((skill) => {
    if (!query) {
      return true;
    }

    return (
      String(skill.id).includes(query) ||
      skill.label.toLowerCase().includes(query) ||
      skill.summary.toLowerCase().includes(query)
    );
  });

  elements.skillCount.textContent = String(items.length);
  if (items.length === 0) {
    elements.skillList.innerHTML =
      '<div class="skill-empty">没有匹配的技能</div>';
    return;
  }

  elements.skillList.innerHTML = items
    .map(
      (skill) => `
        <button
          class="skill-item ${skill.id === state.selectedSkillId ? "is-active" : ""}"
          type="button"
          data-skill-id="${skill.id}"
          title="${escapeHtml(skill.summary)}"
        >
          <strong>${escapeHtml(String(skill.id))} · ${escapeHtml(skill.label)}</strong>
          <span>${escapeHtml(skill.summary)}</span>
        </button>
      `,
    )
    .join("");
}

function selectProvider(name) {
  const provider = state.providers.find((item) => item.name === name);
  if (!provider) {
    return;
  }

  state.selectedProvider = provider.name;
  window.localStorage.setItem("studio.provider", provider.name);
  setProviderStatus(provider, "ready", provider.model || "未配置模型");
}

function showEmptyState(visible) {
  elements.emptyState.classList.toggle("is-hidden", visible);
}

function appendMessage({ role, content, latencyMs, error = false }) {
  showEmptyState(true);

  const message = document.createElement("article");
  message.className = `message is-${role}${error ? " is-error" : ""}`;

  const roleName = role === "user" ? "你" : error ? "调用失败" : "Skill Agent";
  const avatar = role === "user" ? "你" : error ? "!" : "AI";
  const latency =
    typeof latencyMs === "number"
      ? `<span class="message-latency">${latencyMs}ms</span>`
      : "";

  message.innerHTML = `
    <div class="message-avatar">${avatar}</div>
    <div class="message-body">
      <div class="message-role">
        <span>${roleName}</span>
        ${latency}
      </div>
      <div class="message-content">${escapeHtml(content)}</div>
    </div>
  `;

  elements.messages.appendChild(message);
  elements.conversation.scrollTop = elements.conversation.scrollHeight;
  return message;
}

function appendTypingMessage() {
  showEmptyState(true);

  const message = document.createElement("article");
  message.className = "message is-assistant";
  message.dataset.typing = "true";
  message.innerHTML = `
    <div class="message-avatar">AI</div>
    <div class="message-body">
      <div class="message-role"><span>Skill Agent</span></div>
      <div class="typing"><span></span><span></span><span></span></div>
    </div>
  `;

  elements.messages.appendChild(message);
  elements.conversation.scrollTop = elements.conversation.scrollHeight;
  return message;
}

function updateInspector(text) {
  elements.inspectorMeta.textContent = text ? "已获取响应" : "等待提案";
  if (!text) {
    return;
  }

  elements.inspectorContent.innerHTML = `<pre class="inspector-text">${escapeHtml(
    text,
  )}</pre>`;
}

function renderSkillChain(chain) {
  state.selectedSkillId = chain.skillId;
  renderSkillList();

  const nodeById = new Map(chain.nodes.map((node) => [node.key, node]));
  elements.inspectorMeta.textContent = `${chain.nodes.length} 节点 · ${chain.edges.length} 关系`;
  elements.inspectorContent.innerHTML = `
    <div class="chain-header">
      <strong>${escapeHtml(chain.focusKey)}</strong>
      <span>revision ${escapeHtml(chain.revision.slice(0, 12))}</span>
    </div>
    <section class="chain-block">
      <h3>节点</h3>
      ${chain.nodes
        .map((node) => {
          const details = chainNodeDetails(node);
          return `
            <div class="chain-node">
              <div class="chain-label">
                <span class="chain-badge">${escapeHtml(node.namespace)}:${node.id}</span>
                <strong>${escapeHtml(node.label)}</strong>
              </div>
              ${details ? `<div class="chain-meta">${escapeHtml(details)}</div>` : ""}
            </div>
          `;
        })
        .join("")}
    </section>
    <section class="chain-block">
      <h3>关系</h3>
      ${chain.edges
        .map((edge) => {
          const source = nodeById.get(edge.source);
          const target = nodeById.get(edge.target);
          return `
            <div class="chain-edge">
              <div class="chain-edge-line">
                ${escapeHtml(source?.namespace || edge.source)}
                → ${escapeHtml(edge.label)}
                → ${escapeHtml(target?.namespace || edge.target)}
              </div>
              <div class="chain-meta">${escapeHtml(edge.source)} → ${escapeHtml(
                edge.target,
              )}${edge.detail ? ` · ${escapeHtml(edge.detail)}` : ""}</div>
            </div>
          `;
        })
        .join("")}
    </section>
  `;
}

function chainNodeDetails(node) {
  const fields = node.fields || {};
  const candidates = [
    ["action_type", "动作"],
    ["__action", "动作"],
    ["shape", "形状"],
    ["team", "阵营"],
    ["count", "数量"],
    ["duration", "持续"],
  ];

  return candidates
    .map(([key, label]) => {
      const value = fields[key]?.[0];
      return value ? `${label} ${value}` : null;
    })
    .filter(Boolean)
    .join(" · ");
}

async function loadSkillChain(skillId) {
  elements.inspectorMeta.textContent = "读取链路";
  elements.inspectorContent.innerHTML =
    '<div class="inspector-empty"><h3>正在读取真实链路</h3></div>';

  try {
    const response = await fetch(`/api/v1/skills/${skillId}/chain?depth=12`);
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(payload.error || payload.detail || `HTTP ${response.status}`);
    }

    renderSkillChain(payload);
  } catch (error) {
    elements.inspectorMeta.textContent = "读取失败";
    elements.inspectorContent.innerHTML = `<div class="inspector-empty"><h3>链路读取失败</h3><p>${escapeHtml(
      error.message,
    )}</p></div>`;
    showToast(error.message, "error");
  }
}

async function sendMessage(message) {
  if (state.sending || !message.trim()) {
    return;
  }

  const provider = state.selectedProvider;
  if (!provider) {
    showToast("没有可用的模型 Provider。", "error");
    return;
  }

  state.sending = true;
  elements.sendButton.disabled = true;
  elements.messageInput.value = "";
  appendMessage({ role: "user", content: message });
  const typingMessage = appendTypingMessage();

  try {
    const response = await fetch("/api/v1/llm/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        provider,
        message,
        instructions:
          "你是 RTS Skill Studio 的技能配置 Agent。当前处于只读阶段，只能解释和提出方案，不得声称已经修改 Excel。回答使用中文，结构清晰，并明确不确定项。",
      }),
    });

    const payload = await response.json().catch(() => ({}));
    typingMessage.remove();

    if (!response.ok) {
      const detail =
        payload.detail || payload.error || `模型调用失败：HTTP ${response.status}`;
      appendMessage({ role: "assistant", content: detail, error: true });
      updateInspector(detail);
      showToast(detail, "error");
      return;
    }

    appendMessage({
      role: "assistant",
      content: payload.text || "",
      latencyMs: payload.latencyMs,
    });
    updateInspector(payload.text || "");
  } catch (error) {
    typingMessage.remove();
    const detail = `模型调用失败：${error.message}`;
    appendMessage({ role: "assistant", content: detail, error: true });
    updateInspector(detail);
    showToast(detail, "error");
  } finally {
    state.sending = false;
    elements.sendButton.disabled = false;
    elements.messageInput.focus();
  }
}

elements.providerSelect.addEventListener("change", (event) => {
  selectProvider(event.target.value);
});

elements.refreshProviders.addEventListener("click", loadProviders);

elements.skillSearch.addEventListener("input", renderSkillList);

elements.skillList.addEventListener("click", (event) => {
  const item = event.target.closest("[data-skill-id]");
  if (item) {
    loadSkillChain(Number(item.dataset.skillId));
  }
});

elements.composer.addEventListener("submit", (event) => {
  event.preventDefault();
  sendMessage(elements.messageInput.value);
});

elements.messageInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    elements.composer.requestSubmit();
  }
});

document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((item) => {
      item.classList.toggle("is-active", item === tab);
    });
  });
});

Promise.all([loadProviders(), loadWorkspace()]).then(renderIcons);
renderIcons();
