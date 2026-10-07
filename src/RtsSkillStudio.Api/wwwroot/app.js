const state = {
  providers: [],
  selectedProvider: null,
  selectedModel: null,
  selectedReasoningEffort: "",
  conversations: [],
  activeConversationId: null,
  skills: [],
  selectedAsset: null,
  selectedSkillId: null,
  inspector: {
    tab: "chain",
    chain: null,
    chains: [],
    planJson: null,
    planErrors: [],
    planDisposition: "None",
    compileStatus: null,
    compileErrors: [],
    patchJson: null,
    patchValidation: null,
    patchDiff: [],
    applyResult: null,
    clarifications: [],
    unsupported: [],
    toolExecutions: [],
    lastError: null,
  },
  sending: false,
};

const paneLayout = {
  sidebar: {
    cssVariable: "--sidebar-width",
    storageKey: "studio.layout.sidebarWidth",
    defaultValue: 264,
    min: 200,
    max: 420,
  },
  inspector: {
    cssVariable: "--inspector-width",
    storageKey: "studio.layout.inspectorWidth",
    defaultValue: 520,
    min: 360,
    max: 1040,
  },
};

const elements = {
  appShell: document.querySelector(".app-shell"),
  sidebar: document.querySelector(".sidebar"),
  inspectorPane: document.querySelector(".inspector-pane"),
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
  inspectorTab: document.querySelector("#inspectorTab"),
  inspectorContent: document.querySelector("#inspectorContent"),
  assetSearch: document.querySelector("#assetSearch"),
  workspaceState: document.querySelector("#workspaceState"),
  workspacePath: document.querySelector("#workspacePath"),
  workspaceRevision: document.querySelector("#workspaceRevision"),
  toast: document.querySelector("#toast"),
  assetSearchResults: document.querySelector("#assetSearchResults"),
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

function paneWidth(paneName) {
  const config = paneLayout[paneName];
  const value = Number.parseFloat(
    getComputedStyle(document.documentElement).getPropertyValue(
      config.cssVariable,
    ),
  );
  return Number.isFinite(value) ? value : config.defaultValue;
}

function maxPaneWidth(paneName) {
  const config = paneLayout[paneName];
  const otherPaneName = paneName === "sidebar" ? "inspector" : "sidebar";
  const otherWidth = paneWidth(otherPaneName);
  const available =
    elements.appShell.getBoundingClientRect().width -
    otherWidth -
    420 -
    12;
  return Math.max(config.min, Math.min(config.max, available));
}

function setPaneWidth(paneName, width, persist = true) {
  const config = paneLayout[paneName];
  const clamped = Math.round(
    Math.max(config.min, Math.min(maxPaneWidth(paneName), width)),
  );
  document.documentElement.style.setProperty(
    config.cssVariable,
    `${clamped}px`,
  );
  if (persist) {
    window.localStorage.setItem(config.storageKey, String(clamped));
  }
  return clamped;
}

function restorePaneWidths() {
  for (const [paneName, config] of Object.entries(paneLayout)) {
    const saved = Number.parseFloat(
      window.localStorage.getItem(config.storageKey) || "",
    );
    setPaneWidth(
      paneName,
      Number.isFinite(saved) ? saved : config.defaultValue,
      false,
    );
  }
}

function setupPaneResizer(element) {
  const paneName = element.dataset.paneResizer;
  const config = paneLayout[paneName];
  if (!config) {
    return;
  }

  function applyKeyboardResize(delta) {
    const next = setPaneWidth(paneName, paneWidth(paneName) + delta);
    element.setAttribute("aria-valuenow", String(next));
  }

  element.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) {
      return;
    }

    const startX = event.clientX;
    const startWidth = paneWidth(paneName);
    element.setPointerCapture(event.pointerId);
    document.body.classList.add("is-resizing");
    event.preventDefault();

    function move(pointerEvent) {
      const rawDelta = pointerEvent.clientX - startX;
      const delta = paneName === "inspector" ? -rawDelta : rawDelta;
      const next = setPaneWidth(paneName, startWidth + delta, false);
      element.setAttribute("aria-valuenow", String(next));
    }

    function end() {
      setPaneWidth(paneName, paneWidth(paneName));
      document.body.classList.remove("is-resizing");
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
    }

    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
  });

  element.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") {
      return;
    }

    event.preventDefault();
    const delta = event.key === "ArrowLeft" ? -16 : 16;
    applyKeyboardResize(paneName === "inspector" ? -delta : delta);
  });

  const width = paneWidth(paneName);
  element.setAttribute("aria-valuemin", String(config.min));
  element.setAttribute("aria-valuemax", String(maxPaneWidth(paneName)));
  element.setAttribute("aria-valuenow", String(width));
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
      return;
    }

    elements.workspaceState.textContent = "已连接";
    elements.workspaceRevision.textContent = `rev ${status.revision.slice(0, 10)}`;
  } catch (error) {
    elements.workspaceState.textContent = "读取失败";
    showToast(error.message, "error");
  }
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

function updateInspector(text, isError = false) {
  state.inspector.lastError = isError ? text || null : null;
  if (state.inspector.tab === "evidence") {
    renderInspector();
  }
}

