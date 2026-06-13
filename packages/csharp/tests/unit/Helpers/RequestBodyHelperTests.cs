using System.Text.Json;
using Broker.Helpers;
using Broker.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Unit.Tests.Helpers;

/// <summary>
/// 鎖住 RequestBodyHelper 從 HttpContext 抽 principal/role/session/task 的決定性邏輯，
/// 以及 body 取得 / 必填欄位驗證。
///
/// 重點契約（曾出過 bug、見 source doc comment）：
///   1. 雙 auth 來源 —— BrokerAuthMiddleware（token、值已是 "role_admin"）優先；
///      CurrentUserMiddleware（cookie、值是 "admin"）fallback 且要補 "role_" 前綴。
///      之前只看第一套、cookie 進來的 dashboard admin 永遠被當成沒角色、admin 端點全 403。
///   2. 缺對應 Items 時一律安全 fallback 成 ""（不丟例外）。
///   3. GetBody 同一請求內只解析一次（快取在 HttpContext.Items）。
///
/// 用 DefaultHttpContext 直接塞 Items 當 stub，不碰真 middleware。
/// </summary>
public class RequestBodyHelperTests
{
    private static HttpContext Ctx() => new DefaultHttpContext();

    // ---- GetRoleId ----

    [Fact]
    public void GetRoleId_BrokerSource_TakesPriority()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.RoleIdKey] = "role_admin";
        // 即使 cookie 來源不同，broker(token) 優先
        ctx.Items[CurrentUserMiddleware.RoleKey] = "user";

        RequestBodyHelper.GetRoleId(ctx).Should().Be("role_admin");
    }

    [Fact]
    public void GetRoleId_CookieSource_GetsRolePrefixAdded()
    {
        var ctx = Ctx();
        // cookie 路徑值是 "admin"，需要補 "role_" 與 token path 對齊
        ctx.Items[CurrentUserMiddleware.RoleKey] = "admin";

        RequestBodyHelper.GetRoleId(ctx).Should().Be("role_admin");
    }

    [Fact]
    public void GetRoleId_CookieSource_User_GetsRolePrefixAdded()
    {
        var ctx = Ctx();
        ctx.Items[CurrentUserMiddleware.RoleKey] = "user";

        RequestBodyHelper.GetRoleId(ctx).Should().Be("role_user");
    }

    [Fact]
    public void GetRoleId_CookieAlreadyPrefixed_NotDoublePrefixed()
    {
        var ctx = Ctx();
        // 若 cookie 值已含前綴，不該變成 role_role_admin（OrdinalIgnoreCase 比對）
        ctx.Items[CurrentUserMiddleware.RoleKey] = "Role_Admin";

        RequestBodyHelper.GetRoleId(ctx).Should().Be("Role_Admin");
    }

    [Fact]
    public void GetRoleId_NoSource_ReturnsEmpty()
    {
        RequestBodyHelper.GetRoleId(Ctx()).Should().Be("");
    }

    [Fact]
    public void GetRoleId_EmptyBrokerRole_FallsThroughToCookie()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.RoleIdKey] = "";   // 空字串不算有值
        ctx.Items[CurrentUserMiddleware.RoleKey] = "admin";

        RequestBodyHelper.GetRoleId(ctx).Should().Be("role_admin");
    }

    // ---- GetPrincipalId ----

    [Fact]
    public void GetPrincipalId_BrokerSource_TakesPriority()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.PrincipalIdKey] = "prn_token";
        ctx.Items[CurrentUserMiddleware.PrincipalKey] = "prn_cookie";

        RequestBodyHelper.GetPrincipalId(ctx).Should().Be("prn_token");
    }

    [Fact]
    public void GetPrincipalId_CookieFallback_WhenBrokerEmpty()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.PrincipalIdKey] = "";
        ctx.Items[CurrentUserMiddleware.PrincipalKey] = "prn_cookie";

        RequestBodyHelper.GetPrincipalId(ctx).Should().Be("prn_cookie");
    }

    [Fact]
    public void GetPrincipalId_NoSource_ReturnsEmpty()
    {
        RequestBodyHelper.GetPrincipalId(Ctx()).Should().Be("");
    }

    // ---- GetSessionId / GetTaskId ----

    [Fact]
    public void GetSessionId_Present_ReturnsValue()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.SessionIdKey] = "sess_123";

        RequestBodyHelper.GetSessionId(ctx).Should().Be("sess_123");
    }

    [Fact]
    public void GetSessionId_Missing_ReturnsEmpty()
    {
        RequestBodyHelper.GetSessionId(Ctx()).Should().Be("");
    }

    [Fact]
    public void GetTaskId_Present_ReturnsValue()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.TaskIdKey] = "task_abc";

        RequestBodyHelper.GetTaskId(ctx).Should().Be("task_abc");
    }

    [Fact]
    public void GetTaskId_Missing_ReturnsEmpty()
    {
        RequestBodyHelper.GetTaskId(Ctx()).Should().Be("");
    }

    // ---- IsAdmin ----

    [Fact]
    public void IsAdmin_BrokerRoleAdmin_True()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.RoleIdKey] = "role_admin";

        RequestBodyHelper.IsAdmin(ctx).Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_CookieAdmin_True_AfterPrefixNormalization()
    {
        var ctx = Ctx();
        // cookie "admin" 經 GetRoleId 補成 "role_admin" → IsAdmin 必須認得
        ctx.Items[CurrentUserMiddleware.RoleKey] = "admin";

        RequestBodyHelper.IsAdmin(ctx).Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_UserRole_False()
    {
        var ctx = Ctx();
        ctx.Items[BrokerAuthMiddleware.RoleIdKey] = "role_user";

        RequestBodyHelper.IsAdmin(ctx).Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_NoRole_False()
    {
        RequestBodyHelper.IsAdmin(Ctx()).Should().BeFalse();
    }

    // ---- GetBody ----

    [Fact]
    public void GetBody_NoDecryptedBody_DefaultsToEmptyObject()
    {
        var body = RequestBodyHelper.GetBody(Ctx());

        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void GetBody_ParsesDecryptedJson()
    {
        var ctx = Ctx();
        ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = "{\"symbol\":\"BTC\",\"qty\":3}";

        var body = RequestBodyHelper.GetBody(ctx);

        body.GetProperty("symbol").GetString().Should().Be("BTC");
        body.GetProperty("qty").GetInt32().Should().Be(3);
    }

    [Fact]
    public void GetBody_CachesParse_SameRequestParsesOnce()
    {
        var ctx = Ctx();
        ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = "{\"a\":1}";

        var first = RequestBodyHelper.GetBody(ctx);
        // 改 source 字串：若第二次重新解析就會反映新值；快取則維持舊值
        ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = "{\"a\":2}";
        var second = RequestBodyHelper.GetBody(ctx);

        first.GetProperty("a").GetInt32().Should().Be(1);
        second.GetProperty("a").GetInt32().Should().Be(1, "同一請求內只解析一次、快取在 Items");
    }

    // ---- TryGetRequired ----

    [Fact]
    public void TryGetRequired_PresentString_ReturnsTrueAndValue()
    {
        var body = JsonDocument.Parse("{\"name\":\"hello\"}").RootElement;

        var ok = RequestBodyHelper.TryGetRequired(body, "name", out var value, out var error);

        ok.Should().BeTrue();
        value.Should().Be("hello");
        error.Should().BeNull();
    }

    [Fact]
    public void TryGetRequired_MissingProperty_ReturnsFalseWithError()
    {
        var body = JsonDocument.Parse("{}").RootElement;

        var ok = RequestBodyHelper.TryGetRequired(body, "name", out var value, out var error);

        ok.Should().BeFalse();
        value.Should().Be("");
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryGetRequired_WrongValueKind_ReturnsFalse()
    {
        // 數字而非字串 → 視為缺
        var body = JsonDocument.Parse("{\"name\":123}").RootElement;

        var ok = RequestBodyHelper.TryGetRequired(body, "name", out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryGetRequired_WhitespaceString_ReturnsFalse()
    {
        var body = JsonDocument.Parse("{\"name\":\"   \"}").RootElement;

        var ok = RequestBodyHelper.TryGetRequired(body, "name", out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    // ---- TryGetRequiredFields ----

    [Fact]
    public void TryGetRequiredFields_AllPresent_ReturnsDictionary()
    {
        var body = JsonDocument.Parse("{\"a\":\"1\",\"b\":\"2\"}").RootElement;

        var ok = RequestBodyHelper.TryGetRequiredFields(body, new[] { "a", "b" },
            out var values, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        values.Should().HaveCount(2);
        values["a"].Should().Be("1");
        values["b"].Should().Be("2");
    }

    [Fact]
    public void TryGetRequiredFields_OneMissing_ReturnsFalseWithError()
    {
        var body = JsonDocument.Parse("{\"a\":\"1\"}").RootElement;

        var ok = RequestBodyHelper.TryGetRequiredFields(body, new[] { "a", "b" },
            out var values, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
        // a 已先填入；b 缺時短路返回
        values.Should().ContainKey("a");
        values.Should().NotContainKey("b");
    }
}
