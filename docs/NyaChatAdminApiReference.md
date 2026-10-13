# NyaChat Admin API Reference

All endpoints are under `/api/v1/local-admin/nya/`.

## Authentication

All requests must come from **localhost** and carry a valid session cookie.

- Cookie name: `b4a_local_admin`
- Obtain via: `POST /api/v1/local-admin/auth/login`
- Errors: `403 Forbidden` (non-localhost), `401 Unauthorized` (no valid session)

## Standard Response Envelope

Every response uses this wrapper regardless of success or failure.

```json
{
  "success": true | false,
  "message": "ok" | "<error description>",
  "data": { ... } | null,
  "error_code": null | 400 | 401 | 403 | 404,
  "trace_id": "<uuid>"
}
```

All field schemas below describe the shape of `data` only.

---

## Endpoints

---

### 01 · GET /local-admin/nya/users

**Purpose**: List all users who have conversation history. Dashboard entry point — required to enumerate users before querying per-user endpoints.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Max users per page |
| offset | int | no | 0 | Pagination offset |
| keyword | string | no | (none) | Filter by user_id (LIKE %keyword%) |

**Response `data`**
```json
{
  "total": 12,
  "limit": 50,
  "offset": 0,
  "keyword": null,
  "users": [
    {
      "user_id": "Uabc123...",
      "message_count": 48,
      "active_fact_count": 7,
      "last_message_at": "2026-05-28T10:30:00Z",
      "soul_id": "default"
    }
  ]
}
```

**Errors**: 401, 403

---

### 02 · GET /local-admin/nya/users/{userId}/overview

**Purpose**: One-shot stats for a single user — replaces 4 separate calls (messages / facts / summaries / soul) for dashboard user-detail page.

**Path Params**: `userId` — LINE user ID or any registered user ID.

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "soul_id": "default",
  "messages": {
    "total": 52,
    "active": 20,
    "summarized": 28,
    "deleted": 4
  },
  "facts": {
    "total_active": 6,
    "categories": {
      "identity": 2,
      "preference": 3,
      "context": 1
    }
  },
  "summaries": {
    "total_versions": 3,
    "active_version": 3,
    "latest_covered_to_seq": 28
  },
  "last_message_at": "2026-05-28T10:30:00Z",
  "last_fact_updated_at": "2026-05-28T10:31:00Z"
}
```

**Field notes**
- `messages.total` includes soft-deleted rows
- `messages.active` = not deleted AND not summarized (what the LLM currently sees)
- `summaries.active_version` = 0 if no summary exists

**Errors**: 401, 403

---

### 03 · GET /local-admin/nya/users/{userId}/messages

**Purpose**: Paginated list of a user's conversation records. Content is truncated to 100 chars — use endpoint 04 for full content.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Max rows |
| offset | int | no | 0 | Pagination offset |

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "total": 48,
  "limit": 50,
  "offset": 0,
  "messages": [
    {
      "MessageId": "nyam_abc...",
      "Role": "user",
      "content_preview": "你好，我叫小明...",
      "Sequence": 42,
      "TopicId": null,
      "IsDeleted": false,
      "IsSummarized": false,
      "CreatedAt": "2026-05-28T10:30:00Z"
    }
  ]
}
```

**Field naming note**: Fields sourced directly from C# model properties (`MessageId`, `Role`, `Sequence`, `TopicId`, `IsDeleted`, `IsSummarized`, `CreatedAt`) are serialized as **PascalCase**. `content_preview` is a custom-named field defined in the endpoint and uses snake_case.

**Errors**: 401, 403

---

### 04 · GET /local-admin/nya/users/{userId}/messages/{messageId}

**Purpose**: Retrieve a single message with **full untruncated content** and raw `metadata_json` (model name, latency, token count).

**Path Params**: `userId`, `messageId`

**Response `data`**
```json
{
  "message_id": "nyam_abc...",
  "user_id": "Uabc123...",
  "role": "user",
  "content": "你好，我叫小明，我是一名軟體工程師，正在開發一個…",
  "sequence": 42,
  "topic_id": null,
  "is_deleted": false,
  "is_summarized": false,
  "metadata_json": "{\"model\":\"qwen3.6:35b-a3b-Q4\",\"latency_ms\":1823}",
  "created_at": "2026-05-28T10:30:00Z"
}
```

**Errors**: 401, 403, 404 (message not found or belongs to different user)

---

