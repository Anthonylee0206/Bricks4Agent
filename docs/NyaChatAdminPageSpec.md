# NyaChat Admin 前端頁面架構

> **文件用途**：描述 `packages/csharp/broker/wwwroot/NYAChat/` 管理介面目前的頁面結構、權限與對應 API，供前端維護者參考。
> **API 來源**：[NyaChatAdminApiReference.md](NyaChatAdminApiReference.md)。本文以路徑指稱 API（皆位於 `/api/v1/local-admin/nya/` 之下），不使用編號。
> **記憶模型**：舊版「事實（facts）萃取／調和」已移除，改為「回合記憶（turns）+ 歷史記憶（筆記、摘要、聊天室）」。程式內 `PageC_facts.jsx` 與分頁 id `facts` 為沿用的舊名，畫面上顯示為「筆記」。

---

## 一、技術架構

- 單一 `index.html` 載入 React 18.3.1（development build）、ReactDOM 與 babel-standalone 7.29.0，三者皆 vendored 於 `wwwroot/vendor/`，以 SRI 驗證；各頁 `.jsx` 以 `<script type="text/babel">` 在瀏覽器端即時編譯。
- 外框（頂部列、左側選單、快速跳轉、登出、連線狀態）由 Broker 共用的 `admin-shell.js` / `admin-shell.css` 提供，NyaChat 只負責主內容區。
- 路由使用 URL hash：`#home`、`#users`、`#user/{userId}?tab=...`、`#audit`、`#souls`、`#tools`、`#llm`、`#settings`。未知 hash 導回 `#home`；舊的 `#roles` 會提示「角色管理已交回 Broker」。
- 已出貨的 `.jsx` / `.css` / `.html` 不得含註解，由 `packages/csharp/tests/nya-admin-ui.test.cjs` 檢查。

### 1.1 登入與權限

- 啟動時 `index.html` 先呼叫 `GET /api/v1/local-admin/status` 取得登入狀態與權限（存於 `window.__nyaAuth`）；未登入導向 `/line-admin.html`。
- API 回 401 時顯示「Session 已過期」並導回 `/line-admin.html`；403 顯示「權限不足」。

| 判斷 | 條件 |
|------|------|
| 可讀 使用者頁 | `permission.user.manage` 或 `audit.read` |
| 可讀 稽核日誌 | `audit.read` |
| 可讀 LLM 模型 | `system.status.read` |
| 可讀 其他頁 | `system.status.read` 或 `audit.read` |
| 可寫 `users/*` | `permission.user.manage` |
| 可寫 Soul、工具、設定等系統項 | `system.worker.manage` |
| 可寫 `llm/*`、刪除稽核 | 角色 `super_admin` |

按鈕以 `write="user" | "system" | "super"` 標示所需寫入權限，無權限時停用；`apiCall()` 在送出前也會再擋一次。後端仍是最終判斷。

---

## 二、各頁說明

### 總覽 `#home`（PageHome.jsx）

- 指標卡：對話引擎狀態、使用者數、全域已啟用工具數、錯誤紀錄數。
- 「最近活躍的使用者」（`GET users?limit=8`）與「最近的錯誤」（`GET audit?result=error&limit=6`），點選可直接跳到使用者或稽核。
- 常用功能捷徑與「目前設定」摘要（`GET status`）。

### 使用者 `#users`（PageB.jsx）

- `GET users`：關鍵字搜尋使用者 ID、分頁。
- 欄位：使用者 ID、通道（由 ID 前綴判斷：無前綴為 LINE，`discord:`、`web:`）、訊息數、未過期筆記數、最後訊息、Soul（綁定／生效）。

### 使用者詳情 `#user/{userId}`（PageC.jsx、PageC_facts.jsx、PageC_misc.jsx）

頁首顯示使用者 ID、通道與生效 Soul，以分頁切換：

