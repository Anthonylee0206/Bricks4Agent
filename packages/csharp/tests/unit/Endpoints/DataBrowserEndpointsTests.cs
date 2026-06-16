using Broker.Endpoints;

namespace Broker.Tests.Endpoints;

/// <summary>
/// DataBrowserEndpoints SQL validation — read-only allowlist 邊界測試。
/// 把實際 SQL 執行（SqliteConnection）留給 integration test。
/// </summary>
public class DataBrowserEndpointsTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM audit_events LIMIT 5")]
    [InlineData("select * from audit_events")]              // 大小寫不限
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte")]// CTE 允許
    [InlineData("  SELECT * FROM agent_inbox_tasks  ")]      // 前後 whitespace
    public void Validate_AcceptsReadOnlyQueries(string sql)
    {
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeTrue($"sql `{sql}` should be accepted but got error: {err}");
        err.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Validate_RejectsEmpty(string sql)
    {
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        err.Should().Be("empty sql");
    }

    [Theory]
    [InlineData("INSERT INTO audit_events VALUES (1)")]
    [InlineData("UPDATE agent_inbox_tasks SET status = 'pending'")]
    [InlineData("DELETE FROM trading_approval_requests")]
    [InlineData("DROP TABLE audit_events")]
    [InlineData("ALTER TABLE x ADD COLUMN y")]
    [InlineData("CREATE TABLE foo (id INT)")]
    [InlineData("REPLACE INTO foo VALUES (1)")]
    public void Validate_RejectsWriteOperations(string sql)
    {
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        err.Should().Contain("only SELECT");  // 第一道：不是 SELECT 就拒
    }

    [Fact]
    public void Validate_RejectsSemicolon_MultiStatement()
    {
        var sql = "SELECT 1; DROP TABLE audit_events";
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        // 應該 catch 到 DROP（在分號之前已含寫關鍵字）
        // 或 catch 到 semicolon —— 兩種都算正確攔截
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Validate_RejectsSqlComment_LineStyle()
    {
        // 攻擊：用 -- comment 隱藏其他內容
        var sql = "SELECT * FROM users -- harmless comment";
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        err.Should().Contain("--");
    }

    [Fact]
    public void Validate_DefenseInDepth_BlocksCommentEvenIfFollowedByWriteKw()
    {
        // 即使 -- 後面接 INSERT 想隱藏、第一道（先檢查 INSERT 寫關鍵字）就會擋
        // 鎖住現有行為：任一道 fail 即 reject、不必特定錯誤訊息
        var sql = "SELECT * FROM users -- INSERT INTO";
        var (ok, _) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsSqlComment_BlockStyle()
    {
        // /* */ comment 不含其他寫關鍵字、確保 /* 自己會被捕捉
        var sql = "SELECT * /* harmless comment */ FROM users";
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        err.Should().Contain("/*");
    }

    [Fact]
    public void Validate_RejectsPragma()
    {
        // PRAGMA 可改 SQLite 行為、是 admin-level 操作、必拒
        var sql = "SELECT * FROM audit_events; PRAGMA journal_mode = MEMORY";
        var (ok, _) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsAttachDatabase()
    {
        var sql = "ATTACH DATABASE 'evil.db' AS evil";
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeFalse();
        err.Should().Contain("only SELECT");
    }

    [Fact]
    public void Validate_AcceptsAggregation()
    {
        // 真實 use case：報表查詢
        var sql = "SELECT event_type, COUNT(*) AS cnt FROM audit_events GROUP BY event_type ORDER BY cnt DESC LIMIT 30";
        var (ok, err) = DataBrowserEndpoints.ValidateReadOnlySql(sql);
        ok.Should().BeTrue(err);
    }
}