### 05 · DELETE /local-admin/nya/users/{userId}/messages

**Purpose**: Soft-delete messages for a user. If `message_ids` is provided, only those messages are deleted (max 50 per call); otherwise all messages are soft-deleted.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| message_ids | string | no | (none) | Comma-separated list of MessageIds to delete selectively (e.g. `id1,id2,id3`). Max 50 per call. |

**Response `data`** (selective delete)
```json
{
  "user_id": "Uabc123...",
  "soft_deleted": 2,
  "selective": true,
  "requested_ids": 2
}
```

**Response `data`** (all delete — no `message_ids` provided)
```json
{
  "user_id": "Uabc123...",
  "soft_deleted": 48,
  "selective": false
}
```

**Errors**: 401, 403

---

### 06 · POST /local-admin/nya/users/{userId}/topics

**Purpose**: Manually create a new topic segment. Used to force a context break between conversations.

**Request Body**: none required

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "topic_id": "topic_f3a9b2c1d0"
}
```

**Errors**: 401, 403

---

### 07 · GET /local-admin/nya/users/{userId}/facts

**Purpose**: List all currently active (is_active=true) facts for a user. These are exactly what the LLM sees in the system prompt's `<user_profile>` section.

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "total": 6,
  "facts": [
    {
      "FactId": "nyaf_xyz...",
      "Category": "identity",
      "FactKey": "name",
      "FactValue": "小明",
      "Confidence": 0.95,
      "Version": 2,
      "SourceMessageId": "nyam_abc...",
      "CreatedAt": "2026-05-28T10:31:00Z"
    }
  ]
}
```

**Field naming note**: All fields (`FactId`, `Category`, `FactKey`, `FactValue`, `Confidence`, `Version`, `SourceMessageId`, `CreatedAt`) are serialized directly from C# model properties and appear as **PascalCase**.

**Category values**: `identity`, `preference`, `context`, `instruction`, `soul_binding` (reserved — use endpoint 21 to set soul binding)

**Errors**: 401, 403

---

### 08 · POST /local-admin/nya/users/{userId}/facts

**Purpose**: Manually create or overwrite a fact. Triggers the same version-control logic as LLM extraction — old version is retired, new version gets version+1.

**Request Body**
```json
{
  "category": "preference",
  "key": "language",
  "value": "繁體中文",
  "confidence": 1.0
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| category | string | yes | One of: `identity`, `preference`, `context`, `instruction`. (`soul_binding` is reserved — use endpoint 21 instead.) |
| key | string | yes | Fact key, e.g. `name`, `language`, `job_title` |
| value | string | yes | Fact content |
| confidence | float | no (default: 1.0) | 0.0–1.0 |

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "fact_id": "nyaf_xyz...",
  "fact_key": "language",
  "category": "preference",
  "fact_value": "繁體中文",
  "version": 3
}
```

**Errors**: 401, 403, 400 (missing category / key / value)

---

### 09 · GET /local-admin/nya/users/{userId}/facts/{factKey}/history

**Purpose**: Show all versions of a single fact key — for auditing or picking a rollback target.

**Path Params**: `userId`, `factKey` (e.g. `name`)

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "fact_key": "name",
  "total": 3,
  "history": [
    {
      "FactId": "nyaf_v3...",
      "Category": "identity",
      "FactValue": "明明",
      "Confidence": 0.9,
      "Version": 3,
      "IsActive": true,
      "CreatedAt": "2026-05-28T11:00:00Z",
      "SupersededAt": null
    },
    {
      "FactId": "nyaf_v2...",
      "Category": "identity",
      "FactValue": "小明",
      "Confidence": 0.95,
      "Version": 2,
      "IsActive": false,
      "CreatedAt": "2026-05-28T10:31:00Z",
      "SupersededAt": "2026-05-28T11:00:00Z"
    }
  ]
}
```

**Errors**: 401, 403

---

### 10 · POST /local-admin/nya/users/{userId}/facts/{factKey}/rollback

**Purpose**: Roll back a fact to its previous version (or a specific version). The current active version is retired; the target version becomes active again.

**Path Params**: `userId`, `factKey`

**Request Body**
```json
{
  "target_version": 2
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| target_version | int | no | Version to restore. Omit to roll back to the most recent non-active version. |

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "fact_key": "name",
  "rolled_to_version": 2,
  "fact_value": "小明"
}
```

**Errors**: 401, 403, 400 (no previous version exists or target version not found)

---

### 11 · DELETE /local-admin/nya/users/{userId}/facts

**Purpose**: Hard-delete all facts for a user (permanent, not soft-delete).

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "deleted": 6
}
```

