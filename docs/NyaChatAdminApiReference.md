# NyaChat Admin API Reference

All endpoints are under `/api/v1/local-admin/nya/`. Headings below omit the `/api/v1` prefix.

Source of truth: `packages/csharp/broker/Endpoints/NyaChatEndpoints.cs` (the `/local-admin/nya` `MapGroup`, mapped from `Program.cs` via `NyaChatEndpoints.Map(api)`).

## Authentication

The group uses `BrokerAuthPolicy.LocalAdminSession`: the broker auth middleware passes the request through and the NyaChat group filter authenticates it against the broker's own local-admin operator accounts (`LocalAdminAuthService`). NyaChat has no separate admin login or role store.

- Requests must come from **localhost**. A non-local request gets a bare `403` (no envelope).
- A valid local-admin session cookie is required. Cookie name: `b4a_local_admin`; obtain via `POST /api/v1/local-admin/login`. Missing, expired or revoked session: `401` with message `Admin login required.`
- Request and response bodies are plain JSON (the `/api/v1/local-admin/` prefix is a plaintext-trusted path in `EncryptionMiddleware`; no encryption envelope).

### Route authorization

Every request first passes `TryResolveRouteAuthorization(method, path)`. It maps each method + path shape to either a list of broker permissions (the session must hold **any one** of them) or to **super_admin only** (the session's operator role must be `super_admin`; permissions are not consulted).

- A method/path combination that the resolver does not know is rejected with `403` `Forbidden: unknown NyaChat admin route.` before any handler runs. This includes every role/capability route (see "Role administration (removed)").
- Missing permission: `403` `Forbidden: permission '<p>' required.` or `Forbidden: one of [<p1>, <p2>] required.`
- Not super_admin on a super-admin route: `403` `Forbidden: super_admin role required.`

Permission shorthands used in the **Auth** line of each endpoint:

| Shorthand | Requirement |
|-----------|-------------|
| `user.read` | any of `permission.user.manage`, `audit.read` |
| `user.write` | `permission.user.manage` |
| `audit` | `audit.read` |
| `sys.read` | any of `system.status.read`, `audit.read` |
| `sys.status` | `system.status.read` |
| `sys.write` | `system.worker.manage` |
| `super_admin` | operator role `super_admin` |

Default broker role grants (`LocalAdminPermissions.ForRole`): `super_admin` has everything; `system_admin` has `system.status.read`, `system.worker.manage`, `audit.read` (among others); `permission_admin` has `permission.user.manage`, `audit.read` (among others); `auditor` and any other role have `system.status.read`, `audit.read` (among others).

### Side effects of the group filter

- Every authorized request opens an admin-operation audit scope tagged with the operator's username and role.
- Any non-GET request whose route contains `{userId}` takes a per-user memory lock for the duration of the request (writes for the same user are serialized).
- A `NyaMemoryException` thrown by a handler is returned as `400` with a non-standard body: `{ "success": false, "message": "...", "error": { "code": "...", "field": "..." } }`.

## Standard Response Envelope

Responses use the broker `ApiResponse<T>` wrapper, serialized with the default ASP.NET Core web JSON options (camelCase property names):

```json
{
  "success": true,
  "message": "ok",
  "data": { },
  "errorCode": null,
  "traceId": "<activity id or uuid>"
}
```

On error `success` is `false`, `data` is `null` and `errorCode` is the code passed to `ApiResponseHelper.Error` (defaults to `400`); it always matches the HTTP status of the response.

**Field naming inside `data`:** handlers build anonymous objects with explicit snake_case names (`user_id`, `created_at`, ...), which are emitted as written. Where a handler returns a C# model (turns, turn work, hide references, selections, LLM catalogs, tool parameters), it is serialized with a snake_case naming policy so these endpoints follow the same convention (`turn_id`, `forget_id`, `is_active`, ...). Model properties that hold raw JSON text keep a `_json` suffix (`call_json`, `note_ids_json`). The one deliberate exception is `GET /config` (and the `config` echoed by `PATCH`/`PUT /config`), which uses the PascalCase configuration keys that `PATCH /config` accepts and `GET /config/effective` reports as `key`.

Exceptions to the envelope:
- Non-localhost: bare `403`.
- `NyaMemoryException` (see above).
- Memory-tool endpoints (16, 19, 20) on tool failure: `400` `{ "success": false, "message": "<tool error message>", "data": <tool envelope> }`.
- Endpoint 14 returns an empty-body `404` when the turn does not exist.

All field schemas below describe the shape of `data` only. Errors 401/403 from authentication and route authorization apply to every endpoint and are not repeated.

---

## Users & Messages

---

### 01 · GET /local-admin/nya/users

**Auth**: `user.read`

**Purpose**: List users who have conversation history. Dashboard entry point.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Max users per page |
| offset | int | no | 0 | Pagination offset |
| keyword | string | no | (none) | Filter by user ID |

**Response `data`**
```json
{
  "total": 12,
  "limit": 50,
  "offset": 0,
  "keyword": null,
  "users": [
    {
      "user_id": "line:Uabc123...",
      "message_count": 48,
      "active_note_count": 7,
      "last_message_at": "2026-05-28T10:30:00Z",
      "soul_id": "default",
      "bound_soul_id": null,
      "effective_soul_id": "default",
      "soul_reason": "configured_default"
    }
  ]
}
```

**Field notes**
- `soul_id` and `effective_soul_id` are the same value (the resolved Soul).
- `soul_reason` is one of `bound`, `configured_default`, `bound_soul_missing`, `configured_default_missing`, `bound_and_default_missing`.

**Errors**: none beyond auth

---

### 02 · GET /local-admin/nya/users/{userId}/overview

**Auth**: `user.read` (`last_turn` is populated only when the session also holds `audit.read`)

**Purpose**: One-shot stats for a single user for the user-detail page.

**Path Params**: `userId`

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "active_topic": { "topic_id": "nyat_...", "title": "Chat 1" },
  "available_souls": [ { "soul_id": "default", "display_name": "NYA" } ],
  "forgotten_count": 1,
  "last_turn": { "trace_id": "...", "created_at": "2026-05-28T10:30:00Z", "detail_json": "{...}" },
  "auto_summarize_enabled": false,
  "short_term_message_limit": 200,
  "soul_id": "default",
  "bound_soul_id": null,
  "effective_soul_id": "default",
  "soul_reason": "configured_default",
  "messages": { "total": 52, "active": 20, "summarized": 28, "deleted": 4, "recent_history": 18 },
  "notes": { "total_active": 6, "categories": { "...": 2 } },
  "summaries": { "total_versions": 3, "active_count": 1, "latest_covered_to_seq": 28 },
  "last_message_at": "2026-05-28T10:30:00Z",
  "last_note_updated_at": "2026-05-28T10:31:00Z"
}
```

**Field notes**
- `active_topic` is `null` when the user has no active topic; `last_turn` is `null` without `audit.read` or when no `chat_input` audit row exists.
- `forgotten_count` = number of distinct active forget operations (hide references).
- `messages.recent_history` = number of messages the prompt builder would currently inject for the active topic (0 when there is no active topic).

**Errors**: 500 (overview query failed)

---

### 03 · DELETE /local-admin/nya/users/{userId}

**Auth**: `user.write`

**Purpose**: Clear all memory for a user: soft-delete messages, delete summaries and topics, and hard-delete turn memory (turns, turn work, selections, history cursors, hide references, all note versions). The Soul binding is kept.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| confirm | string | yes | (none) | Must be `all` |

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "soft_deleted_messages": 48,
  "deleted_summaries": 3,
  "deleted_topics": 2,
  "deleted_note_versions": 9
}
```

