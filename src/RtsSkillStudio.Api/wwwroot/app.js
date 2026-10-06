const state = {
  providers: [],
  selectedProvider: null,
  selectedModel: null,
  selectedReasoningEffort: "",
  conversations: [],
  activeConversationId: null,
  skills: [],
  selectedSkillId: null,
  inspector: {
    tab: "chain",
    chain: null,
    planJson: null,
    planErrors: [],
    planDisposition: "None",
    evidence: null,
  },
  sending: false,
};

const elements = {
  providerSelect: document.querySelector("#providerSelect"),
  modelSelect: document.querySelector("#modelSelect"),
  reasoningSelect: document.querySelector("#reasoningSelect"),
  providerStatus: document.querySelector("#providerStatus"),
  refreshProviders: document.querySelector("#refreshProviders"),
  newConversation: document.querySelector("#newConversation"),
  conversationList: document.querySelector("#conversationList"),
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
      await loadModels(defaultProvider.name);
    }

    elements.providerSelect.disabled = state.providers.length === 0;
  } catch (error) {
    setProviderStatus(null, "error", "无法读取 Provider");
    showToast(`模型状态读取失败：${error.message}`, "error");
  }
}

async function loadModels(providerName) {
  elements.modelSelect.disabled = true;
  elements.modelSelect.innerHTML = "<option>正在读取...</option>";
  const provider = state.providers.find((item) => item.name === providerName);

  try {
    const response = await fetch(
      `/api/v1/llm/providers/${encodeURIComponent(providerName)}/models`,
    );
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const models = await response.json();
    const saved = window.localStorage.getItem(`studio.model.${providerName}`);
    const defaultModel = provider?.model || "";
    const selected =
      models.find((model) => model.name === saved) ||
      models.find((model) => model.name === defaultModel) ||
      models[0];

    elements.modelSelect.innerHTML = [
      defaultModel
        ? `<option value="">Provider 默认（${escapeHtml(defaultModel)}）</option>`
        : '<option value="">Provider 默认</option>',
      ...models.map(
        (model) =>
          `<option value="${escapeHtml(model.name)}">${escapeHtml(
            model.displayName || model.name,
          )}</option>`,
      ),
    ].join("");
    state.selectedModel = selected?.name || "";
    elements.modelSelect.value = state.selectedModel;
    elements.modelSelect.disabled = false;
  } catch {
    elements.modelSelect.innerHTML = '<option value="">Provider 默认</option>';
    state.selectedModel = "";
    elements.modelSelect.disabled = false;
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
  loadModels(provider.name);
}

function showEmptyState(visible) {
  elements.emptyState.classList.toggle("is-hidden", !visible);
}

function stripPlanBlock(content) {
  return String(content)
    .replace(/```json\s*\{[\s\S]*?\}\s*```/i, "")
    .trim();
}

function appendMessage({ role, content, latencyMs, error = false }) {
  showEmptyState(false);

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
      <div class="message-content">${escapeHtml(
        role === "assistant" ? stripPlanBlock(content) : content,
      )}</div>
    </div>
  `;

  elements.messages.appendChild(message);
  elements.conversation.scrollTop = elements.conversation.scrollHeight;
  return message;
}

function appendTypingMessage() {
  showEmptyState(false);

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
  state.inspector.evidence = text || null;
  if (state.inspector.tab === "evidence") {
    renderInspector();
  }
}

function chainInspectorHtml(chain) {
  const nodeById = new Map(chain.nodes.map((node) => [node.key, node]));
  return `
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
    <section class="chain-block">
      <h3>入向引用</h3>
      ${
        (chain.incomingReferences || []).length > 0
          ? (chain.incomingReferences || [])
              .map(
                (reference) => `
                  <div class="chain-edge">
                    <div class="chain-edge-line">
                      ${escapeHtml(reference.sourceLabel || reference.source)}
                      → ${escapeHtml(reference.relationship)}
                      → ${escapeHtml(chain.focusKey)}
                    </div>
                    <div class="chain-meta">${escapeHtml(
                      reference.source,
                    )}${
                      reference.sourceField
                        ? ` · ${escapeHtml(reference.sourceField)}`
                        : ""
                    }</div>
                  </div>
                `,
              )
              .join("")
          : '<div class="chain-meta">没有入向引用</div>'
      }
      ${
        chain.incomingReferencesTruncated
          ? '<div class="chain-meta">入向引用已截断显示</div>'
          : ""
      }
    </section>
  `;
}

function renderSkillChain(chain, activateTab = true) {
  state.selectedSkillId = chain.skillId;
  state.inspector.chain = chain;
  renderSkillList();
  if (activateTab) {
    showInspectorTab("chain");
  } else if (state.inspector.tab === "chain") {
    renderInspector();
  }
}

function showInspectorTab(tabName) {
  state.inspector.tab = tabName;
  document.querySelectorAll(".tab").forEach((item) => {
    item.classList.toggle("is-active", item.dataset.tab === tabName);
  });
  renderInspector();
}

function renderInspector() {
  const {
    chain,
    tab,
    planJson,
    planErrors,
    planDisposition,
    evidence,
  } = state.inspector;

  if (tab === "plan") {
    elements.inspectorMeta.textContent = planJson
      ? planErrors.length > 0
        ? `${planErrors.length} 个校验问题`
        : "Plan 已生成"
      : planErrors.length > 0
        ? `${planErrors.length} 个计划问题`
        : "等待提案";
    if (!planJson && planErrors.length === 0) {
      elements.inspectorContent.innerHTML = `
        <div class="inspector-empty">
          <i data-lucide="file-json-2"></i>
          <h3>尚无 SkillConfigPlan</h3>
          <p>提出配置修改后，结构化计划会显示在这里。</p>
        </div>
      `;
      renderIcons();
      return;
    }

    let prettyPlan = planJson;
    try {
      prettyPlan = JSON.stringify(JSON.parse(planJson), null, 2);
    } catch {
      prettyPlan = planJson;
    }

    elements.inspectorContent.innerHTML = `
      <div class="plan-validation ${
        planErrors.length > 0 ? "is-error" : "is-valid"
      }">
        <strong>${
          planDisposition === "UnexpectedProposal"
            ? "模型主动提案（未保存）"
            : planDisposition === "Missing"
              ? "配置请求未生成 Plan"
              : planErrors.length > 0
                ? "Schema 校验未通过"
                : "Schema 校验通过"
        }</strong>
        ${
          planErrors.length > 0
            ? `<ul>${planErrors.map((error) => `<li>${escapeHtml(error)}</li>`).join("")}</ul>`
            : "<span>当前阶段只校验结构，不编译或执行。</span>"
        }
      </div>
      ${
        planJson
          ? `<pre class="inspector-text">${escapeHtml(prettyPlan)}</pre>`
          : ""
      }
    `;
    return;
  }

  if (tab === "evidence") {
    elements.inspectorMeta.textContent = evidence ? "已获取响应" : "等待响应";
    elements.inspectorContent.innerHTML = evidence
      ? `<pre class="inspector-text">${escapeHtml(evidence)}</pre>`
      : `
        <div class="inspector-empty">
          <i data-lucide="scan-search"></i>
          <h3>暂无证据内容</h3>
          <p>Agent 回复和校验信息会显示在这里。</p>
        </div>
      `;
    renderIcons();
    return;
  }

  if (!chain) {
    elements.inspectorMeta.textContent = "等待技能";
    elements.inspectorContent.innerHTML = `
      <div class="inspector-empty">
        <i data-lucide="scan-search"></i>
        <h3>没有可检查内容</h3>
        <p>从左侧选择技能后，这里会显示真实执行链。</p>
      </div>
    `;
    renderIcons();
    return;
  }

  elements.inspectorMeta.textContent = `${chain.nodes.length} 节点 · ${chain.edges.length} 关系`;
  elements.inspectorContent.innerHTML = chainInspectorHtml(chain);
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

async function loadSkillChain(skillId, activateTab = true) {
  if (activateTab || state.inspector.tab === "chain") {
    elements.inspectorMeta.textContent = "读取链路";
    elements.inspectorContent.innerHTML =
      '<div class="inspector-empty"><h3>正在读取真实链路</h3></div>';
  }

  try {
    const response = await fetch(`/api/v1/skills/${skillId}/chain?depth=12`);
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(payload.error || payload.detail || `HTTP ${response.status}`);
    }

    renderSkillChain(payload, activateTab);
    if (state.activeConversationId) {
      fetch(
        `/api/v1/conversations/${state.activeConversationId}/skill`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ skillId }),
        },
      ).catch(() => {});
    }
  } catch (error) {
    elements.inspectorMeta.textContent = "读取失败";
    elements.inspectorContent.innerHTML = `<div class="inspector-empty"><h3>链路读取失败</h3><p>${escapeHtml(
      error.message,
    )}</p></div>`;
    showToast(error.message, "error");
  }
}

function renderConversationList() {
  if (state.conversations.length === 0) {
    elements.conversationList.innerHTML =
      '<div class="skill-empty">没有会话</div>';
    return;
  }

  elements.conversationList.innerHTML = state.conversations
    .map(
      (conversation) => `
        <button
          class="conversation-item ${
            conversation.id === state.activeConversationId ? "is-active" : ""
          }"
          type="button"
          data-conversation-id="${conversation.id}"
          title="${escapeHtml(conversation.title)}"
        >
          <strong>${escapeHtml(conversation.title)}</strong>
          <span>${conversation.messageCount || 0} 条消息</span>
        </button>
      `,
    )
    .join("");

  const active = elements.conversationList.querySelector(".is-active");
  if (active) {
    active.scrollIntoView({ block: "nearest" });
  }
}

function activateConversation(conversation) {
  state.activeConversationId = conversation.id;
  state.selectedSkillId = conversation.selectedSkillId ?? null;
  state.history = conversation.messages
    .filter((message) => message.role === "user" || message.role === "assistant")
    .map((message) => ({
      role: message.role,
      content: message.content,
    }));
  window.localStorage.setItem("studio.conversation", conversation.id);
  state.inspector.planJson = conversation.planJson || null;
  state.inspector.planErrors = conversation.planErrors || [];
  state.inspector.planDisposition =
    conversation.planDisposition ||
    (conversation.planJson ? "Expected" : "None");
  const latestAssistant = [...conversation.messages]
    .reverse()
    .find((message) => message.role === "assistant");
  state.inspector.evidence = latestAssistant?.content || null;
  renderConversationList();
  renderConversationMessages(conversation.messages);
  showInspectorTab(conversation.planJson ? "plan" : "chain");
}

function renderConversationMessages(messages) {
  elements.messages.innerHTML = "";
  if (messages.length === 0) {
    showEmptyState(true);
    elements.inspectorMeta.textContent = "等待提案";
    elements.inspectorContent.innerHTML = `
      <div class="inspector-empty">
        <i data-lucide="scan-search"></i>
        <h3>没有可检查内容</h3>
        <p>生成技能方案后，这里将显示链路、计划和校验证据。</p>
      </div>
    `;
    renderIcons();
    return;
  }

  messages.forEach((message) => {
    appendMessage({
      role: message.role === "assistant" ? "assistant" : "user",
      content: message.content,
      latencyMs: message.latencyMs,
    });
  });
  renderIcons();
}

async function loadConversations() {
  try {
    const response = await fetch("/api/v1/conversations");
    if (!response.ok) {
      throw new Error(`会话列表读取失败：HTTP ${response.status}`);
    }

    state.conversations = await response.json();
    if (state.conversations.length === 0) {
      await createConversation();
      return;
    }

    const saved = window.localStorage.getItem("studio.conversation");
    const target =
      state.conversations.find((conversation) => conversation.id === saved) ||
      state.conversations[0];
    await openConversation(target.id);
  } catch (error) {
    elements.conversationList.innerHTML = `<div class="skill-empty">${escapeHtml(
      error.message,
    )}</div>`;
    showToast(error.message, "error");
  }
}

async function createConversation() {
  const response = await fetch("/api/v1/conversations", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({}),
  });
  if (!response.ok) {
    throw new Error(`新建会话失败：HTTP ${response.status}`);
  }

  const conversation = await response.json();
  state.conversations = [conversation, ...state.conversations];
  activateConversation(conversation);
}

async function openConversation(conversationId) {
  const response = await fetch(`/api/v1/conversations/${conversationId}`);
  if (!response.ok) {
    throw new Error(`会话读取失败：HTTP ${response.status}`);
  }

  const conversation = await response.json();
  activateConversation(conversation);
  if (conversation.selectedSkillId) {
    await loadSkillChain(conversation.selectedSkillId, false);
  }
  if (conversation.planJson) {
    showInspectorTab("plan");
  }
}

async function refreshConversationList() {
  const response = await fetch("/api/v1/conversations");
  if (!response.ok) {
    return;
  }

  state.conversations = await response.json();
  renderConversationList();
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

  if (!state.activeConversationId) {
    try {
      await createConversation();
    } catch (error) {
      showToast(error.message, "error");
      return;
    }
  }

  state.sending = true;
  elements.sendButton.disabled = true;
  elements.messageInput.value = "";
  appendMessage({ role: "user", content: message });
  const typingMessage = appendTypingMessage();

  try {
    const response = await fetch(
      `/api/v1/conversations/${state.activeConversationId}/chat`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          provider,
          message,
          skillId: state.selectedSkillId,
          model: state.selectedModel || null,
          reasoningEffort: state.selectedReasoningEffort || null,
        }),
      },
    );

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

    const assistantText = payload.text || "";
    appendMessage({
      role: "assistant",
      content: assistantText,
      latencyMs: payload.latencyMs,
    });
    state.inspector.planJson = payload.planJson || null;
    state.inspector.planErrors = payload.planErrors || [];
    state.inspector.planDisposition = payload.planDisposition || "None";
    state.inspector.evidence = assistantText;
    if (
      payload.selectedSkillId &&
      payload.selectedSkillId !== state.selectedSkillId
    ) {
      state.selectedSkillId = payload.selectedSkillId;
      renderSkillList();
      loadSkillChain(payload.selectedSkillId, false).catch(() => {});
    }
    updateInspector(assistantText);
    showInspectorTab(
      payload.expectedPlan ||
        payload.planJson ||
        state.inspector.planErrors.length > 0
        ? "plan"
        : "evidence",
    );
    await refreshConversationList();
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

elements.modelSelect.addEventListener("change", (event) => {
  state.selectedModel = event.target.value;
  if (state.selectedProvider) {
    window.localStorage.setItem(
      `studio.model.${state.selectedProvider}`,
      state.selectedModel,
    );
  }
});

elements.reasoningSelect.addEventListener("change", (event) => {
  state.selectedReasoningEffort = event.target.value;
});

elements.refreshProviders.addEventListener("click", loadProviders);

elements.newConversation.addEventListener("click", async () => {
  try {
    await createConversation();
  } catch (error) {
    showToast(error.message, "error");
  }
});

elements.conversationList.addEventListener("click", (event) => {
  const item = event.target.closest("[data-conversation-id]");
  if (item && item.dataset.conversationId !== state.activeConversationId) {
    openConversation(item.dataset.conversationId).catch((error) =>
      showToast(error.message, "error"),
    );
  }
});

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
    showInspectorTab(tab.dataset.tab);
  });
});

Promise.all([loadProviders(), loadWorkspace()])
  .then(loadConversations)
  .then(renderIcons);
renderIcons();
