# NyaChat Admin 後端網頁架構規劃

> **文件用途**：本文件供前端開發者參考，用於在單一 HTML 檔案中實作 NyaChat Admin 管理介面。  
> **API 來源**：`NyaChatAdminApiReference.md`（共 21 支 API）  

---

## 整體頁面結構

採用**單頁應用（SPA）**架構，所有功能在同一個 HTML 檔案中，透過 JavaScript 切換顯示區塊，不做頁面跳轉。

```
┌──────────────────────────────────────────────────┐
│  頂部導覽列（Nav Bar）                            │
├──────────────┬───────────────────────────────────┤
│              │                                   │
│  左側選單    │  主內容區（動態切換）              │
│  (Sidebar)   │                                   │
│              │                                   │
└──────────────┴───────────────────────────────────┘
```

---

## 一、全域元素

### 1.1 頂部導覽列

| 元素 | 說明 |
|------|------|
| 標題文字 | `NyaChat` |
| 引擎狀態徽章 | 顯示 `enabled: true/false`，頁面載入時呼叫 **API #18** 取得 |
| 目前模型標籤 | 顯示 `chat_model` 值，來源同上 |

**載入行為**：頁面初始化時自動呼叫 API #18，將狀態顯示於導覽列。

---

### 1.2 左側選單（Sidebar）

選單項目對應各功能頁：

| 選單項目 | 對應功能頁 |
|----------|-----------|
| 儀表板 | 系統狀態頁（預設頁面）|
| 使用者列表 | 使用者列表頁 |
| 稽核日誌 | 跨用戶稽核日誌頁 |
| Soul 管理 | Soul 管理頁 |

點擊選單項目時，切換主內容區的顯示區塊，同時更新 URL hash（如 `#users`、`#audit`、`#souls`）以便重新整理後還原位置。

---

## 二、各功能頁規劃

---

### 頁面 A：系統狀態頁（預設）

**對應 API**：#18 `GET /status`

**功能說明**：顯示引擎目前的運行狀態與設定值，作為管理員開啟後的第一眼資訊。

**需要的 UI 元素**：

1. **狀態卡片群組**（Key-Value 顯示）
   - NyaChat 是否啟用（`enabled`）
   - 對話模型（`chat_model` + `chat_provider`）
   - 摘要模型（`summarization_model`）
   - 事實萃取模型（`fact_extraction_model`）
   - 短期訊息上限（`short_term_message_limit`）
   - 摘要觸發數量（`summarize_trigger_count`）
   - 事實版本保留數（`fact_version_retention`）
   - 摘要版本保留數（`summary_version_retention`）
   - 預設 Soul ID（`default_soul_id`）

2. **已載入 Soul 列表**（`available_souls`）
   - 以表格或清單顯示 `id` 與 `display_name`

3. **「重新整理」按鈕**
   - 手動重新呼叫 API #18

---

### 頁面 B：使用者列表頁

**對應 API**：#01 `GET /users`

**功能說明**：列出所有有對話記錄的使用者，作為進入個別使用者管理的入口。

**需要的 UI 元素**：

1. **搜尋列**
   - `keyword` 文字輸入框（佔位文字：「輸入 user_id 關鍵字搜尋」）
   - 「**搜尋**」按鈕（觸發後重置 offset = 0）

2. **使用者資料表**
   - 欄位：`user_id`、`message_count`、`active_fact_count`、`last_message_at`、`soul_id`
   - 每列末尾有「**查看詳情**」按鈕，點擊後導入「使用者詳情頁」並帶入 `user_id`

3. **分頁控制列**
   - `limit` 下拉選單（預設 50，可選 20 / 50 / 100）
   - 上一頁 / 下一頁按鈕（根據 `total` 與 `offset` 計算是否顯示）
   - 顯示「第 X–Y 筆，共 Z 筆」

4. **「重新整理」按鈕**

---

### 頁面 C：使用者詳情頁

**觸發方式**：從使用者列表頁點擊「查看詳情」後進入，URL hash 建議設為 `#user/{userId}`

此頁為子頁面，包含以下多個功能分區（可用分頁 Tab 或折疊區塊實作）。

---

#### C-1：用戶概覽區

**對應 API**：#02 `GET /users/{userId}/overview`

**UI 元素**：
- `user_id` 顯示（標題）
- 訊息統計（`messages.total` / `active` / `summarized` / `deleted`）
- 事實統計（`facts.total_active` 及各 category 數量）
- 摘要統計（`summaries.total_versions` / `active_version` / `latest_covered_to_seq`）
- 最後訊息時間（`last_message_at`）
- 最後事實更新時間（`last_fact_updated_at`）
- 目前綁定的 Soul（`soul_id`）

