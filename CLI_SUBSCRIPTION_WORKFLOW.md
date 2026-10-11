# 跨訂閱 CLI 協作開發工作流指引 (方案 A)

本文件定義在**無自備 API Key** 的環境下，充分利用本機已登入的付費訂閱方案（Claude Code、GitHub Copilot、Google Antigravity），執行如同 Dynamic Workflows 般多角色接力協作的標準作業程序（SOP）。

---

## 一、各訂閱角色分工與定位

| 階段 / 角色 | 調度 CLI 指令 | 使用訂閱方案 | 核心職責 |
|---|---|---|---|
| **1. 規格與調查 (Research & Spec)** | `claude -p "<任務>" --dangerously-skip-permissions` | **Claude 訂閱** | 深入研讀規範（RFC / 官方文件）、長篇架構分析、產出技術規格 Markdown（如 `specs/`）。 |
| **2. 實作與測試 (Implementation)** | `copilot --model gpt-5.5 -p "<任務>" --allow-all` | **GitHub Copilot 訂閱** (GPT-5.5) | 依據規格建立方案、編寫程式碼、處理套件依賴、修復編譯錯誤並完成單元測試。 |
| **3. 審查與驗收 (Code Review & Audit)** | Antigravity IDE 代理人 (或 `agy`) | **Antigravity 訂閱** (Gemini / Claude) | 雙軸審查（規格對齊度 + 程式碼安全性與例外處理）、架構盲點檢視、執行最終提交。 |

---

## 二、標準執行指令範本

### 步驟 1：產出規格書 (Claude)
```bash
claude -p "請調查 <主題> 的最佳實踐架構，並將完整技術規格書輸出至 specs/<主題>.md。需包含規範細節、快取、並行互斥、與現有架構的整合方式及邊界測試清單。" --dangerously-skip-permissions
```

### 步驟 2：實作程式碼與測試 (Copilot GPT-5.5)
```bash
copilot --model gpt-5.5 -p "請依據 specs/<主題>.md 規範，在 src/<專案名>/ 建立方案與專案：
1. 實作核心服務與介面
2. 實作單元測試，涵蓋正常與極端情境
3. 執行 build 與 test 指令，確保 0 error、0 warning 且測試全數通過！" --allow-all
```

### 步驟 3：Code Review (Antigravity)
在當前 Antigravity 對話中：
- 檢查編譯狀態：`dotnet test` / `npm test`
- 逐檔審查原始碼，產出審查報告：
  - **已知事實**（實作成果與測試數據）
  - **推論**（潛在架構邊界風險）
  - **建議**（優化方向）

---

## 三、優勢與注意事項

1. **零額外 API 費用**：完全不消耗任何 pay-as-you-go 的 API Token，直接使用現有月租訂閱配額。
2. **模型互補性**：
   - Claude 擅長嚴謹邏輯思考與長文規格擬定；
   - Copilot (GPT-5.5) 具備強大的多檔案編輯能力與本機除錯韌性；
   - Antigravity 負責全貌審查與對齊團隊工程制度。
3. **安全規範**：
   - CLI 參數務必確保在工作區內執行，產物一律走乾淨的 Git 提交。