function chainInspectorHtml(chain) {
  const nodeById = new Map(chain.nodes.map((node) => [node.key, node]));
  const outgoing = new Map();
  for (const edge of chain.edges || []) {
    if (!outgoing.has(edge.source)) {
      outgoing.set(edge.source, []);
    }
    outgoing.get(edge.source).push(edge);
  }
  for (const edges of outgoing.values()) {
    edges.sort((left, right) => {
      const leftParam = left.parameterIndex ?? Number.MAX_SAFE_INTEGER;
      const rightParam = right.parameterIndex ?? Number.MAX_SAFE_INTEGER;
      return (
        leftParam - rightParam ||
        String(left.sourceField || "").localeCompare(
          String(right.sourceField || ""),
        ) ||
        String(left.label || "").localeCompare(String(right.label || "")) ||
        String(left.target).localeCompare(String(right.target))
      );
    });
  }

  const rendered = new Set();
  const focus =
    nodeById.get(chain.focusKey) ||
    chain.nodes.find((node) => node.isFocus) ||
    chain.nodes[0];

  function nodeIcon(node, hasChildren, isCycle, isReused) {
    if (node.isMissing) {
      return "triangle-alert";
    }
    if (isCycle) {
      return "repeat-2";
    }
    if (isReused) {
      return "copy";
    }
    return hasChildren ? "git-branch" : "circle";
  }

  function renderNode(node, edge, path, isRoot = false) {
    if (!node) {
      return "";
    }

    const children = outgoing.get(node.key) || [];
    const isCycle = path.has(node.key);
    const isReused = rendered.has(node.key) && !isRoot;
    if (!isReused) {
      rendered.add(node.key);
    }
    const renderChildren = children.length > 0 && !isCycle && !isReused;
    const nextPath = new Set(path);
    nextPath.add(node.key);
    const relation = edge?.label || (isRoot ? "根技能" : "关联");
    const relationMeta = [
      edge?.sourceField,
      edge?.parameterIndex === null ||
      edge?.parameterIndex === undefined
        ? ""
        : `param[${edge.parameterIndex}]`,
      isCycle ? "循环引用" : "",
      isReused ? "共享节点，已在上方展开" : "",
    ]
      .filter(Boolean)
      .join(" · ");
    const details = chainNodeDetails(node);
    const className = [
      "tree-node",
      isRoot ? "is-root" : "",
      renderChildren ? "is-branch" : "is-leaf",
      node.isMissing ? "is-missing" : "",
      isReused ? "is-reused" : "",
      isCycle ? "is-cycle" : "",
    ]
      .filter(Boolean)
      .join(" ");
    const toggle = renderChildren
      ? '<span class="tree-toggle" aria-hidden="true"></span>'
      : "";
    const rowTitle = renderChildren
      ? `${escapeHtml(node.key)} · Shift+点击折叠/展开整棵子树`
      : escapeHtml(node.key);
    const summary = `
      <div class="tree-summary">
        ${toggle}
        <span class="tree-icon">${icon(
          nodeIcon(node, renderChildren, isCycle, isReused),
        )}</span>
        <span class="tree-copy">
          <span class="tree-title">
            <span class="chain-badge">${escapeHtml(node.namespace)}:${escapeHtml(
              String(node.id),
            )}</span>
            <strong>${escapeHtml(node.label || node.key)}</strong>
          </span>
          <span class="tree-relation">${escapeHtml(relation)}${
            relationMeta ? ` · ${escapeHtml(relationMeta)}` : ""
          }</span>
          ${
            details
              ? `<span class="tree-detail">${escapeHtml(details)}</span>`
              : ""
          }
        </span>
      </div>
    `;

    if (!renderChildren) {
      return `<div class="${className}" title="${rowTitle}">${summary}</div>`;
    }

    return `
      <details class="${className}" open>
        <summary title="${rowTitle}">${summary}</summary>
        <div class="tree-children">
          ${children
            .map((childEdge) =>
              renderNode(
                nodeById.get(childEdge.target) ||
                  missingTreeNode(childEdge.target),
                childEdge,
                nextPath,
              ),
            )
            .join("")}
        </div>
      </details>
    `;
  }

  const incoming = chain.incomingReferences || [];
  const incomingHtml =
    incoming.length === 0
      ? ""
      : `
        <details class="tree-node tree-incoming is-branch" open>
          <summary>
            <div class="tree-summary">
              <span class="tree-toggle" aria-hidden="true"></span>
              <span class="tree-icon">${icon("link")}</span>
              <span class="tree-copy">
                <span class="tree-title">
                  <strong>上游引用</strong>
                </span>
                <span class="tree-relation">${incoming.length} 条来源引用</span>
              </span>
            </div>
          </summary>
          <div class="tree-children">
            ${incoming
              .map(
                (reference) => `
                  <div class="tree-node is-leaf" title="${escapeHtml(
                    reference.source,
                  )}">
                    <div class="tree-summary">
                      <span class="tree-icon">${icon("corner-down-right")}</span>
                      <span class="tree-copy">
                        <span class="tree-title">
                          <span class="chain-badge">${escapeHtml(
                            reference.source,
                          )}</span>
                          <strong>${escapeHtml(
                            reference.sourceLabel || reference.source,
                          )}</strong>
                        </span>
                        <span class="tree-relation">${escapeHtml(
                          reference.relationship || "引用",
                        )}${
                          reference.sourceField
                            ? ` · ${escapeHtml(reference.sourceField)}`
                            : ""
                        }${
                          reference.derived
                            ? ' · <span class="tree-derived">派生关系</span>'
                            : ""
                        }</span>
                      </span>
                    </div>
                  </div>
                `,
              )
              .join("")}
          </div>
        </details>
      `;

  return `
    <div class="chain-header">
      <strong>${escapeHtml(chain.focusKey)}</strong>
      <span>revision ${escapeHtml(chain.revision.slice(0, 12))}</span>
    </div>
    <div class="tree-legend">
      <span>${icon("git-branch")} 分支</span>
      <span>${icon("circle")} 叶节点</span>
      <span>${icon("copy")} 共享</span>
      <span class="tree-legend-hint">Shift+点击 整棵子树折叠/展开</span>
    </div>
    <div class="chain-tree">
      ${incomingHtml}
      ${renderNode(focus, null, new Set(), true)}
    </div>
  `;
}