**Errors**: 401, 403

---

### 12 · GET /local-admin/nya/users/{userId}/summaries

**Purpose**: List all summary versions for a user. The active version is what the LLM sees in `<long_term_memory>`.

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "total": 3,
  "summaries": [
    {
      "SummaryId": "nyas_abc...",
      "Version": 3,
      "IsActive": true,
      "summary_preview": "使用者小明是軟體工程師，正在開發…",
      "CoveredFromSeq": 1,
      "CoveredToSeq": 28,
      "MessageCount": 28,
      "CreatedAt": "2026-05-28T12:00:00Z",
      "SupersededAt": null
    }
  ]
}
```

**Note**: `summary_preview` is the first 150 chars of `summary_text`. Use the rollback endpoint to inspect full content indirectly by rolling back and checking the active summary.

**Errors**: 401, 403

---

### 13 · POST /local-admin/nya/users/{userId}/summaries/{summaryId}/rollback

**Purpose**: Roll back to a previous summary version. Also restores `is_summarized=false` on the messages that were covered by the retired summary, and re-marks them for the restored version.

**Path Params**: `userId`, `summaryId` (the target summary to restore — must be inactive)

**Request Body**: none

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "summary_id": "nyas_v2...",
  "rolled_to_version": 2,
  "covered_from_seq": 1,
  "covered_to_seq": 20
}
```

**Errors**: 401, 403, 404 (summary not found or already active)

---

### 14 · DELETE /local-admin/nya/users/{userId}/summaries

**Purpose**: Hard-delete all summaries for a user. Also resets `is_summarized = false` on all non-deleted messages in a single transaction, ensuring full data consistency.

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "deleted": 3
}
```

**Errors**: 401, 403

---

### 15 · GET /local-admin/nya/users/{userId}/audit

**Purpose**: Retrieve per-user audit log entries in reverse chronological order. Covers all engine events: chat I/O, fact changes, summarization, errors.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| action | string | no | (all) | Filter by action type (see below) |
| limit | int | no | 50 | Max rows |
| offset | int | no | 0 | Pagination offset |

**Action type values**: `chat_input`, `chat_output`, `fact_extract`, `fact_upsert`, `fact_rollback`, `summary_create`, `summary_rollback`, `memory_delete`, `topic_create`, `error`

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "total": 120,
  "limit": 50,
  "offset": 0,
  "logs": [
    {
      "LogId": "nyal_abc...",
      "Action": "chat_output",
      "Result": "success",
      "DetailJson": "{\"reply_preview\":\"好的！\",\"model\":\"qwen3.6:35b-a3b-Q4\",\"latency_ms\":1823,\"part_count\":1}",
      "CreatedAt": "2026-05-28T10:30:01Z"
    }
  ]
}
```

**Errors**: 401, 403

---

### 16 · GET /local-admin/nya/audit

**Purpose**: Same as endpoint 15 but without a path-level user_id — for cross-user queries or when user_id contains special characters.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| user_id | string | yes | — | User to query |
| action | string | no | (all) | Filter by action type |
| limit | int | no | 50 | Max rows |
| offset | int | no | 0 | Pagination offset |

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "total": 120,
  "limit": 50,
  "offset": 0,
  "logs": [
    {
      "UserId": "Uabc123...",
      "LogId": "nyal_abc...",
      "Action": "chat_output",
      "Result": "success",
      "DetailJson": "{\"reply_preview\":\"好的！\",\"model\":\"qwen3.6:35b-a3b-Q4\",\"latency_ms\":1823,\"part_count\":1}",
      "CreatedAt": "2026-05-28T10:30:01Z"
    }
  ]
}
```

**Errors**: 401, 403, 400 (missing user_id)

---

### 17 · DELETE /local-admin/nya/users/{userId}

**Purpose**: Nuke all memory for a user in one call — soft-deletes messages, hard-deletes facts and summaries. Use before re-onboarding a user or during testing cleanup.

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "soft_deleted_messages": 48,
  "deleted_facts": 6,
  "deleted_summaries": 3
}
```

**Errors**: 401, 403

---

### 18 · GET /local-admin/nya/status

**Purpose**: Engine health check — returns current config values and loaded Souls. Use to confirm NyaChat is enabled and which model is active.