**Errors**: 400 (missing `confirm=all`)

---

### 04 · GET /local-admin/nya/users/{userId}/messages

**Auth**: `user.read`

**Purpose**: Paginated list of a user's messages, with per-message diagnostics on why a message is or is not in the current prompt history. Content is truncated to 100 chars; use 05 for full content.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Max rows |
| offset | int | no | 0 | Pagination offset |
| topic_id | string | no | (none) | Only messages of this topic |
| unassigned | bool | no | false | Only messages with no topic |

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "total": 48,
  "limit": 50,
  "offset": 0,
  "topic_id": null,
  "external_conversations": ["..."],
  "messages": [
    {
      "message_id": "nyam_abc...",
      "role": "user",
      "content_preview": "...",
      "sequence": 42,
      "topic_id": "nyat_...",
      "origin_topic_id": "nyat_...",
      "turn_id": "...",
      "turn_status": "completed",
      "failure_reason": null,
      "scope": "normal",
      "is_deleted": false,
      "is_summarized": false,
      "metadata_json": "{...}",
      "exclusion_reasons": ["..."],
      "created_at": "2026-05-28T10:30:00Z"
    }
  ]
}
```

**Field notes**
- `turn_status` / `failure_reason` come from the message's turn (`pending`, `completed`, `failed`) and are `null` when the turn is unknown.
- `exclusion_reasons` comes from the memory service's history diagnostics for that message.

**Errors**: none beyond auth

---

### 05 · GET /local-admin/nya/users/{userId}/messages/{messageId}

**Auth**: `user.read`

**Purpose**: Retrieve a single message with full untruncated content and raw `metadata_json`.

**Path Params**: `userId`, `messageId`

**Response `data`**
```json
{
  "message_id": "nyam_abc...",
  "user_id": "line:Uabc123...",
  "role": "user",
  "content": "...",
  "sequence": 42,
  "topic_id": "nyat_...",
  "origin_topic_id": "nyat_...",
  "turn_id": "...",
  "turn_status": "completed",
  "failure_reason": null,
  "scope": "normal",
  "is_deleted": false,
  "is_summarized": false,
  "metadata_json": "{...}",
  "created_at": "2026-05-28T10:30:00Z"
}
```

**Errors**: 404 (message not found for this user)

---

### 06 · DELETE /local-admin/nya/users/{userId}/messages

**Auth**: `user.write`

**Purpose**: Soft-delete messages. With `message_ids`, only those messages (at most the first 50 are processed); otherwise all messages, which requires `confirm=all`.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| message_ids | string | no | (none) | Comma-separated message IDs |
| confirm | string | when `message_ids` absent | (none) | Must be `all` for a mass delete |

**Response `data`** (selective)
```json
{ "user_id": "line:Uabc123...", "soft_deleted": 2, "selective": true, "requested_ids": 2 }
```

**Response `data`** (all)
```json
{ "user_id": "line:Uabc123...", "soft_deleted": 48, "selective": false }
```

**Errors**: 400 (no `message_ids` and missing `confirm=all`)

---

### 07 · POST /local-admin/nya/users/{userId}/messages/soft-delete

**Auth**: `user.write`

**Purpose**: Body-based selective soft-delete (used by the admin UI). Non-string and blank entries are ignored, IDs are de-duplicated and capped at 50.

**Request Body**
```json
{ "message_ids": ["nyam_1", "nyam_2"] }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| message_ids | string[] | yes | Message IDs to soft-delete |

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "soft_deleted": 2, "selective": true, "requested_ids": 2 }
```

**Errors**: 400 (`message_ids` missing / not an array / empty after filtering)

---

## Topics & Turns

---

### 08 · GET /local-admin/nya/users/{userId}/topics

**Auth**: `user.read`

**Purpose**: List the user's topics (chat rooms) with message counts.

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "total": 2,
  "topics": [
    { "topic_id": "nyat_...", "title": "Chat 1", "is_active": true, "msg_count": 20, "created_at": "2026-05-28T10:00:00Z" }
  ]
}
```

**Errors**: none beyond auth

---

### 09 · POST /local-admin/nya/users/{userId}/topics

**Auth**: `user.write`

**Purpose**: Create a new topic for the user (it becomes the active topic per `CreateNewTopic`).

**Request Body**
```json
{ "title": "Travel plans" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| title | string | no | Topic title; the service picks a default when omitted |

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "topic_id": "nyat_...", "title": "Travel plans", "is_active": true, "created_at": "2026-05-28T10:00:00Z" }
```

**Errors**: 400 (`NyaMemoryException`: `title_length` over 128 chars, `topic_capacity` when `TopicCapacity` is reached)

---

### 10 · POST /local-admin/nya/users/{userId}/topics/{topicId}/switch

**Auth**: `user.write`

**Purpose**: Make `topicId` the user's active topic. Audited as a topic switch from the previous active topic.

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "topic_id": "nyat_...", "title": "Chat 1", "is_active": true, "created_at": "2026-05-28T10:00:00Z" }
```

**Errors**: 404 (topic not found for user)

---

### 11 · POST /local-admin/nya/users/{userId}/topics/{topicId}/rename

**Auth**: `user.write`

**Request Body**
```json
{ "title": "New title" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| title | string | yes | Non-empty new title |

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "topic_id": "nyat_...", "title": "New title", "is_active": true, "created_at": "2026-05-28T10:00:00Z" }
```

**Errors**: 400 (missing/empty `title`; `NyaMemoryException` `title_length` over 128 chars), 404 (topic not found for user)

---

### 12 · DELETE /local-admin/nya/users/{userId}/topics/{topicId}

**Auth**: `user.write`

**Purpose**: Delete one topic.

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "topic_id": "nyat_...", "deleted": true }
```

