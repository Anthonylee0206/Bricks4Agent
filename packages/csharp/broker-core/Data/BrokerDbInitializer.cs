using BrokerCore.Models;
using System.Text.Json;

namespace BrokerCore.Data;

/// <summary>
/// 資料庫初始化 —— EnsureTable + 種子資料
/// 啟動時呼叫，確保 17 張表存在並植入種子資料
/// </summary>
public class BrokerDbInitializer
{
    private readonly BrokerDb _db;

    public BrokerDbInitializer(BrokerDb db)
    {
        _db = db;
    }

    /// <summary>初始化所有表結構 + 種子資料</summary>
    public void Initialize(DevelopmentSeedOptions? developmentSeed = null)
    {
        EnsureTables();
        SeedSystemEpoch();
        SeedRoles();
        SeedCapabilities();
        SeedDevelopmentData(developmentSeed);
    }

    /// <summary>建立 19 張表（含向量 + FTS5）</summary>
    private void EnsureTables()
    {
        // ── Phase 1：核心控制平面（12 張） ──
        _db.EnsureTable<Principal>();
        _db.EnsureTable<Role>();
        _db.EnsureTable<RoleBinding>();
        _db.EnsureTable<Capability>();
        _db.EnsureTable<CapabilityGrant>();
        _db.EnsureTable<BrokerTask>();
        _db.EnsureTable<ContainerSession>();
        _db.EnsureTable<ExecutionRequest>();
        _db.EnsureTable<AuditEvent>();
        _db.EnsureTable<TradingApprovalRequest>();
        _db.EnsureTable<PrincipalCapabilityOverride>();
        _db.EnsureTable<HealthScoreSnapshot>();
        _db.EnsureTable<PerpDailyOpenBalance>();
        _db.EnsureTable<SharedContextEntry>();
        _db.EnsureTable<BrowserSiteBinding>();
        _db.EnsureTable<BrowserSessionLease>();
        _db.EnsureTable<BrowserUserGrant>();
        _db.EnsureTable<BrowserSystemBinding>();
        _db.EnsureTable<AzureIisDeploymentTarget>();
        _db.EnsureTable<GoogleDriveOAuthState>();
        _db.EnsureTable<GoogleDriveDelegatedCredential>();
        _db.EnsureTable<LocalAdminCredential>();
        _db.EnsureTable<LocalAdminSession>();
        _db.EnsureTable<Revocation>();
        _db.EnsureTable<SystemEpoch>();

        // ── Phase 4：因果工作流（4 張） + 觀測（1 張） ──
        _db.EnsureTable<Plan>();
        _db.EnsureTable<PlanNode>();
        _db.EnsureTable<PlanEdge>();
        _db.EnsureTable<Checkpoint>();
        _db.EnsureTable<ObservationEvent>();

        // ── 向量嵌入（1 張） ──
        _db.EnsureTable<VectorEntry>();

        // ── Trading / W13 audit / Agent inbox （3 張）— 之前在 Program.cs 才 ensure、
        // 移進來確保 test in-memory DB 也有同一張 schema、避免 dev/test 跟 prod 對不上 ──
        _db.EnsureTable<RiskAnchorState>();
        _db.EnsureTable<LlmReasoningAuditEntry>();
        _db.EnsureTable<AgentInboxTask>();

        EnsureColumns();

        // FTS5 全文檢索虛擬表（BM25）
        EnsureFts5();

        // 額外建立複合唯一約束 + 索引（EnsureTable 不自動建立）
        CreateUniqueConstraints();
    }