**危險操作區**（置於概覽區底部，需二次確認）：
- 「**清除全部記憶**」按鈕 → 呼叫 API #17 `DELETE /users/{userId}`，彈出確認對話框，顯示將刪除的數量後再執行

---

#### C-2：訊息管理區

**對應 API**：#03、#04、#05

**UI 元素**：

1. **訊息列表**（API #03）
   - 欄位：序號（`Sequence`）、角色（`Role`）、內容預覽（`content_preview`，最多 100 字）、狀態（`IsDeleted` / `IsSummarized`）、建立時間（`CreatedAt`）
   - 每列有「**查看全文**」按鈕 → 呼叫 API #04，在 Modal 中顯示完整內容與 `metadata_json`
   - 每列可勾選（checkbox），用於批量刪除
   - `limit` 下拉選單（可選 20 / 50 / 100，預設 50）
   - 上一頁 / 下一頁按鈕（根據 `total` 與 `offset` 計算）

2. **操作按鈕列**
   - 「**軟刪除全部訊息**」按鈕 → 呼叫 API #05（不帶 `message_ids`），需二次確認對話框
   - 「**刪除已勾選**」按鈕（勾選至少一筆時顯示）→ 組合 `message_ids` 呼叫 API #05（最多 50 筆/次），需確認對話框

3. **「查看全文」Modal**（API #04）
   - 顯示欄位：`message_id`、`role`、`content`（完整）、`sequence`、`topic_id`、`is_deleted`、`is_summarized`、`created_at`
   - `metadata_json` 呈現規格：使用 `JSON.parse()` 解析後，以 `<pre><code>` 區塊輸出格式化（縮排 2 空格）的 JSON 字串；若解析失敗則原文顯示。此欄含模型名稱、延遲毫秒數、token 用量等除錯關鍵資訊，禁止以純字串 `innerHTML` 直接嵌入。

---

#### C-3：話題管理區

**對應 API**：#06

**UI 元素**：
- 說明文字：「建立新話題區段，強制在此處產生對話上下文的分隔」
- 「**建立新話題**」按鈕 → 呼叫 API #06 `POST /users/{userId}/topics`
- 執行後顯示回傳的 `topic_id`

---

#### C-4：事實（Facts）管理區

**對應 API**：#07、#08、#09、#10、#11

**UI 元素**：

1. **現有事實列表**（API #07）
   - 欄位：`FactKey`、`Category`、`FactValue`、`Confidence`、`Version`、`CreatedAt`
   - `FactValue` 欄位若內容過長，儲存格須限制最大高度（建議 `max-height: 4em`）並啟用垂直捲動軸，避免長文字內容撐爆表格列高
   - 每列操作：
     - 「**查看歷史**」按鈕 → 展開/Modal 顯示該 factKey 的版本歷史（API #09）
     - 「**回滾**」按鈕 → 觸發回滾操作（API #10），需確認對話框

2. **版本歷史 Modal/展開區**（API #09）
   - 欄位：`Version`、`FactValue`、`Confidence`、`IsActive`、`CreatedAt`、`SupersededAt`
   - `FactValue` 同樣需設最大高度與捲動，避免歷史版本中的長值破版
   - 每個非 active 版本旁有「**回滾至此版本**」按鈕

3. **手動新增/覆蓋事實表單**（API #08）
   - 欄位：`category`（下拉，選項：`identity` / `preference` / `context` / `instruction`）、`key`（文字輸入）、`value`（文字輸入）、`confidence`（數字輸入，0.0–1.0，預設 1.0）
   - 「**送出**」按鈕

4. **危險操作**
   - 「**硬刪除全部事實**」按鈕 → 呼叫 API #11，需二次確認，強調「永久刪除、不可還原」
   - 每列操作區加入「**刪除此事實**」按鈕 → 呼叫 API #22 `DELETE /users/{userId}/facts/{factKey}`，確認對話框顯示將刪除的 factKey，需二次確認

---

#### C-5：摘要（Summaries）管理區

**對應 API**：#12、#13、#14

**UI 元素**：

1. **摘要版本列表**（API #12）
   - 欄位：`Version`、`IsActive`（標示 active 版本）、`summary_preview`（前 150 字）、`CoveredFromSeq`–`CoveredToSeq`、`MessageCount`、`CreatedAt`
   - `summary_preview` 欄位須設最大高度（建議 `max-height: 6em`）並啟用垂直捲動軸，避免 150 字預覽撐爆列高
   - 每個非 active 版本旁有「**回滾至此版本**」按鈕 → 呼叫 API #13，需確認對話框

2. **危險操作**
   - 「**硬刪除全部摘要**」按鈕 → 呼叫 API #14，需二次確認
   - 後端已在同一 transaction 中重置所有訊息的 `is_summarized` 旗標，UI 無需額外警告