function missingTreeNode(key) {
  const separator = key.indexOf(":");
  const namespace = separator >= 0 ? key.slice(0, separator) : key;
  const rawId = separator >= 0 ? key.slice(separator + 1) : key;
  const numericId = Number(rawId);
  return {
    key,
    namespace,
    id: Number.isFinite(numericId) ? numericId : 0,
    label: key,
    kind: "缺失",
    isMissing: true,
    isFocus: false,
    fields: {},
  };
}

function renderSkillChain(chain, activateTab = true) {
  state.selectedAsset = {
    namespace: chain.rootNamespace,
    id: chain.rootId,
  };
  state.selectedSkillId =
    chain.rootNamespace === "TbSkill" ? chain.rootId : null;
  state.inspector.chain = chain;
  state.inspector.chains = [chain];
  if (activateTab) {
    showInspectorTab("chain");
  } else if (
    state.inspector.tab === "chain" ||
    state.inspector.tab === "evidence"
  ) {
    renderInspector();
  }
}

function showInspectorTab(tabName) {
  state.inspector.tab = tabName;
  if (elements.inspectorTab) {
    elements.inspectorTab.value = tabName;
  }
  renderInspector();
}

function renderInspector() {
  const {
    chain,
    chains,
    tab,
    planJson,
    planErrors,
    planDisposition,
    compileStatus,
    compileErrors,
    patchJson,
    patchValidation,
    patchDiff,
    applyResult,
    clarifications,
    unsupported,
    toolExecutions,
    lastError,
  } = state.inspector;
  const visibleChains = chains?.length ? chains : chain ? [chain] : [];

  if (tab === "plan") {
    elements.inspectorMeta.textContent = planJson
      ? planErrors.length > 0
        ? `${planErrors.length} 个校验问题`
        : "Plan 已生成"
      : clarifications.length > 0
        ? `${clarifications.length} 个澄清问题`
        : unsupported.length > 0
          ? `${unsupported.length} 个不支持项`
          : planErrors.length > 0
            ? `${planErrors.length} 个计划问题`
            : "等待提案";
    if (
      !planJson &&
      planErrors.length === 0 &&
      clarifications.length === 0 &&
      unsupported.length === 0
    ) {
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
    const planStatusHtml =
      planJson || planErrors.length > 0
        ? `<div class="plan-validation ${
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
                : "<span>Plan 通过后可编译为 WorkbookPatch，并投影 Excel 字段差异。</span>"
            }
          </div>`
        : "";

    elements.inspectorContent.innerHTML = `
      ${planStatusHtml}
      ${
        planJson
          ? `<pre class="inspector-text">${escapeHtml(prettyPlan)}</pre>`
          : ""
      }
      ${
        clarifications.length
          ? `<div class="evidence-block">
              <h3>需要澄清</h3>
              ${clarifications
                .map(
                  (item) => `<div class="evidence-item">
                    <strong>${escapeHtml(item.question)}</strong>
                    <span>${escapeHtml(item.fieldPath)}</span>
                  </div>`,
                )
                .join("")}
            </div>`
          : ""
      }
      ${
        unsupported.length
          ? `<div class="evidence-block is-error">
              <h3>不支持项</h3>
              ${unsupported
                .map(
                  (item) => `<div class="evidence-item">
                    <strong>${escapeHtml(item.code)}</strong>
                    <span>${escapeHtml(item.message)}</span>
                    <span>${escapeHtml(item.manualPath)}</span>
                  </div>`,
                )
                .join("")}
            </div>`
          : ""
      }
    `;
    return;
  }

  if (tab === "evidence") {
    elements.inspectorMeta.textContent = visibleChains.length || planJson
      ? "结构化证据"
      : "等待证据";
    elements.inspectorContent.innerHTML = evidenceInspectorHtml(
      visibleChains,
      planJson,
      planErrors,
      planDisposition,
      clarifications,
      unsupported,
      toolExecutions,
      lastError,
    );
    renderIcons();
    return;
  }

  if (tab === "diff") {
    elements.inspectorMeta.textContent = patchJson
      ? patchValidation?.status === "Valid"
        ? `${patchDiff.length} 个字段变化`
        : compileErrors.length
          ? `${compileErrors.length} 个编译问题`
          : "等待校验"
      : compileStatus === "Compiling"
        ? "编译中"
        : compileStatus === "NoChange"
          ? "没有字段变化"
        : "等待 Patch";
    elements.inspectorContent.innerHTML = diffInspectorHtml(
      patchJson,
      compileStatus,
      compileErrors,
      patchValidation,
      patchDiff,
      applyResult,
    );
    renderIcons();
    return;
  }

  if (visibleChains.length === 0) {
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

  elements.inspectorMeta.textContent =
    visibleChains.length > 1
      ? `${visibleChains.length} 个资产根`
      : `${visibleChains[0].nodes.length} 节点 · ${visibleChains[0].edges.length} 关系`;
  elements.inspectorContent.innerHTML =
    visibleChains.length > 1
      ? visibleChains
          .map(
            (item) => `
              <section class="multi-chain">
                ${chainInspectorHtml(item)}
              </section>
            `,
          )
          .join("")
      : chainInspectorHtml(visibleChains[0]);
}

function evidenceInspectorHtml(
  chains,
  planJson,
  planErrors,
  planDisposition,
  clarifications,
  unsupported,
  toolExecutions,
  lastError,
) {
  if (
    chains.length === 0 &&
    !planJson &&
    !lastError &&
    clarifications.length === 0 &&
    unsupported.length === 0 &&
    toolExecutions.length === 0
  ) {
    return `
      <div class="inspector-empty">
        <i data-lucide="scan-search"></i>
        <h3>暂无可验证证据</h3>
        <p>选择技能或生成 Plan 后，这里会显示字段、关系和校验依据。</p>
      </div>
    `;
  }

  let plan = null;
  if (planJson) {
    try {
      plan = JSON.parse(planJson);
    } catch {
      plan = null;
    }
  }

  const operations = plan?.operations || [];
  const compileErrors = (state.inspector.compileErrors || []).filter(
    (error) => error.code !== "compiler.no_change",
  );
  const patchValidation = state.inspector.patchValidation;
  const patchDiff = state.inspector.patchDiff || [];

  return `
    ${
      unsupported.length
        ? `<section class="evidence-block is-error">
            <h3>不支持项</h3>
            ${unsupported
              .map(
                (item) => `<div class="evidence-item">
                  <strong>${escapeHtml(item.code)}</strong>
                  <span>${escapeHtml(item.message)}</span>
                  <span>${escapeHtml(item.manualPath)}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      clarifications.length
        ? `<section class="evidence-block">
            <h3>澄清问题</h3>
            ${clarifications
              .map(
                (item) => `<div class="evidence-item">
                  <strong>${escapeHtml(item.question)}</strong>
                  <span>${escapeHtml(item.fieldPath)}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      toolExecutions.length
        ? `<section class="evidence-block">
            <h3>只读工具调用</h3>
            ${toolExecutions
              .map(
                (execution) => `<div class="evidence-item">
                  <strong>${escapeHtml(execution.name)}</strong>
                  <code>${escapeHtml(execution.argumentsJson)}</code>
                  <span>${execution.isError ? "执行失败" : "执行完成"}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      lastError
        ? `<section class="evidence-block is-error">
            <h3>最近错误</h3>
            <pre class="inspector-text">${escapeHtml(lastError)}</pre>
          </section>`
        : ""
    }
    ${
      chains
        .map(
          (chain) => {
            const focus = chain.nodes?.find((node) => node.isFocus);
            const relations = (chain.edges || []).slice(0, 30);
            const incoming = (chain.incomingReferences || []).slice(0, 30);
            return `<section class="evidence-block">
              <h3>${escapeHtml(chain.rootKey)}</h3>
              <div class="evidence-row">
                <span>Revision</span>
                <code>${escapeHtml(chain.revision)}</code>
              </div>
              <div class="evidence-row">
                <span>根资产</span>
                <code>${escapeHtml(chain.focusKey)}</code>
              </div>
            </section>
            <section class="evidence-block">
              <h3>根字段证据</h3>
              ${
                focus
                  ? Object.entries(focus.fields || {})
                      .filter(([, values]) => values?.length)
                      .map(
                        ([key, values]) => `
                          <div class="evidence-row">
                            <span>${escapeHtml(key)}</span>
                            <code>${escapeHtml(values.join(", "))}</code>
                          </div>
                        `,
                      )
                      .join("")
                  : '<div class="chain-meta">没有根字段</div>'
              }
            </section>
            <section class="evidence-block">
              <h3>图关系证据</h3>
              ${
                relations
                  .map(
                    (edge) => `
                      <div class="evidence-item">
                        <strong>${escapeHtml(edge.label)}</strong>
                        <code>${escapeHtml(edge.source)} → ${escapeHtml(
                          edge.target,
                        )}</code>
                        <span>${escapeHtml(
                          [
                            edge.sourceField,
                            edge.parameterIndex === null ||
                            edge.parameterIndex === undefined
                              ? ""
                              : `param[${edge.parameterIndex}]`,
                          ]
                            .filter(Boolean)
                            .join(" · "),
                        )}</span>
                      </div>
                    `,
                  )
                  .join("")
              }
            </section>
            <section class="evidence-block">
              <h3>入向引用证据</h3>
              ${
                incoming
                  .map(
                    (reference) => `
                      <div class="evidence-item">
                        <strong>${escapeHtml(
                          reference.sourceLabel || reference.source,
                        )}</strong>
                        <code>${escapeHtml(
                          reference.source,
                        )} → ${escapeHtml(chain.focusKey)}</code>
                        <span>${escapeHtml(reference.relationship || "")}${
                          reference.sourceField
                            ? ` · ${escapeHtml(reference.sourceField)}`
                            : ""
                        }${
                          reference.derived
                            ? ` · ${escapeHtml(
                                reference.detail || "派生关系",
                              )}`
                            : ""
                        }</span>
                      </div>
                    `,
                  )
                  .join("")
              }
            </section>`;
          },
        )
        .join("")
    }
    ${
      plan
        ? `<section class="evidence-block">
            <h3>Plan 校验证据</h3>
            <div class="evidence-row">
              <span>状态</span>
              <code>${escapeHtml(planDisposition)}</code>
            </div>
            <div class="evidence-row">
              <span>校验问题</span>
              <code>${planErrors.length}</code>
            </div>
            ${
              planErrors.length
                ? `<ul>${planErrors
                    .map((error) => `<li>${escapeHtml(error)}</li>`)
                    .join("")}</ul>`
                : ""
            }
          </section>
          <section class="evidence-block">
            <h3>Plan 字段来源</h3>
            ${operations
              .map((operation) => {
                const fields = operation.fields || {};
                return Object.entries(fields)
                  .map(
                    ([field, value]) => `
                      <div class="evidence-item">
                        <strong>${escapeHtml(
                          operation.kind,
                        )} · ${escapeHtml(field)}</strong>
                        <code>${escapeHtml(
                          JSON.stringify(value.value),
                        )}</code>
                        <span>source = ${escapeHtml(value.source || "")}</span>
                      </div>
                    `,
                  )
                  .join("");
              })
              .join("")}
          </section>`
        : ""
    }
    ${
      compileErrors.length
        ? `<section class="evidence-block is-error">
            <h3>Patch 编译证据</h3>
            ${compileErrors
              .map(
                (error) => `<div class="evidence-item">
                  <strong>${escapeHtml(error.code)}</strong>
                  <span>${escapeHtml(error.message)}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      patchValidation
        ? `<section class="evidence-block">
            <h3>Patch 校验证据</h3>
            <div class="evidence-row">
              <span>状态</span>
              <code>${escapeHtml(patchValidation.status)}</code>
            </div>
            ${(patchValidation.checks || [])
              .map(
                (check) => `<div class="evidence-row">
                  <span>${escapeHtml(check.code)}</span>
                  <code>${escapeHtml(check.status)}</code>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      patchDiff.length
        ? `<section class="evidence-block">
            <h3>Excel 字段差异证据</h3>
            ${patchDiff
              .map(
                (row) => `<div class="evidence-item">
                  <strong>${escapeHtml(row.logicalAddress)}</strong>
                  <code>${escapeHtml(row.before || "空")} → ${escapeHtml(
                    row.after || "空",
                  )}</code>
                  <span>${escapeHtml(row.semanticField)} · ${escapeHtml(
                    row.source,
                  )}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
  `;
}

function diffInspectorHtml(
  patchJson,
  compileStatus,
  compileErrors,
  patchValidation,
  patchDiff,
  applyResult,
) {
  if (!patchJson && compileStatus !== "Compiling" && compileErrors.length === 0) {
    return `
      <div class="inspector-empty">
        <i data-lucide="table-2"></i>
        <h3>尚无 WorkbookPatch</h3>
        <p>生成可编译的 SkillConfigPlan 后，这里会显示 Excel 字段级差异。</p>
      </div>
    `;
  }

  if (compileStatus === "Compiling") {
    return `
      <div class="inspector-empty">
        <i data-lucide="loader-circle"></i>
        <h3>正在编译 WorkbookPatch</h3>
        <p>编译器只读取当前工作区快照，不会修改源工作簿。</p>
      </div>
    `;
  }

  const valid = patchValidation?.status === "Valid";
  const checks = patchValidation?.checks || [];
  const noChange = compileStatus === "NoChange";
  const errors = (compileErrors || []).filter(
    (error) => error.code !== "compiler.no_change",
  );
  const rows = patchDiff || [];
  const failedChecks = checks.filter(
    (check) => check.status === "Failed" || check.status === "NotRun",
  );
  return `
    <div class="plan-validation ${valid ? "is-valid" : "is-error"}">
      <strong>${
        valid
          ? "WorkbookPatch 校验通过"
          : noChange
            ? "没有字段变化"
            : "WorkbookPatch 已阻止"
      }</strong>
      <span>源工作簿不会被修改；本阶段只允许写入临时工作区副本。</span>
    </div>
    ${
      errors.length
        ? `<section class="evidence-block is-error">
            <h3>编译器问题</h3>
            ${errors
              .map(
                (error) => `<div class="evidence-item">
                  <strong>${escapeHtml(error.code)}</strong>
                  <span>${escapeHtml(error.message)}</span>
                  ${
                    error.subject
                      ? `<code>${escapeHtml(error.subject)}</code>`
                      : ""
                  }
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      checks.length
        ? `<section class="evidence-block">
            <h3>校验检查</h3>
            ${checks
              .map(
                (check) => `<div class="evidence-row">
                  <span>${escapeHtml(check.code)}</span>
                  <code>${escapeHtml(check.status)}</code>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      patchJson
        ? `<section class="evidence-block">
            <div class="patch-change-header">
              <h3>Excel 字段变化</h3>
              <button
                class="quiet-button"
                type="button"
                data-apply-temporary
                ${valid ? "" : "disabled"}
              >
                ${icon("play")}
                <span>验证到临时副本</span>
              </button>
            </div>
            <div class="patch-table-wrap">
              <table class="patch-table">
                <thead>
                  <tr>
                    <th>逻辑地址</th>
                    <th>字段</th>
                    <th>before</th>
                    <th>after</th>
                    <th>来源</th>
                  </tr>
                </thead>
                <tbody>
                  ${rows
                    .map(
                      (row) => `<tr class="${row.isNoOp ? "is-noop" : ""}">
                        <td><code>${escapeHtml(row.logicalAddress)}</code></td>
                        <td>
                          <strong>${escapeHtml(row.semanticField)}</strong>
                          <span>${escapeHtml(row.field)}</span>
                        </td>
                        <td><code>${escapeHtml(row.before || "空")}</code></td>
                        <td><code>${escapeHtml(row.after || "空")}</code></td>
                        <td><span>${escapeHtml(row.source)}</span></td>
                      </tr>`,
                    )
                    .join("")}
                </tbody>
              </table>
            </div>
          </section>`
        : ""
    }
    ${
      failedChecks.length
        ? `<section class="evidence-block is-error">
            <h3>阻止原因</h3>
            ${failedChecks
              .map(
                (check) => `<div class="evidence-item">
                  <strong>${escapeHtml(check.code)}</strong>
                  <span>${escapeHtml(check.message)}</span>
                </div>`,
              )
              .join("")}
          </section>`
        : ""
    }
    ${
      applyResult
        ? `<section class="evidence-block ${
            applyResult.status === "Verified" ? "" : "is-error"
          }">
            <h3>临时副本验证</h3>
            <div class="evidence-row">
              <span>状态</span>
              <code>${escapeHtml(applyResult.status)}</code>
            </div>
            ${
              applyResult.outputRoot
                ? `<div class="evidence-row">
                    <span>输出目录</span>
                    <code>${escapeHtml(applyResult.outputRoot)}</code>
                  </div>`
                : ""
            }
            <div class="evidence-row">
              <span>源哈希未变</span>
              <code>${applyResult.sourceUnchanged ? "是" : "否"}</code>
            </div>
            <div class="evidence-row">
              <span>重读字段</span>
              <code>${escapeHtml(applyResult.verifiedFieldCount ?? 0)}</code>
            </div>
            <div class="evidence-item">
              <span>${escapeHtml(applyResult.message || "")}</span>
            </div>
          </section>`
        : ""
    }
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

async function loadSkillChain(skillId, activateTab = true) {
  return loadAssetChain("TbSkill", skillId, activateTab);
}

async function loadAssetChain(
  assetNamespace,
  assetId,
  activateTab = true,
) {
  if (activateTab || state.inspector.tab === "chain") {
    elements.inspectorMeta.textContent = "读取链路";
    elements.inspectorContent.innerHTML =
      '<div class="inspector-empty"><h3>正在读取真实链路</h3></div>';
  }

  try {
    const response = await fetch(
      `/api/v1/assets/chain?namespace=${encodeURIComponent(
        assetNamespace,
      )}&id=${assetId}&depth=32&view=execution`,
    );
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(payload.error || payload.detail || `HTTP ${response.status}`);
    }

    renderSkillChain(payload, activateTab);
    await persistAssetSelection(assetNamespace, assetId);
  } catch (error) {
    elements.inspectorMeta.textContent = "读取失败";
    elements.inspectorContent.innerHTML = `<div class="inspector-empty"><h3>链路读取失败</h3><p>${escapeHtml(
      error.message,
    )}</p></div>`;
    showToast(error.message, "error");
  }
}

async function loadAssetChains(assets, activateTab = true) {
  if (!assets?.length) {
    return;
  }

  if (activateTab || state.inspector.tab === "chain") {
    elements.inspectorMeta.textContent = "读取多资产链路";
    elements.inspectorContent.innerHTML =
      '<div class="inspector-empty"><h3>正在读取候选资产链路</h3></div>';
  }

  const chains = [];
  for (const asset of assets) {
    const response = await fetch(
      `/api/v1/assets/chain?namespace=${encodeURIComponent(
        asset.namespace,
      )}&id=${asset.id}&depth=32&view=execution`,
    );
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(
        payload.error || payload.detail || `HTTP ${response.status}`,
      );
    }
    chains.push(payload);
  }

  state.inspector.chains = chains;
  state.inspector.chain = chains[0] || null;
  if (activateTab) {
    showInspectorTab("chain");
  } else if (
    state.inspector.tab === "chain" ||
    state.inspector.tab === "evidence"
  ) {
    renderInspector();
  }
}

async function persistAssetSelection(assetNamespace, assetId) {
  if (!state.activeConversationId) {
    return;
  }

  await fetch(
    `/api/v1/conversations/${state.activeConversationId}/asset`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        assetNamespace,
        assetId,
      }),
    },
  );
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
        <div class="conversation-item-row">
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
          <button
            class="conversation-delete"
            type="button"
            data-delete-conversation-id="${conversation.id}"
            title="删除会话"
            aria-label="删除会话"
          >×</button>
        </div>
      `,
    )
    .join("");

  const active = elements.conversationList.querySelector(".is-active");
  if (active) {
    active.scrollIntoView({ block: "nearest" });
  }
}

function renderAssetSearchResults(results) {
  if (!results.length) {
    elements.assetSearchResults.innerHTML =
      '<div class="asset-search-empty">没有匹配资产</div>';
    return;
  }

  elements.assetSearchResults.innerHTML = results
    .map(
      (result) => `
        <button
          class="asset-search-result"
          type="button"
          data-asset-namespace="${escapeHtml(result.ref.namespace)}"
          data-asset-id="${result.ref.id}"
          title="${escapeHtml(result.summary)}"
        >
          <strong>${escapeHtml(result.ref.namespace)}:${result.ref.id}</strong>
          <span>${escapeHtml(result.label)} · ${escapeHtml(result.kind)}</span>
        </button>
      `,
    )
    .join("");
}

async function searchAssets(query) {
  const response = await fetch(
    `/api/v1/assets/search?query=${encodeURIComponent(query)}&limit=20`,
  );
  if (!response.ok) {
    throw new Error(`资产检索失败：HTTP ${response.status}`);
  }

  renderAssetSearchResults(await response.json());
}

function activateConversation(conversation) {
  state.activeConversationId = conversation.id;
  state.selectedSkillId = conversation.selectedSkillId ?? null;
  state.selectedAsset =
    conversation.selectedAsset ||
    (conversation.selectedSkillId
      ? { namespace: "TbSkill", id: conversation.selectedSkillId }
      : null);
  window.localStorage.setItem("studio.conversation", conversation.id);
  state.inspector.planJson = conversation.planJson || null;
  state.inspector.planErrors = conversation.planErrors || [];
  state.inspector.planDisposition =
    conversation.planDisposition ||
    (conversation.planJson ? "Expected" : "None");
  state.inspector.compileStatus = null;
  state.inspector.compileErrors = [];
  state.inspector.patchJson = null;
  state.inspector.patchValidation = null;
  state.inspector.patchDiff = [];
  state.inspector.applyResult = null;
  state.inspector.clarifications = [];
  state.inspector.unsupported = [];
  state.inspector.toolExecutions = [];
  state.inspector.chain = null;
  state.inspector.chains = [];
  state.inspector.lastError = null;
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
  if (conversation.selectedAsset) {
    await loadAssetChain(
      conversation.selectedAsset.namespace,
      conversation.selectedAsset.id,
      false,
    );
  } else if (
    Array.isArray(conversation.mentionedAssets) &&
    conversation.mentionedAssets.length > 1
  ) {
    await loadAssetChains(conversation.mentionedAssets, false);
  } else if (conversation.selectedSkillId) {
    await loadSkillChain(conversation.selectedSkillId, false);
  }
  if (conversation.planJson) {
    const compiledPatch = await compilePlan(conversation.planJson);
    showInspectorTab(compiledPatch ? "diff" : "plan");
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

async function compilePlan(planJson) {
  if (!planJson) {
    return false;
  }

  state.inspector.compileStatus = "Compiling";
  state.inspector.compileErrors = [];
  state.inspector.patchJson = null;
  state.inspector.patchValidation = null;
  state.inspector.patchDiff = [];
  state.inspector.applyResult = null;
  renderInspector();
  try {
    const response = await fetch("/api/v1/workbook-patches/compile", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ planJson }),
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(
        payload.detail || payload.error || `编译失败：HTTP ${response.status}`,
      );
    }

    state.inspector.compileStatus = payload.status || "Invalid";
    state.inspector.compileErrors = payload.errors || [];
    state.inspector.patchJson = payload.patchJson || null;
    state.inspector.patchValidation = payload.validation || null;
    state.inspector.patchDiff = payload.diff || [];
    renderInspector();
    return Boolean(state.inspector.patchJson);
  } catch (error) {
    state.inspector.compileStatus = "Invalid";
    state.inspector.compileErrors = [
      {
        code: "studio.compile_failed",
        message: error.message,
      },
    ];
    renderInspector();
    return false;
  }
}

async function applyTemporaryPatch() {
  const patchJson = state.inspector.patchJson;
  if (!patchJson || state.inspector.patchValidation?.status !== "Valid") {
    return;
  }

  state.inspector.applyResult = {
    status: "Applying",
    sourceUnchanged: true,
    verifiedFieldCount: 0,
    message: "正在复制工作区并应用 Patch。",
  };
  renderInspector();
  try {
    const response = await fetch(
      "/api/v1/workbook-patches/apply-temporary",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ patchJson }),
      },
    );
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(
        payload.detail || payload.error || `临时应用失败：HTTP ${response.status}`,
      );
    }

    state.inspector.patchValidation =
      payload.validation || state.inspector.patchValidation;
    state.inspector.applyResult = payload.applyResult || {
      status: payload.status || "Failed",
      sourceUnchanged: true,
      verifiedFieldCount: 0,
      message: "服务未返回临时应用详情。",
    };
    renderInspector();
  } catch (error) {
    state.inspector.applyResult = {
      status: "Failed",
      sourceUnchanged: true,
      verifiedFieldCount: 0,
      message: error.message,
    };
    renderInspector();
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
          assetNamespace: state.selectedAsset?.namespace || null,
          assetId: state.selectedAsset?.id || null,
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
      updateInspector(detail, true);
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
    state.inspector.clarifications = payload.clarifications || [];
    state.inspector.unsupported = payload.unsupported || [];
    state.inspector.toolExecutions = payload.toolExecutions || [];
    let compiledPatch = false;
    if (
      payload.planJson &&
      payload.turnStatus === "Ready" &&
      payload.planDisposition === "Expected"
    ) {
      compiledPatch = await compilePlan(payload.planJson);
    }
    if (
      !payload.selectedAsset &&
      Array.isArray(payload.mentionedAssets) &&
      payload.mentionedAssets.length > 1
    ) {
      state.selectedAsset = null;
      state.selectedSkillId = null;
      loadAssetChains(payload.mentionedAssets, true).catch(() => {});
    } else if (payload.selectedAsset || payload.selectedSkillId) {
      state.selectedAsset = payload.selectedAsset || {
        namespace: "TbSkill",
        id: payload.selectedSkillId,
      };
      state.selectedSkillId =
        state.selectedAsset.namespace === "TbSkill"
          ? state.selectedAsset.id
          : null;
      loadAssetChain(
        state.selectedAsset.namespace,
        state.selectedAsset.id,
        false,
      ).catch(() => {});
    }
    updateInspector(null);
    showInspectorTab(
      compiledPatch
        ? "diff"
        : payload.expectedPlan ||
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
    updateInspector(detail, true);
    showToast(detail, "error");
  } finally {
    state.sending = false;
    elements.sendButton.disabled = false;
    elements.messageInput.focus();
  }
}

function setTreeSubtreeOpen(details, open) {
  details.open = open;
  details.querySelectorAll("details").forEach((item) => {
    item.open = open;
  });
}

elements.inspectorContent.addEventListener("click", (event) => {
  const applyButton = event.target.closest("[data-apply-temporary]");
  if (applyButton) {
    event.preventDefault();
    applyTemporaryPatch().catch((error) => showToast(error.message, "error"));
    return;
  }

  if (!event.shiftKey) {
    return;
  }
  const summary = event.target.closest("summary");
  if (!summary) {
    return;
  }
  const details = summary.parentElement;
  if (!details || details.tagName !== "DETAILS") {
    return;
  }
  if (!details.closest(".chain-tree")) {
    return;
  }
  event.preventDefault();
  setTreeSubtreeOpen(details, !details.open);
});

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
  const deleteButton = event.target.closest(
    "[data-delete-conversation-id]",
  );
  if (deleteButton) {
    event.stopPropagation();
    deleteConversation(deleteButton.dataset.deleteConversationId).catch(
      (error) => showToast(error.message, "error"),
    );
    return;
  }

  const item = event.target.closest("[data-conversation-id]");
  if (item && item.dataset.conversationId !== state.activeConversationId) {
    openConversation(item.dataset.conversationId).catch((error) =>
      showToast(error.message, "error"),
    );
  }
});

async function deleteConversation(conversationId) {
  const target = state.conversations.find(
    (conversation) => conversation.id === conversationId,
  );
  const confirmed = window.confirm(
    `删除会话“${target?.title || conversationId}”？`,
  );
  if (!confirmed) {
    return;
  }

  const response = await fetch(
    `/api/v1/conversations/${conversationId}`,
    { method: "DELETE" },
  );
  if (!response.ok && response.status !== 404) {
    throw new Error(`删除会话失败：HTTP ${response.status}`);
  }

  const wasActive = state.activeConversationId === conversationId;
  state.conversations = state.conversations.filter(
    (conversation) => conversation.id !== conversationId,
  );
  if (wasActive) {
    state.activeConversationId = null;
  }

  if (state.conversations.length === 0) {
    await createConversation();
  } else if (wasActive) {
    await openConversation(state.conversations[0].id);
  } else {
    renderConversationList();
  }
}

elements.assetSearch.addEventListener("input", (event) => {
  if (!event.target.value.trim()) {
    elements.assetSearchResults.innerHTML = "";
  }
});

elements.assetSearch.addEventListener("keydown", (event) => {
  if (event.key !== "Enter") {
    return;
  }

  const value = event.target.value.trim();
  if (!value) {
    return;
  }

  const match = value
    .match(
      /^(Tb[A-Za-z]+|EffectGroup|ConditionGroup|Skill|Item|Effect|Buff|Bullet|Search|Trap|Equipment)\s*[:：]\s*(\d+)$/i,
    );
  if (match) {
    event.preventDefault();
    loadAssetChain(match[1], Number(match[2]), true).catch((error) =>
      showToast(error.message, "error"),
    );
    elements.assetSearchResults.innerHTML = "";
    return;
  }

  event.preventDefault();
  searchAssets(value).catch((error) =>
    showToast(error.message, "error"),
  );
});

elements.assetSearchResults.addEventListener("click", (event) => {
  const item = event.target.closest("[data-asset-namespace]");
  if (!item) {
    return;
  }

  loadAssetChain(
    item.dataset.assetNamespace,
    Number(item.dataset.assetId),
    true,
  )
    .then(() => {
      elements.assetSearchResults.innerHTML = "";
    })
    .catch((error) => showToast(error.message, "error"));
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

elements.inspectorTab.addEventListener("change", (event) => {
  showInspectorTab(event.target.value);
});

Promise.all([loadProviders(), loadWorkspace()])
  .then(loadConversations)
  .then(renderIcons);
restorePaneWidths();
document.querySelectorAll("[data-pane-resizer]").forEach(setupPaneResizer);
window.addEventListener("resize", () => {
  for (const paneName of Object.keys(paneLayout)) {
    setPaneWidth(paneName, paneWidth(paneName), false);
  }
});
renderIcons();