    /// <summary>建立 EnsureTable 無法自動產生的唯一約束 + 查詢索引</summary>
    private void CreateUniqueConstraints()
    {
        // ── Phase 1 ──

        // ExecutionRequest: (task_id, idempotency_key) 防重複執行
        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_execution_requests_idempotency
                      ON execution_requests(task_id, idempotency_key)");

        // AuditEvent: (trace_id, trace_seq) per-trace chain 序列化
        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_audit_events_trace_seq
                      ON audit_events(trace_id, trace_seq)");

        // AgentInboxTask: (agent_id, idempotency_key) push 冪等
        //   SQLite 把 NULL 視為相異 → 沒帶 key 的既有/新列彼此不衝突、向後相容;有 key 才強制唯一
        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_inbox_idempotency
                      ON agent_inbox_tasks(agent_id, idempotency_key)");

        // ── Phase 4：因果工作流索引 ──

        // PlanNode: (plan_id, ordinal) 拓撲序查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_plan_nodes_plan
                      ON plan_nodes(plan_id, ordinal)");

        // PlanEdge: plan_id 查詢 + from/to 依賴查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_plan_edges_plan
                      ON plan_edges(plan_id)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_plan_edges_from
                      ON plan_edges(from_node_id)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_plan_edges_to
                      ON plan_edges(to_node_id)");

        // Checkpoint: (plan_id, node_id) 快照查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_checkpoints_plan
                      ON checkpoints(plan_id, node_id)");

        // ── SharedContext 查詢索引 ──

        // (document_id, version) 版本查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_shared_context_doc_ver
                      ON shared_context_entries(document_id, version)");

        // (key, task_id) node output 查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_shared_context_key_task
                      ON shared_context_entries(key, task_id)");

        // ── 向量嵌入索引 ──

        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_vector_entries_source
                      ON vector_entries(source_key, task_id)");

        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_vector_entries_hash
                      ON vector_entries(content_hash, task_id)");

        // 分塊父文件查詢索引
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_vector_entries_parent
                      ON vector_entries(parent_key, task_id)");

        // ── 觀測事件索引 ──

        // trace_id correlation 查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_site_bindings_identity
                      ON browser_site_bindings(identity_mode, site_class, status)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_site_bindings_principal
                      ON browser_site_bindings(principal_id, status)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_session_leases_principal
                      ON browser_session_leases(principal_id, lease_state, expires_at)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_session_leases_site
                      ON browser_session_leases(site_binding_id, lease_state)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_user_grants_principal
                      ON browser_user_grants(principal_id, status, expires_at)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_user_grants_site
                      ON browser_user_grants(site_binding_id, status)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_browser_system_bindings_site
                      ON browser_system_bindings(site_binding_id, status)");
        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_google_drive_oauth_state_token
                      ON google_drive_oauth_states(state_token)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_google_drive_oauth_state_user
                      ON google_drive_oauth_states(channel, user_id, oauth_state, expires_at)");
        TryExecute(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_google_drive_credential_user
                      ON google_drive_delegated_credentials(channel, user_id)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_azure_iis_targets_status
                      ON azure_iis_deployment_targets(status, provider)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_azure_iis_targets_host
                      ON azure_iis_deployment_targets(vm_host, site_name)");
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_local_admin_sessions_expires
                      ON local_admin_sessions(expires_at, revoked_at)");

        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_observations_trace
                      ON observation_events(trace_id)");

        // plan_id correlation 查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_observations_plan
                      ON observation_events(plan_id)");

        // severity + observed_at 告警查詢
        TryExecute(@"CREATE INDEX IF NOT EXISTS idx_observations_severity
                      ON observation_events(severity, observed_at)");
    }

    private void EnsureColumns()
    {
        TryExecute("ALTER TABLE broker_tasks ADD COLUMN runtime_descriptor TEXT DEFAULT '{}'");

        // VectorEntry 新增欄位（智慧分塊 + 多標籤）
        TryExecute("ALTER TABLE vector_entries ADD COLUMN tags TEXT DEFAULT '[]'");
        TryExecute("ALTER TABLE vector_entries ADD COLUMN chunk_index INTEGER DEFAULT 0");
        TryExecute("ALTER TABLE vector_entries ADD COLUMN chunk_total INTEGER DEFAULT 0");
        TryExecute("ALTER TABLE vector_entries ADD COLUMN parent_key TEXT DEFAULT ''");

        // SharedContextEntry 新增 tags 欄位
        TryExecute("ALTER TABLE shared_context_entries ADD COLUMN tags TEXT DEFAULT '[]'");
        TryExecute("ALTER TABLE azure_iis_deployment_targets ADD COLUMN deployment_mode TEXT DEFAULT 'site_root'");
        TryExecute("ALTER TABLE azure_iis_deployment_targets ADD COLUMN application_path TEXT DEFAULT ''");
        TryExecute("ALTER TABLE azure_iis_deployment_targets ADD COLUMN health_check_path TEXT DEFAULT ''");
        TryExecute("ALTER TABLE azure_iis_deployment_targets ADD COLUMN health_check_base_url TEXT DEFAULT ''");

        // TradingApprovalRequest 派發冪等：避免「立刻執行」按一次下一單真錢、按 N 次下 N 單
        TryExecute("ALTER TABLE trading_approval_requests ADD COLUMN dispatched_at TEXT");
        TryExecute("ALTER TABLE trading_approval_requests ADD COLUMN dispatched_by TEXT");

        // AgentInbox push 冪等（2026-05-29）：同 (agent_id, idempotency_key) 重試/重啟接手不重建任務
        TryExecute("ALTER TABLE agent_inbox_tasks ADD COLUMN idempotency_key TEXT");

        // approval_id schema 從 INTEGER 修成 TEXT 的單次 migration。
        // 既有 schema 寫成 INTEGER PRIMARY KEY AUTOINCREMENT、但 model 是 string ApprovalId
        // = IdGen.New("apr") 產生的 "apr_xxx" 字串。INSERT 時 SQLite 把 string coerce 成 NULL
        // → autoincrement 給個 int、變兩個 ID（記憶體一個、DB 一個）的 split-brain。
        // 這個 migration 只在偵測到 INTEGER 型別時跑、跑完就改 TEXT、舊 row 的 int ID 改寫成
        // "apr_legacy_<n>"、新 row 走正常 IdGen.New 流程。
        MigrateApprovalIdToText();
    }

    private void MigrateApprovalIdToText()
    {
        // pragma_table_info('trading_approval_requests') 回 cid/name/type/...、找 approval_id 那欄
        string colType;
        try
        {
            colType = _db.Scalar<string>(
                "SELECT type FROM pragma_table_info('trading_approval_requests') WHERE name = 'approval_id'") ?? "";
        }
        catch
        {
            // 表還不存在（fresh DB、EnsureTable 還沒跑）→ 跳過、這次啟動之後 schema 會走 model 路徑
            return;
        }
        if (string.IsNullOrEmpty(colType)) return;
        if (colType.Equals("TEXT", StringComparison.OrdinalIgnoreCase)) return;  // 已是新 schema

        // SQLite ALTER TABLE 不支援改 column 型別、走 rename + create + copy + drop
        TryExecute("ALTER TABLE trading_approval_requests RENAME TO trading_approval_requests_legacy_int");
        TryExecute(@"
            CREATE TABLE trading_approval_requests (
                approval_id TEXT PRIMARY KEY,
                trace_id TEXT NOT NULL,
                capability_id TEXT NOT NULL,
                route TEXT,
                payload TEXT,
                principal_id TEXT,
                role TEXT,
                requested_at TEXT,
                status TEXT NOT NULL,
                decided_by TEXT,
                decided_at TEXT,
                decision_reason TEXT,
                dispatched_at TEXT,
                dispatched_by TEXT
            )");
        TryExecute(@"
            INSERT INTO trading_approval_requests (
                approval_id, trace_id, capability_id, route, payload,
                principal_id, role, requested_at, status, decided_by, decided_at,
                decision_reason, dispatched_at, dispatched_by
            )
            SELECT
                'apr_legacy_' || approval_id, trace_id, capability_id, route, payload,
                principal_id, role, requested_at, status, decided_by, decided_at,
                decision_reason, dispatched_at, dispatched_by
            FROM trading_approval_requests_legacy_int");
        TryExecute("DROP TABLE trading_approval_requests_legacy_int");
    }

    /// <summary>
    /// 建立 FTS5 全文檢索虛擬表（BM25 排序）
    /// - memory_fts：智慧記憶全文索引
    /// - convlog_fts：對話日誌全文索引
    /// unicode61 tokenizer 會將 CJK 字元逐字拆分，適合中文搜尋
    /// </summary>
    private void EnsureFts5()
    {
        // 智慧記憶全文索引
        TryExecute(@"CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
            source_key,
            content,
            task_id UNINDEXED,
            tokenize='unicode61 remove_diacritics 2'
        )");

        // 對話日誌全文索引
        TryExecute(@"CREATE VIRTUAL TABLE IF NOT EXISTS convlog_fts USING fts5(
            user_id,
            role UNINDEXED,
            content,
            task_id UNINDEXED,
            tokenize='unicode61 remove_diacritics 2'
        )");
    }

    /// <summary>初始化 SystemEpoch（僅一行，epoch=1）</summary>
    private void SeedSystemEpoch()
    {
        var existing = _db.Get<SystemEpoch>(1);
        if (existing == null)
        {
            _db.Insert(new SystemEpoch
            {
                EpochId = 1,
                CurrentEpoch = 1,
                UpdatedBy = "system"
            });
        }
    }

    /// <summary>Phase 1 角色種子</summary>
    private void SeedRoles()
    {
        var roles = new[]
        {
            new Role
            {
                RoleId = "role_reader",
                DisplayName = "Reader",
                AllowedTaskTypes = ToJson(new[] { "query", "analysis" }),
                DefaultCapabilityIds = ToJson(new[] { "file.read", "file.list", "file.search_name", "file.search_content" }),
                Status = EntityStatus.Active
            },
            new Role
            {
                RoleId = "role_pm",
                DisplayName = "Project Manager",
                AllowedTaskTypes = ToJson(new[] { "task_management", "planning" }),
                DefaultCapabilityIds = ToJson(Array.Empty<string>()),
                Status = EntityStatus.Active
            },
            new Role
            {
                RoleId = "role_sa",
                DisplayName = "Solution Architect",
                AllowedTaskTypes = ToJson(new[] { "architecture", "review" }),
                DefaultCapabilityIds = ToJson(new[] { "file.read", "file.list", "file.search_name", "file.search_content" }),
                Status = EntityStatus.Active
            },
            new Role
            {
                RoleId = "role_executor",
                DisplayName = "Executor",
                AllowedTaskTypes = ToJson(new[] { "code_gen", "system_scaffold", "doc_gen" }),
                DefaultCapabilityIds = ToJson(new[] { "file.read", "file.list", "file.search_name", "file.search_content" }),
                Status = EntityStatus.Active
            },
            new Role
            {
                RoleId = "role_auditor",
                DisplayName = "Auditor",
                AllowedTaskTypes = ToJson(new[] { "audit" }),
                DefaultCapabilityIds = ToJson(Array.Empty<string>()),
                Status = EntityStatus.Active
            },
            new Role
            {
                RoleId = "role_admin",
                DisplayName = "Administrator",
                AllowedTaskTypes = ToJson(new[] { "*" }),
                DefaultCapabilityIds = ToJson(new[] { "*" }),
                Status = EntityStatus.Active
            }
        };

        foreach (var role in roles)
        {
            if (_db.Get<Role>(role.RoleId) == null)
                _db.Insert(role);
        }
    }

    /// <summary>Phase 1 能力種子（4 Low + 2 Medium/High）</summary>
    private void SeedCapabilities()
    {
        var capabilities = new[]
        {
            // ── Low 風險：Phase 1 自動放行 ──
            new Capability
            {
                CapabilityId = "file.read",
                Route = "read_file",
                ActionType = ActionType.Read,
                ResourceType = "file",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        offset = new { type = "integer" },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "path" }
                })
            },
            new Capability
            {
                CapabilityId = "file.list",
                Route = "list_directory",
                ActionType = ActionType.Read,
                ResourceType = "file",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        depth = new { type = "integer" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "file.search_name",
                Route = "search_files",
                ActionType = ActionType.Read,
                ResourceType = "file",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        directory = new { type = "string" },
                        pattern = new { type = "string" }
                    },
                    required = new[] { "pattern" }
                })
            },
            new Capability
            {
                CapabilityId = "file.search_content",
                Route = "search_content",
                ActionType = ActionType.Read,
                ResourceType = "file",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        directory = new { type = "string" },
                        pattern = new { type = "string" },
                        file_pattern = new { type = "string" }
                    },
                    required = new[] { "pattern" }
                })
            },

            // ── Medium 風險：Phase 3 功能池允許 ──
            new Capability
            {
                CapabilityId = "file.write",
                Route = "write_file",
                ActionType = ActionType.Write,
                ResourceType = "file",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "require_approval",
                TtlSeconds = 300,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        content = new { type = "string" },
                        mode = new { type = "string", @enum = new[] { "rewrite", "append" } }
                    },
                    required = new[] { "path", "content" }
                })
            },
            new Capability
            {
                CapabilityId = "file.delete",
                Route = "delete_file",
                ActionType = ActionType.Write,
                ResourceType = "file",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "require_approval",
                TtlSeconds = 300,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new { path = new { type = "string" } },
                    required = new[] { "path" }
                })
            },

            // ── High 風險：仍然拒絕 ──
            new Capability
            {
                CapabilityId = "command.execute",
                Route = "run_command",
                ActionType = ActionType.Execute,
                ResourceType = "command",
                RiskLevel = RiskLevel.High,
                ApprovalPolicy = "deny",
                TtlSeconds = 60,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        command = new { type = "string" },
                        cwd = new { type = "string" }
                    },
                    required = new[] { "command" }
                })
            },

            // ── LINE 通訊能力 ──

            new Capability
            {
                CapabilityId = "line.message.send",
                Route = "send_line_message",
                ActionType = ActionType.Execute,
                ResourceType = "communication",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        to = new { type = "string" },
                        text = new { type = "string", maxLength = 5000 }
                    },
                    required = new[] { "text" }
                })
            },
            new Capability
            {
                CapabilityId = "line.notification.send",
                Route = "send_line_notification",
                ActionType = ActionType.Execute,
                ResourceType = "communication",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        to = new { type = "string" },
                        title = new { type = "string" },
                        body = new { type = "string" },
                        level = new { type = "string", @enum = new[] { "info", "warning", "error", "success" } }
                    },
                    required = new[] { "title", "body" }
                })
            },
            new Capability
            {
                CapabilityId = "line.audio.send",
                Route = "send_line_audio",
                ActionType = ActionType.Execute,
                ResourceType = "communication",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        to = new { type = "string" },
                        text = new { type = "string" },
                        audio_url = new { type = "string" },
                        duration_ms = new { type = "integer" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "line.message.read",
                Route = "read_line_messages",
                ActionType = ActionType.Read,
                ResourceType = "communication",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        limit = new { type = "integer" },
                        consume = new { type = "boolean" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "line.approval.request",
                Route = "request_line_approval",
                ActionType = ActionType.Execute,
                ResourceType = "communication",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        to = new { type = "string" },
                        description = new { type = "string" },
                        request_id = new { type = "string" },
                        timeout_sec = new { type = "integer" }
                    },
                    required = new[] { "description" }
                })
            },

            // ── Agent 管理能力 ──

            new Capability
            {
                CapabilityId = "agent.list",
                Route = "list_agents",
                ActionType = ActionType.Read,
                ResourceType = "agent",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        filter = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "agent.create",
                Route = "create_agent",
                ActionType = ActionType.Execute,
                ResourceType = "agent",
                RiskLevel = RiskLevel.High,
                ApprovalPolicy = "require_approval",
                TtlSeconds = 300,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        display_name = new { type = "string" },
                        capability_ids = new { type = "array", items = new { type = "string" } },
                        task_type = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "agent.stop",
                Route = "stop_agent",
                ActionType = ActionType.Execute,
                ResourceType = "agent",
                RiskLevel = RiskLevel.High,
                ApprovalPolicy = "require_approval",
                TtlSeconds = 300,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        agent_id = new { type = "string" }
                    },
                    required = new[] { "agent_id" }
                })
            },

            // ── 對話日誌能力（Layer 1：自動機械式紀錄） ──

            new Capability
            {
                CapabilityId = "conv.log.write",
                Route = "conv_log_append",
                ActionType = ActionType.Write,
                ResourceType = "conversation",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        user_id = new { type = "string" },
                        role = new { type = "string", @enum = new[] { "user", "assistant" } },
                        content = new { type = "string" },
                        metadata = new { type = "string" }
                    },
                    required = new[] { "user_id", "role", "content" }
                })
            },
            new Capability
            {
                CapabilityId = "conv.log.read",
                Route = "conv_log_read",
                ActionType = ActionType.Read,
                ResourceType = "conversation",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        user_id = new { type = "string" },
                        limit = new { type = "integer" },
                        before = new { type = "string" }
                    },
                    required = new[] { "user_id" }
                })
            },

            // ── 記憶能力（Layer 2：Agent 判斷式持久化） ──

            new Capability
            {
                CapabilityId = "memory.read",
                Route = "memory_retrieve",
                ActionType = ActionType.Read,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string" },
                        search = new { type = "string" },
                        limit = new { type = "integer" }
                    },
                    required = Array.Empty<string>()
                })
            },
            new Capability
            {
                CapabilityId = "memory.write",
                Route = "memory_store",
                ActionType = ActionType.Write,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string" },
                        value = new { type = "string" },
                        content_type = new { type = "string" }
                    },
                    required = new[] { "key", "value" }
                })
            },
            new Capability
            {
                CapabilityId = "memory.delete",
                Route = "memory_delete",
                ActionType = ActionType.Write,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 300,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        key = new { type = "string" }
                    },
                    required = new[] { "key" }
                })
            },

            // ── 搜尋能力（BM25 + 向量 + RAG） ──

            new Capability
            {
                CapabilityId = "memory.fulltext_search",
                Route = "memory_fulltext_search",
                ActionType = ActionType.Read,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        scope = new { type = "string", @enum = new[] { "memory", "convlog", "all" } },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "query" }
                })
            },
            new Capability
            {
                CapabilityId = "memory.semantic_search",
                Route = "memory_semantic_search",
                ActionType = ActionType.Read,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        limit = new { type = "integer" },
                        threshold = new { type = "number" }
                    },
                    required = new[] { "query" }
                })
            },
            new Capability
            {
                CapabilityId = "rag.retrieve",
                Route = "rag_retrieve",
                ActionType = ActionType.Read,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        mode = new { type = "string", @enum = new[] { "hybrid", "semantic", "fulltext" } },
                        limit = new { type = "integer" },
                        threshold = new { type = "number" },
                        include_convlog = new { type = "boolean" },
                        tags = new { type = "array", items = new { type = "string" }, description = "標籤過濾" },
                        rewrite = new { type = "boolean", description = "LLM 查詢改寫（預設 true）" },
                        rerank = new { type = "boolean", description = "LLM 重排序（預設 true）" }
                    },
                    required = new[] { "query" }
                })
            },

            new Capability
            {
                CapabilityId = "rag.import",
                Route = "rag_import",
                ActionType = ActionType.Write,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 1800,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        format = new { type = "string", @enum = new[] { "json", "csv" }, description = "匯入格式" },
                        tag = new { type = "string", description = "分類標籤（作為 source_key 前綴）" },
                        data = new { type = "string", description = "JSON 陣列或 CSV 字串" },
                        task_id = new { type = "string" }
                    },
                    required = new[] { "format", "tag", "data" }
                })
            },
            new Capability
            {
                CapabilityId = "rag.import_web",
                Route = "rag_import_web",
                ActionType = ActionType.Write,
                ResourceType = "memory",
                RiskLevel = RiskLevel.Medium,
                ApprovalPolicy = "auto",
                TtlSeconds = 1800,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "搜尋關鍵字" },
                        tag = new { type = "string", description = "分類標籤" },
                        urls = new { type = "array", items = new { type = "string" }, description = "直接指定 URL 列表" },
                        max_pages = new { type = "integer", description = "最大抓取頁面數（預設 5）" },
                        chunk_size = new { type = "integer", description = "分段大小（預設 1000 字元）" },
                        chunk_overlap = new { type = "integer", description = "分段重疊（預設 100 字元）" },
                        task_id = new { type = "string" }
                    },
                    required = new[] { "query" }
                })
            },

            // ── 網路能力 ──

            new Capability
            {
                CapabilityId = "web.search",
                Route = "web_search",
                ActionType = ActionType.Read,
                ResourceType = "web",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        limit = new { type = "integer" }
                    },
                    required = new[] { "query" }
                })
            },
            new Capability
            {
                CapabilityId = "web.fetch",
                Route = "web_fetch",
                ActionType = ActionType.Read,
                ResourceType = "web",
                RiskLevel = RiskLevel.Low,
                ApprovalPolicy = "auto",
                TtlSeconds = 900,
                ParamSchema = ToJson(new
                {
                    type = "object",
                    properties = new
                    {
                        url = new { type = "string" },
                        max_length = new { type = "integer" }
                    },
                    required = new[] { "url" }
                })
            }
        };

        foreach (var cap in capabilities)
        {
            var existing = _db.Get<Capability>(cap.CapabilityId);
            if (existing == null)
            {
                _db.Insert(cap);
                continue;
            }

            cap.Version = existing.Version;
            if (CapabilityChanged(existing, cap))
            {
                cap.Version = existing.Version + 1;
                _db.Update(cap);
            }
        }
    }

    // ── 輔助 ──

    private static string ToJson(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });

    private static bool CapabilityChanged(Capability existing, Capability candidate)
    {
        return existing.Route != candidate.Route ||
               existing.ActionType != candidate.ActionType ||
               existing.ResourceType != candidate.ResourceType ||
               existing.ParamSchema != candidate.ParamSchema ||
               existing.RiskLevel != candidate.RiskLevel ||
               existing.ApprovalPolicy != candidate.ApprovalPolicy ||
               existing.TtlSeconds != candidate.TtlSeconds ||
               existing.Quota != candidate.Quota ||
               existing.AuditLevel != candidate.AuditLevel;
    }

    private void TryExecute(string sql)
    {
        try { _db.Execute(sql); }
        catch { /* 索引已存在等情況忽略 */ }
    }
    private void SeedDevelopmentData(DevelopmentSeedOptions? developmentSeed)
    {
        if (developmentSeed?.Enabled != true)
            return;

        if (!string.IsNullOrWhiteSpace(developmentSeed.PrincipalId) &&
            _db.Get<Principal>(developmentSeed.PrincipalId) == null)
        {
            var actorType = Enum.TryParse<ActorType>(developmentSeed.ActorType, true, out var parsedActorType)
                ? parsedActorType
                : ActorType.AI;

            _db.Insert(new Principal
            {
                PrincipalId = developmentSeed.PrincipalId,
                ActorType = actorType,
                DisplayName = developmentSeed.DisplayName,
                Status = EntityStatus.Active,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (!string.IsNullOrWhiteSpace(developmentSeed.TaskId) &&
            _db.Get<BrokerTask>(developmentSeed.TaskId) == null)
        {
            _db.Insert(new BrokerTask
            {
                TaskId = developmentSeed.TaskId,
                TaskType = developmentSeed.TaskType,
                SubmittedBy = developmentSeed.PrincipalId,
                RiskLevel = RiskLevel.Low,
                State = TaskState.Active,
                ScopeDescriptor = developmentSeed.ScopeDescriptor,
                RuntimeDescriptor = developmentSeed.RuntimeDescriptor,
                AssignedPrincipalId = developmentSeed.PrincipalId,
                AssignedRoleId = developmentSeed.AssignedRoleId,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