---

#### C-6：用戶稽核日誌區

**對應 API**：#15 `GET /users/{userId}/audit`

**UI 元素**：

1. **篩選列**
   - `action` 下拉篩選（選項：全部 / `chat_input` / `chat_output` / `fact_extract` / `fact_upsert` / `fact_rollback` / `summary_create` / `summary_rollback` / `memory_delete` / `topic_create` / `error`）
   - `limit` 下拉（可選 20 / 50 / 100，預設 50）
   - 「**查詢**」按鈕

2. **日誌資料表**
   - 欄位：`LogId`、`Action`、`Result`、`CreatedAt`
   - 每列有「**查看詳情**」按鈕，展開/Modal 顯示解析後的 `DetailJson`（同 C-2 Modal 的 `metadata_json` 規格：`JSON.parse()` 後以 `<pre><code>` 格式化輸出）

---

#### C-7：Soul 綁定區

**對應 API**：#21、#19

**UI 元素**：
- 顯示目前綁定的 Soul（`soul_id`，來自概覽 API #02）
- Soul 下拉選單（從 API #19 取得 `available_souls` 清單，顯示 `display_name`，值為 `id`）
- 「**套用綁定**」按鈕 → 呼叫 API #21 `POST /users/{userId}/soul`
- 執行後顯示回傳的綁定版本（`version`）

---

### 頁面 D：跨用戶稽核日誌頁

**對應 API**：#16 `GET /audit`

**功能說明**：不針對特定用戶，可搜尋任意 user_id 的稽核記錄。

**導入動線**：管理員通常不會記得用戶的 LINE ID 原文。前端需在 `user_id` 輸入框旁提供「**從使用者列表選取**」捷徑按鈕：點擊後導至頁面 B（使用者列表），當管理員在列表中點擊任一用戶的「查看詳情」時，除了開啟 C 頁面外，同時將該 `user_id` 帶回並填入此欄，返回頁面 D 自動觸發查詢。（實作上可透過全域狀態變數或 URL hash 參數傳遞 `user_id`。）

**UI 元素**：

1. **查詢表單**
   - `user_id` 文字輸入框（必填）＋「**從使用者列表選取**」捷徑按鈕（見上方導入動線說明）
   - `action` 下拉篩選（同 C-6 選項）
   - `limit` 下拉（可選 20 / 50 / 100，預設 50）
   - 「**查詢**」按鈕

2. **日誌資料表**
   - 欄位：`UserId`（超連結，點擊跳轉至該用戶詳情頁 C）、`LogId`、`Action`、`Result`、`CreatedAt`
   - 每列有「**查看詳情**」按鈕，展開顯示解析後的 `DetailJson`（`JSON.parse()` 後以 `<pre><code>` 格式化輸出）
   - 橫向分頁：加入 `limit` + `offset` 控制列（同頁面 B 分頁規格）

---

### 頁面 E：Soul 管理頁

**對應 API**：#19 `GET /souls`、#20 `POST /souls/reload`

**UI 元素**：

1. **Soul 清單**（API #19）
   - 以表格顯示 `id` 與 `display_name`
   - 顯示 Soul 總數（`total`）

2. **操作區**
   - 「**熱重載 Soul 檔案**」按鈕 → 呼叫 API #20 `POST /souls/reload`
   - 執行後顯示：載入數量（`loaded`）、更新後的 Soul 清單
   - 附加說明文字：「編輯 souls/ 目錄下的 JSON 檔後，點此按鈕套用，無需重啟伺服器」

---

## 三、共用元件規劃

以下元件需實作為可複用的 JavaScript 函式或 HTML 樣板。

### 3.1 確認對話框（Confirm Dialog）

**使用時機**：所有刪除操作、回滾操作  
**必要元素**：
- 標題（描述操作，如「確認刪除所有訊息？」）
- 描述文字（說明影響範圍，如「將軟刪除 48 筆訊息」）
- 「**確認**」按鈕
- 「**取消**」按鈕

**實作建議**：使用原生 `<dialog>` 元素或自訂 Modal div，透過 Promise 包裝使 async 呼叫流程清晰。

---

### 3.2 API 呼叫封裝

實作一個通用的 `apiCall(method, path, body)` 函式：
- 自動附加 Cookie（`credentials: 'include'`）
- 統一解析標準回應信封（`success`, `message`, `data`, `error_code`, `trace_id`）
- 失敗時：顯示錯誤訊息，附上 `message` 與 `trace_id` 供除錯
- 401 錯誤：提示「Session 已過期，請重新登入」
- 403 錯誤：提示「僅限本機存取」