| 分頁 | 內容 | 主要 API |
|------|------|----------|
| 概覽 | 目前聊天室、近期訊息預覽、未過期筆記、遺忘紀錄數；筆記分類分布；摘要歷史；Soul 綁定；最後一輪注入內容；危險區「清除全部記憶」 | `GET users/{id}/overview`、`POST/DELETE users/{id}/soul`、`DELETE users/{id}` |
| 訊息 | 依聊天室／外部會話／未歸屬篩選；全文 Modal；勾選軟刪除或全部軟刪除；狀態標記（已刪除、摘要旗標、失敗輪、排除原因） | `GET users/{id}/messages`、`GET users/{id}/messages/{messageId}`、`POST users/{id}/messages/soft-delete`、`DELETE users/{id}/messages` |
| 輪次 | 每輪狀態（處理中／完成／失敗）、範圍、起訖聊天室；檢視工作紀錄 | `GET users/{id}/turns`、`GET users/{id}/turns/{turnId}/work` |
| 聊天室 | 列表、新話題、切換、改名、刪除；切換於下一輪生效 | `GET/POST users/{id}/topics`、`POST .../topics/{topicId}/switch`、`POST .../rename`、`DELETE .../topics/{topicId}` |
| 筆記 | 筆記版本（有效／歷史／過期／已隱藏）、新增與更正、還原版本；遺忘候選與隱藏引用、撤銷 | `GET/POST users/{id}/notes`、`POST .../notes/{noteId}/restore`、`POST .../forget-find`、`POST .../forget`、`GET .../hide-references`、`POST .../hide-references/{forgetId}/unforget`、`GET .../selections` |
| 摘要 | 摘要版本與來源訊息、立即摘要、還原版本 | `users/{id}/summaries` 系列 |
| 稽核 | 此使用者的稽核紀錄，依動作／結果／關鍵字篩選；清空（需 super_admin） | `GET/DELETE users/{id}/audit`、`DELETE audit/{logId}`、`GET audit/traces/{traceId}` |

「稽核」分頁只在有 `audit.read` 時出現。

### 稽核日誌 `#audit`（PageD.jsx）

- `GET audit`：跨使用者查詢，條件為關鍵字、User ID、動作、結果（成功／錯誤／略過）、trace。
- 每筆可展開明細，連到「本輪鏈」（`GET audit/traces/{traceId}`）。
- 刪除單筆或清空符合條件者（`DELETE audit/{logId}`、`DELETE audit`），需 super_admin。

### Soul 人格 `#souls`（PageE.jsx）

- `GET souls` 列表；新增／編輯表單（Soul ID 建立後不可改、顯示名稱、personality 五欄、rules、forbidden）。
- 刪除前顯示受影響的綁定使用者；被刪 Soul 的綁定保留，使用者暫時回退預設 Soul。
- 「從檔案重新載入」（`POST souls/reload`），載入失敗的檔案另列。

### 工具 `#tools`（PageF.jsx）

- `GET tools` 依群組（聊天室、筆記、搜尋、交通）列出並以開關切換（`POST tools/{toolId}/toggle`）。
- 「使用者工具可用性」診斷：輸入 User ID 與通道，呼叫 `GET tools/availability`，顯示每個工具的全域開關與使用者是否授權。使用者授權在 Broker「LINE 與使用者」頁調整。

### LLM 模型 `#llm`（PageG.jsx）

- **Profile 清單**：`GET llm/profiles`；新增／編輯（可套模板 `GET llm/templates`、參數目錄 `GET llm/param-catalog`）、測試連線（`POST llm/profiles/{id}/test`）、刪除（系統預設使用中者不可刪）。API Key 欄位留空或含 `*` 表示保留原值。`extra_body` 不可覆寫 `model`、`messages`、`tools`、`stream`。
- **呼叫點路由**：`GET llm/call-sites`、`GET llm/routes`，以 `PUT llm/routes` 指定每個呼叫點用哪個 Profile；「預設」代表跟隨 `__default__`。立即生效。
- 本頁所有寫入需 super_admin。

### 對話設定 `#settings`（PageI.jsx）

- `GET config/effective` 回傳可熱調整的設定清單（鍵、標籤、型別、範圍、說明、目前值、預設值），定義於 `NyaChatHotSettings.All`。
- 單項修改以 `PATCH config` 送出，前端先檢查範圍與整數；`DefaultSoulId` 必須指向現有 Soul。
- 「還原 appsettings 預設」（`POST config/reset`）只還原本頁列出的設定。

---

## 三、共用元件（components.jsx、ui.jsx）

- `showConfirm(title, message)`：所有刪除、清除、切換類操作前的確認對話框。
- `showToast(message, type)`：操作結果提示；錯誤訊息附上回應信封的 `traceId`。
- `PageHeader`、`Panel`、`Table`、`Pagination`、`Modal`、`JsonBlock`、`DangerZone` 等版面元件。
- `apiCall(method, path, params)`（api.jsx）：GET／DELETE 參數走 query string，其他方法走 JSON body；統一處理 401／403 與錯誤提示。