**Response `data`**
```json
{
  "enabled": true,
  "chat_model": "qwen3.6:35b-a3b-Q4",
  "chat_provider": "ollama",
  "fact_extraction_model": "qwen3.6:35b-a3b-Q4",
  "summarization_model": "qwen3.6:35b-a3b-Q4",
  "short_term_message_limit": 30,
  "summarize_trigger_count": 30,
  "fact_version_retention": 5,
  "summary_version_retention": 5,
  "default_soul_id": "default",
  "available_souls": [
    { "id": "default", "display_name": "NYA" }
  ]
}
```

**Errors**: 401, 403

---

### 19 · GET /local-admin/nya/souls

**Purpose**: List all Soul JSON files loaded from the `souls/` directory.

**Response `data`**
```json
{
  "total": 1,
  "souls": [
    { "id": "default", "display_name": "NYA" }
  ]
}
```

**Errors**: 401, 403

---

### 20 · POST /local-admin/nya/souls/reload

**Purpose**: Hot-reload all Soul JSON files from disk without restarting the server. Use after editing a soul JSON file.

**Request Body**: none

**Response `data`**
```json
{
  "ok": true,
  "loaded": 1,
  "souls": [
    { "id": "default", "display_name": "NYA" }
  ]
}
```

**Errors**: 401, 403

---

### 21 · POST /local-admin/nya/users/{userId}/soul

**Purpose**: Bind a specific Soul to a user. Stored as a `soul_binding` fact. Overrides the `default_soul_id` config for this user.

**Request Body**
```json
{
  "soul_id": "default"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| soul_id | string | yes | Must match a loaded Soul's `id`. Use endpoint 19 to list available IDs. |

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "soul_id": "default",
  "version": 1
}
```

**Errors**: 401, 403, 400 (soul_id not found in loaded Souls)

---

### 22 · DELETE /local-admin/nya/users/{userId}/facts/{factKey}

**Purpose**: Hard-delete a single fact key (all versions) for a user. Use when the LLM extracted an incorrect fact and only that specific key needs to be removed, without nuking all facts.

**Path Params**: `userId`, `factKey` (e.g. `food_preference`)

**Response `data`**
```json
{
  "user_id": "Uabc123...",
  "fact_key": "food_preference",
  "deleted_versions": 2
}
```

**Errors**: 401, 403, 404 (factKey not found for user)

---

## Quick Reference Table

| # | Method | Path | Purpose |
|---|--------|------|---------|
| 01 | GET | `/users` | List all users with message data (supports `keyword` search + `offset`) |
| 02 | GET | `/users/{userId}/overview` | All stats for one user in one call |
| 03 | GET | `/users/{userId}/messages` | Paginated message list (supports `limit` + `offset`) |
| 04 | GET | `/users/{userId}/messages/{messageId}` | Single message full content |
| 05 | DELETE | `/users/{userId}/messages` | Soft-delete all messages (or selective via `?message_ids=`) |
| 06 | POST | `/users/{userId}/topics` | Create a topic segment |
| 07 | GET | `/users/{userId}/facts` | List active facts |
| 08 | POST | `/users/{userId}/facts` | Manually create/overwrite a fact |
| 09 | GET | `/users/{userId}/facts/{factKey}/history` | Fact version history |
| 10 | POST | `/users/{userId}/facts/{factKey}/rollback` | Roll back a fact |
| 11 | DELETE | `/users/{userId}/facts` | Hard-delete all facts |
| 12 | GET | `/users/{userId}/summaries` | List summary versions |
| 13 | POST | `/users/{userId}/summaries/{summaryId}/rollback` | Roll back a summary |
| 14 | DELETE | `/users/{userId}/summaries` | Hard-delete all summaries (resets is_summarized) |
| 15 | GET | `/users/{userId}/audit` | Per-user audit log (supports `limit` + `offset`) |
| 16 | GET | `/audit` | Cross-user audit query (supports `limit` + `offset`; includes `UserId` per log) |
| 17 | DELETE | `/users/{userId}` | Nuke all user memory |
| 18 | GET | `/status` | Engine status and config |
| 19 | GET | `/souls` | List loaded Souls |
| 20 | POST | `/souls/reload` | Hot-reload Soul files |
| 21 | POST | `/users/{userId}/soul` | Bind Soul to user |
| 22 | DELETE | `/users/{userId}/facts/{factKey}` | Hard-delete a single fact key (all versions) |