**Errors**: 404 (topic not found for user)

---

### 13 · GET /local-admin/nya/users/{userId}/turns

**Auth**: `user.read`

**Purpose**: List the user's chat turns, newest first. Failed turns keep their user message.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Clamped to 1-100 |
| offset | int | no | 0 | Clamped to >= 0 |

**Response `data`** (model `NyaTurn`, snake_case)
```json
{
  "turns": [
    {
      "turn_id": "...",
      "user_id": "line:Uabc123...",
      "scope": "normal",
      "conversation_id": null,
      "start_topic_id": "nyat_...",
      "current_topic_id": "nyat_...",
      "status": "completed",
      "failure_reason": null,
      "created_at": "2026-05-28T10:30:00Z",
      "finished_at": "2026-05-28T10:30:04Z"
    }
  ]
}
```

**Errors**: none beyond auth

---

### 14 · GET /local-admin/nya/users/{userId}/turns/{turnId}/work

**Auth**: `user.read`

**Purpose**: Tool-call work records of one turn, in order, with a computed hidden flag (hidden when the work item, a dependency message/note, or a dependency's turn is covered by an active hide reference).

**Response `data`** (model `NyaTurnWork`, snake_case)
```json
{
  "work": [
    {
      "work_id": "nyaw_...",
      "turn_id": "...",
      "position": 0,
      "call_json": "{...}",
      "result_json": "{...}",
      "effects_json": "[...]",
      "dependencies_json": "[...]",
      "is_hidden": false
    }
  ]
}
```

**Errors**: 404 (turn not found for user)

---

## Notes & Memory

---

### 15 · GET /local-admin/nya/users/{userId}/notes

**Auth**: `user.read`

**Purpose**: All note versions for the user (current, old and expired), newest first, with the configured capacity limits.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| limit | int | no | 50 | Clamped to 1-100 |
| offset | int | no | 0 | Clamped to >= 0 |

**Response `data`**
```json
{
  "total": 9,
  "limit": 50,
  "offset": 0,
  "capacity": 32,
  "max_length": 512,
  "notes": [
    {
      "note_id": "...",
      "key": "food_preference",
      "content": "...",
      "version": 2,
      "is_current": true,
      "scope": "normal",
      "conversation_id": null,
      "expires_at": null,
      "expired": false,
      "hidden": false,
      "source_message_ids": ["nyam_..."],
      "created_at": "2026-05-28T10:31:00Z"
    }
  ]
}
```

**Field notes**: `total` counts all versions; `hidden` is true when an active hide reference targets the note or this version.

**Errors**: none beyond auth

---

### 16 · POST /local-admin/nya/users/{userId}/notes

**Auth**: `user.write`

**Purpose**: Save a note by running the `note_save` memory tool on the user's behalf (same validation and token budget as the chat model). Use `key` to add, `note_id` to correct an existing note; the two are mutually exclusive.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| scope | string | no | `normal` | `normal` or `external` |
| conversation_id | string | when `scope=external` | (none) | External conversation ID |

**Request Body** (tool arguments)
```json
{
  "key": "food_preference",
  "content": "Likes ramen",
  "expiry": { "mode": "on_date", "date": "2026-12-31" },
  "source_message_ids": ["nyam_..."]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| key | string (<=128) | one of `key` / `note_id` | New note key |
| note_id | string (<=128) | one of `key` / `note_id` | Existing note to correct |
| content | string (<=100000) | yes | Note text (also bounded by `NoteMaxLength`) |
| expiry | object | no | `mode`: `keep` (default), `never`, `on_date` (+ `date` `YYYY-MM-DD`, required for `on_date`) |
| source_message_ids | string[] (1-100) | no | Source messages |

**Response `data`** (the tool envelope)
```json
{
  "ok": true,
  "outcome": "completed",
  "error": null,
  "data": { "note_id": "...", "key": "food_preference", "version": 1, "expires_at": "...", "source_message_ids": ["nyam_..."] },
  "effects": [ { "action": "note_save", "note_id": "...", "version": 1 } ]
}
```

**Errors**: 400 (tool error: `{ success: false, message, data: <envelope with error.code/field/message> }`), 400 (`NyaMemoryException` for invalid `scope` / missing `conversation_id`)

---

### 17 · POST /local-admin/nya/users/{userId}/notes/{noteId}/restore

**Auth**: `user.write`

**Purpose**: Restore an older version of a note as the current version. Audited as `note_restore`.

**Query Params**: `scope`, `conversation_id` (as in 16)

**Request Body**
```json
{ "version": 1 }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| version | int | yes | Version number to restore |

**Response `data`** (tool envelope)
```json
{
  "ok": true,
  "outcome": "completed",
  "error": null,
  "data": { "note_id": "...", "version": 3, "expires_at": null },
  "effects": []
}
```

**Errors**: 400 (`version` not an integer), 400 (`NyaMemoryException`, e.g. `note_not_found`, `note_hidden` - revoke the hide reference first, key conflict with another current note, capacity)

---

### 18 · GET /local-admin/nya/users/{userId}/selections

**Auth**: `user.read`

**Purpose**: List forget-candidate selections created by `forget_find` (endpoint 19 or the chat model), newest expiry first. Selections expire 15 minutes after creation.

**Query Params**: `limit` (default 50, clamped 1-100), `offset` (default 0)

**Response `data`** (model `NyaMemorySelection`, snake_case)
```json
{
  "selections": [
    {
      "selection_id": "nyas_...",
      "user_id": "line:Uabc123...",
      "scope": "normal",
      "conversation_id": null,
      "visibility_version": 12,
      "note_ids_json": "[\"...\"]",
      "message_ids_json": "[\"nyam_...\"]",
      "expires_at": "2026-05-28T10:45:00Z"
    }
  ]
}
```

**Errors**: none beyond auth

---

## Forget

---

### 19 · POST /local-admin/nya/users/{userId}/forget-find

**Auth**: `user.write`

**Purpose**: Run the `forget_find` memory tool: find candidate notes and messages to forget and store them as a selection. Nothing is hidden yet.

**Query Params**: `scope`, `conversation_id` (as in 16)

**Request Body** (tool arguments; `terms` or `note_ids` required)
```json
{ "terms": ["ramen"], "scope": "all", "match": "any" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| terms | string[] (1-100) | one of `terms` / `note_ids` | Search terms |
| note_ids | string[] (1-100) | one of `terms` / `note_ids` | Notes to forget (with their source messages) |
| scope | string | no | `all` or `current` (current topic only) |
| match | string | no | `any` (default) or `all` |

**Response `data`** (tool envelope, `outcome` = `needs_confirmation`)
```json
{
  "ok": true,
  "outcome": "needs_confirmation",
  "error": null,
  "data": {
    "selection_id": "nyas_...",
    "expires_at": "2026-05-28T10:45:00Z",
    "notes": [ { "note_id": "...", "version": 2, "preview": "...", "source_message_ids": ["nyam_..."] } ],
    "messages": [ { "message_id": "nyam_...", "topic_id": "nyat_...", "role": "user", "preview": "..." } ],
    "rule": "..."
  },
  "effects": []
}
```

**Errors**: 400 (tool error envelope), 400 (`NyaMemoryException` for scope)

---

### 20 · POST /local-admin/nya/users/{userId}/forget

**Auth**: `user.write`

**Purpose**: Run the `forget` memory tool: hide a selection, or explicit notes/messages, together with their replies and related work records (hide references; nothing is deleted).

**Query Params**: `scope`, `conversation_id` (as in 16)

**Request Body** (tool arguments)
```json
{ "selection_id": "nyas_..." }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| selection_id | string | one of the three | Selection from 19; cannot be combined with the ID lists |
| note_ids | string[] | one of the three | Notes to hide |
| message_ids | string[] | one of the three | Messages to hide |

**Response `data`** (tool envelope; `outcome` is `noop` when nothing matched)
```json
{
  "ok": true,
  "outcome": "completed",
  "error": null,
  "data": { "forget_id": "...", "note_ids": ["..."], "message_ids": ["nyam_..."], "note_count": 1, "message_count": 3 },
  "effects": [ { "action": "forget", "forget_id": "...", "note_count": 1, "message_count": 3 } ]
}
```

**Errors**: 400 (tool error envelope, e.g. `required`, `exclusive`), 400 (`NyaMemoryException` for scope)

---

### 21 · GET /local-admin/nya/users/{userId}/hide-references

**Auth**: `user.read`

**Purpose**: All hide references (active and revoked) created by forget operations, newest first.

**Response `data`** (model `NyaHideReference`, snake_case)
```json
{
  "references": [
    {
      "reference_id": "...",
      "forget_id": "...",
      "user_id": "line:Uabc123...",
      "scope": "normal",
      "conversation_id": null,
      "kind": "message",
      "target_id": "nyam_...",
      "is_active": true,
      "created_at": "2026-05-28T10:40:00Z"
    }
  ]
}
```

**Field notes**: `kind` values seen in code: `note`, `note_version`, `message`, `turn`, `work`.

**Errors**: none beyond auth

---

### 22 · POST /local-admin/nya/users/{userId}/hide-references/{forgetId}/unforget

**Auth**: `user.write`

**Purpose**: Revoke all active hide references of one forget operation (within the scope recorded on the reference, respecting the note token budget). Audited as `unforget`.

**Request Body**: none

**Response `data`**
```json
{ "changed": true }
```

`changed` is `false` (still `200`) when no active reference with that `forgetId` exists.

**Errors**: 400 (`NyaMemoryException`, e.g. restoring would exceed note capacity/budget)

---

## Summaries

---

### 23 · GET /local-admin/nya/users/{userId}/summaries

**Auth**: `user.read`

**Purpose**: All summary versions for the user, with per-topic counts of not-yet-summarized messages.

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "total": 3,
  "trigger_count": 12,
  "auto_summarize_enabled": false,
  "topic_active": { "nyat_...": 8 },
  "summaries": [
    {
      "summary_id": "...",
      "version": 3,
      "is_active": true,
      "topic_id": "nyat_...",
      "topic_title": "Chat 1",
      "source_message_ids": ["nyam_..."],
      "visibility_version": 12,
      "scope": "normal",
      "conversation_id": null,
      "invalidated": false,
      "summary_text": "...",
      "summary_preview": "...",
      "covered_from_seq": 1,
      "covered_to_seq": 28,
      "message_count": 28,
      "created_at": "2026-05-28T10:30:00Z",
      "superseded_at": null
    }
  ]
}
```

**Field notes**: `summary_preview` is the first 150 chars; `invalidated` is true when the summary's sources were later hidden or deleted.

**Errors**: none beyond auth

---

### 24 · POST /local-admin/nya/users/{userId}/summaries/trigger

**Auth**: `user.write`

**Purpose**: Force a summarization pass on the user's active topic (creating one if needed).

**Request Body**: none

**Response `data`** (auto-summarize disabled)
```json
{ "user_id": "line:Uabc123...", "triggered": false, "status": "Disabled", "message": "..." }
```

**Response `data`** (otherwise)
```json
{
  "user_id": "line:Uabc123...",
  "triggered": true,
  "topic_id": "nyat_...",
  "status": "Created",
  "new_version": 4,
  "covered_to_seq": 36,
  "message": "..."
}
```

**Field notes**: `status` is the `NyaSummarizeStatus` name (`Created`, `NotEnoughMessages`, `LlmEmpty`, or an error status); `new_version` and `covered_to_seq` are `null` unless `triggered`.

**Errors**: none beyond auth (summarizer failures are reported through `status`)

---

### 25 · POST /local-admin/nya/users/{userId}/summaries/{summaryId}/rollback

**Auth**: `user.write`

**Purpose**: Re-activate an inactive summary version. Refused when the target is already active or any of its source messages is hidden.

**Request Body**: none

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "summary_id": "...", "rolled_to_version": 2, "covered_from_seq": 1, "covered_to_seq": 20 }
```

