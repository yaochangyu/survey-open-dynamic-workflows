# ODW (Open Dynamic Workflows) 配置紀錄與模型分級實驗報告

本文件記錄本次環境配置、跨訂閱 CLI 協作流程，以及針對 `open-dynamic-workflows`（ODW）動態模型分級（依照任務難度配置語言模型）的實驗驗證成果。

---

## 一、本次環境配置紀錄

### 1. 專案與 CLI 安裝路徑
- **ODW Monorepo**: `/home/yao/projects/open-dynamic-workflows` (Node.js v22.20.0)
- **ODW 設定目錄**: `~/.odw/config.json`，認證 Token 存於 `~/.odw/daemon.token`
- **ODW 背景服務 (Daemon)**:
  - 啟動指令：`odw-daemon start`
  - 預設監聽位址：`http://127.0.0.1:7345`
  - 狀態驗證：`node .agents/skills/odw/scripts/daemon-bridge.js --check`
- **Antigravity 技能整合**:
  - 專案級 Skills：`.agents/skills/odw/` 與 `.agents/skills/ultracode/`
  - 全域 Plugins/Skills：`~/.gemini/config/plugins/odw` 與 `~/.gemini/config/skills/odw`

### 2. 本機訂閱方案 CLI 整合與實測模式
本機已配置並實測可用的三大訂閱方案 CLI（無需額外自付 API Token 費用）：
1. **調查 Agent (Claude Code CLI)**:
   - 指令：`claude -p "<prompt>" --dangerously-skip-permissions`
   - 特色：深入分析規格、長篇技術調查。
2. **實作 Agent (GitHub Copilot CLI)**:
   - 指令：`copilot --model gpt-5.5 -p "<prompt>" --allow-all`
   - 特色：讀取規格書，建立方案、實作程式碼、除錯編譯並跑單元測試。
3. **審查 Agent (Antigravity Agent)**:
   - 指令：由本機 IDE 代理人或 `agy` 執行。
   - 特色：雙軸審查（規範對齊 + 程式碼安全性與邊界條件審查）。

---

## 二、ODW 依照任務難度動態配置模型實驗

### 1. 實驗目標
驗證 ODW 是否能像 **Claude Dynamic Workflows / Ultracode** 一樣，在同一套編排腳本內針對不同階段與難度的 Agent，動態指派不同等級（Model Tier）的語言模型。

### 2. 原始碼實作機制分析

經檢驗 `open-dynamic-workflows` 原始碼發現：
1. **核心階層解析 (`packages/daemon/src/runtime.js` L134-L140)**：
   ```javascript
   // Model tier aliases: 'planning' | 'default' | 'fallback' resolve to configured model ids
   let model = job.model ?? strategy.budget.model;
   if ((model === 'planning' || model === 'default' || model === 'fallback') && config?.models?.[model]) {
     model = config.models[model];
   }
   ```
2. **沙盒原生支援 (`packages/daemon/src/guest-prelude.js`)**：
   - `agent({ role, prompt, model: "..." })` 支援直接指定 `model`（名稱或別名）。
   - `verify({ target, critics: [{ role, model: "..." }] })` 的審查者亦原生支援指定 `model`。
3. **設定檔模型階層 (`~/.odw/config.json`)**：
   ```json
   "models": {
     "planning": "claude-opus-5-5 / gpt-5.5 (高難度架構/複雜推理)",
     "default":  "claude-sonnet-5-5 / gpt-5.4 (標準分析/實作)",
     "fallback": "claude-haiku-5-5 / gpt-5.4-mini (輕量探索/批次過濾)"
   }
   ```

### 3. 實驗設計與驗證

在 QuickJS-WASM 隔離沙盒中執行分級編排腳本：
- **階段一：Discovery (輕量初探)** ➔ 指派 `model: "fallback"` (低成本/極速)
- **階段二：Deep Analysis (深度推理)** ➔ 指派 `model: "planning"` (旗艦推理)
- **階段三：Adversarial Verification (對抗審查)** ➔
  - 批評者 1 (False-Positive Hunter) ➔ 指派 `model: "default"` (標準模型)
  - 批評者 2 (Severity Validator) ➔ 指派 `model: "planning"` (旗艦嚴格審查)

#### 實驗程式碼與執行結果：
```text
Sandbox run completed successfully!
Captured Agent Jobs:
  Agent 1: role=discovery-agent, model=fallback
  Agent 2: role=analysis-agent, model=planning
  Agent 3: role=false-positive-hunter, model=default
  Agent 4: role=severity-validator, model=planning
```

### 4. 實驗結論
- **完全支援**：ODW 在沙盒編排引擎與 Runtime 層均完全支援多模型分級配置。
- **最佳實踐**：可透過 `~/.odw/config.json` 綁定不同 Provider 或 Ollama 模型，讓大量並行的小型探勘工作走便宜/本機模型，高難度架構與對抗驗證走旗艦模型，兼顧品質與 Token 成本。
