# Survey Open Dynamic Workflows & 跨訂閱 CLI 協作開發

本專案記錄對開源動態工作流引擎 **Open Dynamic Workflows (ODW)** 的實測調研，以及在**無商業 API Key** 的條件下，如何透過本機既有三大訂閱方案（Claude Code、GitHub Copilot、Google Antigravity）串接出高效率的多 Agent 協作工作流。

---

## 專案核心文件索引

1. **[跨訂閱 CLI 協作開發工作流指引 (方案 A)](CLI_SUBSCRIPTION_WORKFLOW.md)**
   - **核心精神**：免自備 API Key、零額外 Token 支出，充分運用既有月租訂閱配額。
   - **三階段流水線**：
     - **階段一（規格與深度調查）**：`claude` CLI（Claude 訂閱）產出技術架構規格。
     - **階段二（實作與單元測試）**：`copilot` CLI（GitHub Copilot 訂閱 / GPT-5.5）編寫程式碼並自動自我修復除錯。
     - **階段三（審查驗收與提交）**：Antigravity Agent 進行雙軸 Code Review 與 Git 規範提交。

2. **[ODW 配置紀錄與模型分級實驗報告](ODW_CONFIGURATION_AND_EXPERIMENT.md)**
   - ODW Daemon 安裝、啟動與 Antigravity 技能整合細節。
   - 深入 QuickJS-WASM 沙盒原始碼，證實 ODW 支援三層模型別名（`planning`、`default`、`fallback`）與自訂 BaseURL。
   - 沙盒實測四 Agent 分級派工之實驗數據。

---

## 範例實作產物

- **規格文件**：[specs/oauth2-client-credentials.md](specs/oauth2-client-credentials.md)（由 Claude 產出之 RFC 6749 繁體中文技術規格書）
- **C# 實作專案**：[src/OAuth2ClientCredentials/](src/OAuth2ClientCredentials/)（由 Copilot 實作、通過 4 項單元測試之 .NET 8/9/10 OAuth2 Client Credentials 類別庫）