**Errors**: 404 (summary not found or cannot be rolled back)

---

### 26 · DELETE /local-admin/nya/users/{userId}/summaries

**Auth**: `user.write`

**Purpose**: Hard-delete all summaries and reset `is_summarized` on the user's non-deleted messages.

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "deleted": 3 }
```

**Errors**: none beyond auth

---

## Audit & Traces

---

### 27 · GET /local-admin/nya/audit

**Auth**: `audit`

**Purpose**: Cross-user audit log query.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| user_id | string | no | (none) | Filter by user |
| action | string | no | (none) | Filter by action |
| result | string | no | (none) | Filter by result |
| keyword | string | no | (none) | Keyword search |
| limit | int | no | 50 | Max rows |
| offset | int | no | 0 | Pagination offset |

**Response `data`**
```json
{
  "user_id": null,
  "total": 120,
  "limit": 50,
  "offset": 0,
  "write_failure_count": 0,
  "keyword": null,
  "logs": [
    {
      "user_id": "line:Uabc123...",
      "log_id": "...",
      "trace_id": "...",
      "action": "chat_input",
      "result": "success",
      "detail_json": "{...}",
      "created_at": "2026-05-28T10:30:00Z"
    }
  ]
}
```

**Field notes**: `write_failure_count` is the audit logger's count of failed audit writes since startup.

**Errors**: none beyond auth

---

### 28 · GET /local-admin/nya/users/{userId}/audit

**Auth**: `audit`

**Purpose**: Audit log for one user.

**Query Params**: `action`, `result`, `keyword`, `limit` (default 50), `offset` (default 0) - as in 27, without `user_id`.

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "total": 40,
  "write_failure_count": 0,
  "limit": 50,
  "offset": 0,
  "keyword": null,
  "logs": [
    { "log_id": "...", "trace_id": "...", "action": "chat_input", "result": "success", "detail_json": "{...}", "created_at": "2026-05-28T10:30:00Z" }
  ]
}
```