---

### 3.3 訊息提示列（Toast / Alert Bar）

**使用時機**：API 操作成功或失敗後的即時回饋  
**元素**：頁面頂部或右下角的浮動提示，顯示 2–3 秒後自動消失  
**類型**：成功（綠）、錯誤（紅）、提示（黃）

---

### 3.4 Loading 狀態

所有 API 呼叫期間：
- 對應按鈕改為 disabled 狀態並顯示「載入中…」
- 資料表區塊顯示 loading 指示（如旋轉圖示或骨架屏）

---

## 四、頁面導覽流程圖

```
[系統狀態頁 A]
     │
[使用者列表頁 B]
     │
     └──► [使用者詳情頁 C]
               ├── [C-1] 概覽 + 清除全部記憶
               ├── [C-2] 訊息管理（列表 + 查看全文 + 軟刪除）
               ├── [C-3] 話題管理（建立新話題）
               ├── [C-4] 事實管理（列表 + 歷史 + 回滾 + 新增 + 硬刪除）
               ├── [C-5] 摘要管理（列表 + 回滾 + 硬刪除）
               ├── [C-6] 用戶稽核日誌
               └── [C-7] Soul 綁定

[跨用戶稽核日誌頁 D]

[Soul 管理頁 E]
```

---

## 五、API 對應總覽

| API # | Method | 路徑 | 對應頁面/區塊 |
|-------|--------|------|--------------|
| 01 | GET | `/users` | 頁面 B：使用者列表 |
| 02 | GET | `/users/{userId}/overview` | C-1：用戶概覽 |
| 03 | GET | `/users/{userId}/messages` | C-2：訊息列表 |
| 04 | GET | `/users/{userId}/messages/{messageId}` | C-2：查看全文 Modal |
| 05 | DELETE | `/users/{userId}/messages` | C-2：軟刪除全部或指定訊息 |
| 06 | POST | `/users/{userId}/topics` | C-3：建立新話題 |
| 07 | GET | `/users/{userId}/facts` | C-4：事實列表 |
| 08 | POST | `/users/{userId}/facts` | C-4：手動新增/覆蓋事實 |
| 09 | GET | `/users/{userId}/facts/{factKey}/history` | C-4：版本歷史 Modal |
| 10 | POST | `/users/{userId}/facts/{factKey}/rollback` | C-4：事實回滾 |
| 11 | DELETE | `/users/{userId}/facts` | C-4：硬刪除全部事實 |
| 12 | GET | `/users/{userId}/summaries` | C-5：摘要版本列表 |
| 13 | POST | `/users/{userId}/summaries/{summaryId}/rollback` | C-5：摘要回滾 |
| 14 | DELETE | `/users/{userId}/summaries` | C-5：硬刪除全部摘要（同步重置 is_summarized）|
| 15 | GET | `/users/{userId}/audit` | C-6：用戶稽核日誌 |
| 16 | GET | `/audit` | 頁面 D：跨用戶稽核 |
| 17 | DELETE | `/users/{userId}` | C-1：清除全部記憶 |
| 18 | GET | `/status` | 頁面 A：系統狀態 + 導覽列 |
| 19 | GET | `/souls` | 頁面 E：Soul 清單；C-7：Soul 下拉選單 |
| 20 | POST | `/souls/reload` | 頁面 E：熱重載按鈕 |
| 21 | POST | `/users/{userId}/soul` | C-7：Soul 綁定 |
| 22 | DELETE | `/users/{userId}/facts/{factKey}` | C-4：硬刪除單一事實 |

---

## 六、注意事項與技術要點

### 6.1 欄位命名混用問題

API 回應中存在 PascalCase（直接序列化自 C# 模型）與 snake_case（自訂欄位）混用的情況，開發者需注意以下欄位實際名稱：

- **訊息列表**（API #03）：`MessageId`, `Role`, `Sequence`, `TopicId`, `IsDeleted`, `IsSummarized`, `CreatedAt`（PascalCase）；`content_preview`（snake_case）
- **事實列表**（API #07）：全部為 PascalCase（`FactId`, `Category`, `FactKey`, `FactValue`, `Confidence`, `Version`, `SourceMessageId`, `CreatedAt`）
- **單筆訊息**（API #04）：全部為 snake_case

### 6.2 `soul_binding` Category 保護

新增事實表單的 `category` 下拉選單中，**不得**出現 `soul_binding` 選項。Soul 綁定只能透過 C-7 區塊（API #21）操作。

### 6.3 分頁實作

API #01（使用者列表）、#03（訊息列表）、#15（用戶稽核）、#16（跨用戶稽核）均支援 `limit` + `offset` 分頁，前端可統一套用相同的分頁元件邏輯。