**Errors**: none beyond auth

---

### 29 · GET /local-admin/nya/audit/traces/{traceId}

**Auth**: `audit`

**Purpose**: All audit rows that share one trace ID (one chat turn or admin operation), across users.

**Response `data`**
```json
{
  "trace_id": "...",
  "logs": [
    { "user_id": "line:Uabc123...", "log_id": "...", "trace_id": "...", "action": "chat_input", "result": "success", "detail_json": "{...}", "created_at": "2026-05-28T10:30:00Z" }
  ]
}
```

**Errors**: none beyond auth (unknown trace returns an empty `logs` list)

---

### 30 · DELETE /local-admin/nya/audit/{logId}

**Auth**: `super_admin`

**Purpose**: Delete a single audit row. The purge itself is audited.

**Response `data`**
```json
{ "log_id": "...", "deleted": 1 }
```

**Errors**: 404 (log not found)

---

### 31 · DELETE /local-admin/nya/users/{userId}/audit

**Auth**: `super_admin`

**Purpose**: Delete a user's audit rows, optionally filtered. The purge is audited.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| confirm | string | yes | (none) | Must be `all` |
| action | string | no | (none) | Only rows with this action |
| keyword | string | no | (none) | Only rows matching keyword |
| result | string | no | (none) | Only rows with this result |

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "deleted": 40 }
```

**Errors**: 400 (missing `confirm=all`)

---

### 32 · DELETE /local-admin/nya/audit

**Auth**: `super_admin`

**Purpose**: Delete audit rows across users, optionally filtered (`all` purge when unfiltered). The purge is audited.

**Query Params**: `confirm` (required, `all`), `user_id`, `action`, `keyword`, `result` (all optional filters)

**Response `data`**
```json
{ "deleted": 120 }
```

**Errors**: 400 (missing `confirm=all`)

---

## Souls

---

### 33 · GET /local-admin/nya/souls

**Auth**: `sys.read`

**Response `data`**
```json
{ "total": 1, "souls": [ { "soul_id": "default", "display_name": "NYA" } ] }
```

**Errors**: none beyond auth

---

### 34 · GET /local-admin/nya/souls/{soulId}

**Auth**: `sys.read`

**Purpose**: Full Soul definition plus the users explicitly bound to it.

**Response `data`**
```json
{
  "soul_id": "default",
  "bound_users": ["line:Uabc123..."],
  "display_name": "NYA",
  "personality": { "tone": "...", "language": "...", "greeting_style": "...", "humor_level": "...", "formality": "..." },
  "rules": ["..."],
  "forbidden": ["..."]
}
```

**Errors**: 404 (soul not found)

---

### 35 · POST /local-admin/nya/souls

**Auth**: `sys.write`

**Purpose**: Create a Soul.

**Request Body**
```json
{
  "soul_id": "friendly",
  "display_name": "Friendly NYA",
  "personality": { "tone": "...", "language": "...", "greeting_style": "...", "humor_level": "...", "formality": "..." },
  "rules": ["..."],
  "forbidden": ["..."]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| soul_id | string | yes | 1-64 chars of letters, digits, `_`, `-` |
| display_name | string | yes | Non-empty |
| personality | object | no | String fields `tone`, `language`, `greeting_style`, `humor_level`, `formality` |
| rules | string[] | no | Blank entries dropped |
| forbidden | string[] | no | Blank entries dropped |

**Response `data`**
```json
{ "soul_id": "friendly", "display_name": "Friendly NYA" }
```

**Errors**: 400 (missing/invalid `soul_id` or `display_name`), 409 (soul already exists)

---

### 36 · PUT /local-admin/nya/souls/{soulId}

**Auth**: `sys.write`

**Purpose**: Replace an existing Soul. Body as in 35 (`soul_id` must still be present and valid, but the path `soulId` wins).

**Response `data`**
```json
{ "soul_id": "friendly", "display_name": "Friendly NYA" }
```

**Errors**: 400 (body validation), 404 (soul not found)

---

### 37 · DELETE /local-admin/nya/souls/{soulId}

**Auth**: `sys.write`

**Response `data`**
```json
{ "soul_id": "friendly", "deleted": true }
```

**Errors**: 400 (soul is the configured `DefaultSoulId`), 404 (soul not found)

---

### 38 · POST /local-admin/nya/souls/reload

**Auth**: `sys.write`

**Purpose**: Hot-reload Soul files from disk.

**Response `data`**
```json
{
  "ok": true,
  "loaded": 2,
  "failures": [ { "file": "bad.json", "message": "..." } ],
  "souls": [ { "soul_id": "default", "display_name": "NYA" } ]
}
```

`ok` is `false` when any file failed to load.

**Errors**: none beyond auth

---

### 39 · POST /local-admin/nya/users/{userId}/soul

**Auth**: `user.write`

**Purpose**: Bind a Soul to a user, overriding `DefaultSoulId` for that user.

**Request Body**
```json
{ "soul_id": "default" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| soul_id | string | yes | Must match a loaded Soul (case-insensitive) |

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "soul_id": "default" }
```

**Errors**: 400 (missing `soul_id`, or soul not found)

---

### 40 · DELETE /local-admin/nya/users/{userId}/soul

**Auth**: `user.write` (also checked inside the handler)

**Purpose**: Remove the user's Soul binding; the user falls back to `DefaultSoulId`.

**Response `data`**
```json
{ "user_id": "line:Uabc123...", "removed": true, "bound_soul_id": null, "effective_soul_id": "default", "soul_reason": "configured_default" }
```

**Errors**: none beyond auth

---

## LLM Profiles & Routes

Call sites are `chat`, `summarization` and `__default__` (fallback route).

---

### 41 · GET /local-admin/nya/llm/param-catalog

**Auth**: `sys.status`

**Purpose**: Static catalog of the params allowed in a profile's `params_json` (drives the UI form and validation).

**Response `data`** (array of `NyaLlmParamSpec`, snake_case)
```json
[
  { "key": "temperature", "type": "number", "min": 0, "max": 2, "doc": "...", "common": true, "applies_to": ["*"] }
]
```

Keys: `temperature`, `top_p`, `max_context_tokens`, `timeout_seconds`, `enable_thinking`, `thinking_budget`, `frequency_penalty`, `presence_penalty`, `repeat_penalty`.

**Errors**: none beyond auth

---

### 42 · GET /local-admin/nya/llm/templates

**Auth**: `sys.status`

**Purpose**: Provider presets for creating profiles.

**Response `data`** (array of `NyaLlmProviderTemplate`, snake_case)
```json
[
  {
    "id": "deepseek",
    "label": "DeepSeek",
    "provider": "openai_chat",
    "default_base_url": "https://api.deepseek.com/chat/completions",
    "default_params": { "top_p": 1.0 },
    "default_model": "deepseek-flash",
    "extra_body": { "thinking": { "type": "disabled" } }
  }
]
```

Template IDs: `deepseek`, `chatgpt`, `local` (Ollama), `lmstudio`, `claude`, `gemini`.

**Errors**: none beyond auth

---

### 43 · GET /local-admin/nya/llm/call-sites

**Auth**: `sys.status`

**Response `data`**
```json
{ "call_sites": ["chat", "summarization", "__default__"] }
```

**Errors**: none beyond auth

---

### 44 · GET /local-admin/nya/llm/profiles

**Auth**: `sys.status`

**Purpose**: List profiles with masked keys, the call sites that currently resolve to each, and the effective request settings.

**Response `data`** (array)
```json
[
  {
    "profile_id": "local",
    "provider": "ollama",
    "base_url": "http://localhost:11434/api/chat",
    "api_key": "****",
    "model": "qwen3.6:35b-a3b-Q4",
    "template": "local",
    "params_json": "{\"temperature\":0.7}",
    "extra_body": null,
    "call_sites": ["chat", "__default__"],
    "effective": { "...": "same shape as endpoint 48" }
  }
]
```

**Field notes**: `api_key` is masked (`****` or up to 8 `*` plus the last 4 chars).

**Errors**: none beyond auth

---

### 45 · POST /local-admin/nya/llm/profiles

**Auth**: `super_admin`

**Purpose**: Create a profile.

**Request Body**
```json
{
  "profile_id": "local",
  "provider": "ollama",
  "base_url": "http://localhost:11434/api/chat",
  "api_key": "",
  "model": "qwen3.6:35b-a3b-Q4",
  "template": "local",
  "params_json": "{\"temperature\":0.7}",
  "extra_body": { }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| profile_id | string | yes | 1-80 chars of letters, digits, `_`, `-`; not `__default__` |
| provider | string | effectively yes | Must be a registered provider key (lower-cased) |
| base_url | string | effectively yes | Full absolute http(s) API endpoint with a path, no user info/fragment |
| api_key | string | no | Must not contain `*` (masked value) on create |
| model | string | effectively yes | Non-empty, <= 120 chars |
| template | string | no | Template ID |
| params_json | string | no | JSON object string, default `{}`; keys from the param catalog plus `extra_body` / `model_overrides` |
| extra_body | object | no | Merged into `params_json.extra_body`; may not override `model`, `messages`, `tools`, `stream` |

All string fields must be JSON strings when present.

**Response `data`**
```json
{ "profile_id": "local" }
```

**Errors**: 400 (validation), 409 (profile ID exists)

---

### 46 · PUT /local-admin/nya/llm/profiles/{id}

**Auth**: `super_admin`

**Purpose**: Replace a profile. Body as in 45 without `profile_id` (the path ID is used). Omitted fields become empty / `{}`.

**Response `data`**
```json
{ "profile_id": "local" }
```

**Errors**: 400 (validation), 404 (profile not found)

---

### 47 · DELETE /local-admin/nya/llm/profiles/{id}

**Auth**: `super_admin`

**Response `data`**
```json
{ "profile_id": "local", "deleted": true }
```

**Errors**: 404 (not found), 409 (profile still in use, from the store)

---

### 48 · GET /local-admin/nya/llm/profiles/{id}/effective

**Auth**: `sys.status`

**Purpose**: What the broker will actually send for this profile.

**Response `data`**
```json
{
  "sampling": { "temperature": 0.7, "top_p": 0.9 },
  "extra_body": null,
  "sampling_location": "options",
  "thinking": { },
  "max_context_tokens": null,
  "timeout_seconds": 120,
  "output_limit": "...",
  "precedence": "..."
}
```

**Field notes**: `sampling_location` is `options` (ollama), `generationConfig` (gemini) or `body`; `timeout_seconds` is at least 30.

**Errors**: 404 (profile not found)

---

### 49 · POST /local-admin/nya/llm/profiles/{id}/test

**Auth**: `super_admin`

**Purpose**: Connection test: sends `Reply OK.` with `max_tokens` 32 and a timeout of at most 30 s.

**Request Body**: none

**Response `data`**
```json
{ "ok": true, "latency_ms": 812, "error": null }
```

Test failures (timeout, HTTP error, empty reply) return `200` with `ok: false` and a message in `error`.

**Errors**: 400 (stored profile fails validation), 404 (profile not found)

---

### 50 · GET /local-admin/nya/llm/routes

**Auth**: `sys.status`

**Purpose**: Effective profile per call site.

**Response `data`** (array)
```json
[
  {
    "call_site_id": "summarization",
    "profile_id": null,
    "execution_enabled": false,
    "effective_profile_id": "local",
    "provider": "ollama",
    "model": "qwen3.6:35b-a3b-Q4",
    "source": "__default__"
  }
]
```

**Field notes**: `profile_id` is the direct route (`null` when falling back); `source` is the call site whose route was used; `execution_enabled` is true for `chat`, and for `summarization` only when auto-summarize is on.

**Errors**: none beyond auth

---

### 51 · PUT /local-admin/nya/llm/routes

**Auth**: `super_admin`

**Purpose**: Route a call site to a profile. `profile_id: "__default__"` clears the direct route.

**Request Body**
```json
{ "call_site_id": "chat", "profile_id": "local" }
```

**Response `data`**
```json
{ "call_site_id": "chat", "profile_id": "local" }
```

**Errors**: 400 (body not an object, missing fields, unknown call site, store rejection e.g. unknown profile)

---

## Config

---

### 52 · GET /local-admin/nya/config

**Auth**: `sys.read`

**Purpose**: The full current `NyaChatConfig`, with `ChatApiKey` and every `LlmProfiles[*].ApiKey` masked.

**Response `data`** (model `NyaChatConfig`, PascalCase configuration keys; abbreviated)
```json
{
  "NoteCapacity": 32,
  "NoteMaxLength": 512,
  "TopicCapacity": 64,
  "Enabled": true,
  "ShortTermMessageLimit": 200,
  "AutoSummarizeEnabled": false,
  "SummarizeTriggerCount": 12,
  "ChatApiKey": "****",
  "LlmProfiles": { },
  "DefaultSoulId": "default",
  "AuditEnabled": true
}
```

**Errors**: none beyond auth

---

### 53 · GET /local-admin/nya/config/effective

**Auth**: `sys.read`

**Purpose**: The hot-editable settings with current and default values (drives the settings page).

**Response `data`**
```json
{
  "settings": [
    { "key": "NoteCapacity", "label": "...", "type": "integer", "min": 1, "max": 10000, "description": "...", "value": 32, "default_value": 32 }
  ]
}
```

**Field notes**: `key` uses the PascalCase config property name. Editable keys: `NoteCapacity`, `NoteMaxLength`, `TopicCapacity`, `ShortTermMessageLimit`, `DefaultMaxContextTokens`, `OutputReserveRatio`, `MinOutputTokens`, `MaxOutputTokens`, `ContextSafetyMargin`, `MaxReplyLength`, `MaxToolRounds`, `PerToolResultMaxTokens`, `ToolExecTimeoutSeconds`, `StripMarkdown`, `EnableMultiPartReply`, `DefaultSoulId`, `AuditEnabled`. `type` is `integer`, `number`, `bool` or `string`.

**Errors**: none beyond auth

---

### 54 · PATCH /local-admin/nya/config

**Auth**: `sys.write`

**Purpose**: Hot-update one or more settings. Persisted as overrides and audited as `config_update` with the operator.

**Request Body**: a JSON object of setting keys (case-insensitive) to values.
```json
{ "ShortTermMessageLimit": 100, "StripMarkdown": false }
```

Validation: only the keys listed in 53; duplicate keys rejected; values must match the type and min/max; `string` values must match `^[A-Za-z0-9_-]{1,64}$`; `DefaultSoulId` must name an existing Soul; `MinOutputTokens <= MaxOutputTokens`; `MinOutputTokens + ContextSafetyMargin < DefaultMaxContextTokens`; `SummarizeKeepRecentCount < SummarizeTriggerCount`.

**Response `data`**
```json
{ "changed": ["ShortTermMessageLimit", "StripMarkdown"], "config": { "...": "masked NyaChatConfig as in 52" } }
```

**Errors**: 400 (not an object, empty, non-editable key, invalid value, cross-field rule, unknown `DefaultSoulId`)

---

### 55 · PUT /local-admin/nya/config

**Auth**: `sys.write`

**Purpose**: Alias of 54 (same handler; partial update, not a full replace).

**Errors**: as 54

---

### 56 · POST /local-admin/nya/config/reset

**Auth**: `sys.write`

**Purpose**: Drop all hot-setting overrides, returning them to appsettings defaults. Audited as `config_reset`.

**Request Body**: none

**Response `data`**: same shape as 53.

**Errors**: none beyond auth

---

## Prompts

---

### 57 · GET /local-admin/nya/prompts

**Auth**: `sys.read`

**Response `data`**
```json
{ "total": 3, "templates": [ { "id": "...", "version": "..." } ] }
```

**Errors**: none beyond auth

---

### 58 · POST /local-admin/nya/prompts/reload

**Auth**: `sys.write`

**Purpose**: Reload prompt templates from disk. Audited as `prompt_reload`.

**Response `data`**
```json
{ "ok": true, "loaded": 3, "templates": [ { "id": "...", "version": "..." } ] }
```

**Errors**: none beyond auth

---

## Tools

---

### 59 · GET /local-admin/nya/tools

**Auth**: `sys.read`

**Response `data`**
```json
{
  "total": 4,
  "tools": [
    { "id": "note_save", "name": "note_save", "description": "...", "group": "memory", "required_permission": null, "enabled": true }
  ]
}
```

**Field notes**: `id` and `name` are both the tool schema name.

**Errors**: none beyond auth

---

### 60 · GET /local-admin/nya/tools/availability

**Auth**: any of `system.status.read`, `audit.read`, `permission.user.manage`

**Purpose**: Which tools a given user would get on a given channel, and why the others are unavailable.

**Query Params**
| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| user_id | string | yes | (none) | User ID |
| channel_type | string | yes | (none) | Channel, e.g. `line` |

**Response `data`**
```json
{
  "user_id": "line:Uabc123...",
  "channel_type": "line",
  "usable_count": 3,
  "tools": [
    { "id": "note_save", "enabled": true, "usable": true, "function_tool": true, "reason": null, "parameters": [ ] }
  ]
}
```

**Field notes**: `function_tool` currently always equals `usable`; `parameters` is the tool schema's parameter list (snake_case, e.g. `min_length`).

**Errors**: 400 (missing `user_id` or `channel_type`)

---

### 61 · POST /local-admin/nya/tools/{toolId}/toggle

**Auth**: `sys.write`

**Request Body**
```json
{ "enabled": false }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| enabled | bool | yes | New state |

**Response `data`**
```json
{ "tool_id": "note_save", "enabled": false }
```

**Errors**: 400 (`enabled` missing or not a bool), 404 (unknown tool), 500 (state could not be saved)

---

## Status

---

### 62 · GET /local-admin/nya/status

**Auth**: `sys.read`

**Purpose**: Engine status, routing and key config values.

**Response `data`**
```json
{
  "enabled": true,
  "auto_summarize_enabled": false,
  "task_routing": {
    "chat": { "provider": "ollama", "model": "...", "base_url": "...", "timeout_seconds": 120 },
    "summarization": { "provider": "ollama", "model": "...", "base_url": "...", "timeout_seconds": 90 }
  },
  "profiles": [ { "profile_id": "local", "provider": "ollama", "model": "...", "template": "local" } ],
  "chat_model": "...",
  "chat_provider": "ollama",
  "summarization_model": "...",
  "short_term_message_limit": 200,
  "summarize_trigger_count": 12,
  "summary_version_retention": 5,
  "default_soul_id": "default",
  "available_souls": [ { "soul_id": "default", "display_name": "NYA" } ]
}
```

**Errors**: none beyond auth

---

## Role administration (removed)

Role and capability administration moved back to the broker ("LINE 與使用者" and "權限管理" pages). NyaChat registers no `/roles`, `/roles/{id}`, `/capabilities`, `/users/{userId}/roles` or `/users/{userId}/roles/{roleId}` routes, and `TryResolveRouteAuthorization` still denies those paths for every method with `403` (covered by `RoleRoutes_AreDeniedForEveryMethod`).

---

## Quick Reference Table

| # | Method | Path | Auth | Purpose |
|---|--------|------|------|---------|
| 01 | GET | `/users` | user.read | List users |
| 02 | GET | `/users/{userId}/overview` | user.read | All stats for one user |
| 03 | DELETE | `/users/{userId}` | user.write | Clear all user memory (`?confirm=all`) |
| 04 | GET | `/users/{userId}/messages` | user.read | Paginated messages with diagnostics |
| 05 | GET | `/users/{userId}/messages/{messageId}` | user.read | Single message, full content |
| 06 | DELETE | `/users/{userId}/messages` | user.write | Soft-delete selected (`?message_ids=`) or all (`?confirm=all`) |
| 07 | POST | `/users/{userId}/messages/soft-delete` | user.write | Soft-delete by body ID list |
| 08 | GET | `/users/{userId}/topics` | user.read | List topics |
| 09 | POST | `/users/{userId}/topics` | user.write | Create topic |
| 10 | POST | `/users/{userId}/topics/{topicId}/switch` | user.write | Switch active topic |
| 11 | POST | `/users/{userId}/topics/{topicId}/rename` | user.write | Rename topic |
| 12 | DELETE | `/users/{userId}/topics/{topicId}` | user.write | Delete topic |
| 13 | GET | `/users/{userId}/turns` | user.read | List turns |
| 14 | GET | `/users/{userId}/turns/{turnId}/work` | user.read | Tool work records of a turn |
| 15 | GET | `/users/{userId}/notes` | user.read | All note versions |
| 16 | POST | `/users/{userId}/notes` | user.write | Save note (`note_save` tool) |
| 17 | POST | `/users/{userId}/notes/{noteId}/restore` | user.write | Restore a note version |
| 18 | GET | `/users/{userId}/selections` | user.read | Forget-candidate selections |
| 19 | POST | `/users/{userId}/forget-find` | user.write | Find forget candidates (`forget_find` tool) |
| 20 | POST | `/users/{userId}/forget` | user.write | Hide memory (`forget` tool) |
| 21 | GET | `/users/{userId}/hide-references` | user.read | List hide references |
| 22 | POST | `/users/{userId}/hide-references/{forgetId}/unforget` | user.write | Revoke a forget |
| 23 | GET | `/users/{userId}/summaries` | user.read | List summary versions |
| 24 | POST | `/users/{userId}/summaries/trigger` | user.write | Force summarization |
| 25 | POST | `/users/{userId}/summaries/{summaryId}/rollback` | user.write | Re-activate a summary version |
| 26 | DELETE | `/users/{userId}/summaries` | user.write | Delete all summaries |
| 27 | GET | `/audit` | audit | Cross-user audit query |
| 28 | GET | `/users/{userId}/audit` | audit | Per-user audit log |
| 29 | GET | `/audit/traces/{traceId}` | audit | Audit rows of one trace |
| 30 | DELETE | `/audit/{logId}` | super_admin | Delete one audit row |
| 31 | DELETE | `/users/{userId}/audit` | super_admin | Purge a user's audit rows |
| 32 | DELETE | `/audit` | super_admin | Purge audit rows |
| 33 | GET | `/souls` | sys.read | List Souls |
| 34 | GET | `/souls/{soulId}` | sys.read | Soul detail and bound users |
| 35 | POST | `/souls` | sys.write | Create Soul |
| 36 | PUT | `/souls/{soulId}` | sys.write | Update Soul |
| 37 | DELETE | `/souls/{soulId}` | sys.write | Delete Soul |
| 38 | POST | `/souls/reload` | sys.write | Hot-reload Soul files |
| 39 | POST | `/users/{userId}/soul` | user.write | Bind Soul to user |
| 40 | DELETE | `/users/{userId}/soul` | user.write | Unbind Soul |
| 41 | GET | `/llm/param-catalog` | sys.status | Profile parameter catalog |
| 42 | GET | `/llm/templates` | sys.status | Provider templates |
| 43 | GET | `/llm/call-sites` | sys.status | Call-site IDs |
| 44 | GET | `/llm/profiles` | sys.status | List profiles |
| 45 | POST | `/llm/profiles` | super_admin | Create profile |
| 46 | PUT | `/llm/profiles/{id}` | super_admin | Update profile |
| 47 | DELETE | `/llm/profiles/{id}` | super_admin | Delete profile |
| 48 | GET | `/llm/profiles/{id}/effective` | sys.status | Effective request settings |
| 49 | POST | `/llm/profiles/{id}/test` | super_admin | Connection test |
| 50 | GET | `/llm/routes` | sys.status | Effective routes |
| 51 | PUT | `/llm/routes` | super_admin | Set route |
| 52 | GET | `/config` | sys.read | Full config (masked) |
| 53 | GET | `/config/effective` | sys.read | Editable settings with defaults |
| 54 | PATCH | `/config` | sys.write | Hot-update settings |
| 55 | PUT | `/config` | sys.write | Alias of PATCH |
| 56 | POST | `/config/reset` | sys.write | Reset overrides |
| 57 | GET | `/prompts` | sys.read | List prompt templates |
| 58 | POST | `/prompts/reload` | sys.write | Reload prompt templates |
| 59 | GET | `/tools` | sys.read | List tools |
| 60 | GET | `/tools/availability` | status/audit/user | Per-user tool availability |
| 61 | POST | `/tools/{toolId}/toggle` | sys.write | Enable/disable a tool |
| 62 | GET | `/status` | sys.read | Engine status |
