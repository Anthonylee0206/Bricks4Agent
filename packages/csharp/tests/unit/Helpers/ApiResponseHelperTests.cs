using System.Diagnostics;
using Broker.Helpers;

namespace Unit.Tests.Helpers;

/// <summary>
/// 鎖住 ApiResponseHelper 的回應塑形契約（純邏輯、無 MVC / HttpContext 依賴）：
///   - Success&lt;T&gt; → Success=true / Message / Data 透傳 / 無 ErrorCode / TraceId 非空
///   - Error → Success=false / Message / ErrorCode 塑形（預設 400 + 各狀態碼）
///   - 邊界：null message、null data、default(T)、value-type T
///
/// TraceId 來源 = Activity.Current?.Id ?? Guid.NewGuid()。無 ambient Activity 時走 GUID fallback，
/// 我們鎖「非空 + 32 hex（"N" 格式）」這個確定性結構，不去鎖隨機值本身。
/// 有 ambient Activity 時鎖「透傳 Activity.Id」。
/// </summary>
public class ApiResponseHelperTests
{
    // ---------- Success ----------

    [Fact]
    public void Success_WithData_ShapesSuccessfulEnvelope()
    {
        var resp = ApiResponseHelper.Success("payload");

        resp.Success.Should().BeTrue();
        resp.Message.Should().Be("ok", "預設 message 應為 ok");
        resp.Data.Should().Be("payload");
        resp.ErrorCode.Should().BeNull("成功回應不帶 ErrorCode");
        resp.TraceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Success_CustomMessage_IsPassedThrough()
    {
        var resp = ApiResponseHelper.Success(123, "created");

        resp.Success.Should().BeTrue();
        resp.Message.Should().Be("created");
        resp.Data.Should().Be(123);
        resp.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Success_NoArgs_DefaultsDataNullAndMessageOk()
    {
        // string? T 的 default 為 null；不傳任何參數應走預設路徑
        var resp = ApiResponseHelper.Success<string>();

        resp.Success.Should().BeTrue();
        resp.Message.Should().Be("ok");
        resp.Data.Should().BeNull();
        resp.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Success_NullData_KeepsNullDataAndStaysSuccess()
    {
        var resp = ApiResponseHelper.Success<string>(null, "no-content");

        resp.Success.Should().BeTrue();
        resp.Data.Should().BeNull();
        resp.Message.Should().Be("no-content");
    }

    [Fact]
    public void Success_ValueTypeDefault_PreservesZero()
    {
        // value-type T 的 default 為 0、不該被當成「缺資料」
        var resp = ApiResponseHelper.Success(0);

        resp.Success.Should().BeTrue();
        resp.Data.Should().Be(0);
        resp.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Success_ComplexType_PassesReferenceThrough()
    {
        var data = new[] { "a", "b" };
        var resp = ApiResponseHelper.Success(data);

        resp.Data.Should().BeSameAs(data, "Data 應原樣透傳、不複製");
    }

    // ---------- Error ----------

    [Fact]
    public void Error_DefaultCode_Is400()
    {
        var resp = ApiResponseHelper.Error("bad request");

        resp.Success.Should().BeFalse();
        resp.Message.Should().Be("bad request");
        resp.ErrorCode.Should().Be(400, "Error 預設狀態碼為 400");
        resp.Data.Should().BeNull("錯誤回應不帶 Data");
        resp.TraceId.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public void Error_ExplicitCode_ShapesGivenStatus(int code)
    {
        var resp = ApiResponseHelper.Error("nope", code);

        resp.Success.Should().BeFalse();
        resp.ErrorCode.Should().Be(code);
        resp.Message.Should().Be("nope");
    }

    [Fact]
    public void Error_NullMessage_DoesNotThrowAndStaysError()
    {
        // 邊界：null message 直接塞進 Message（helper 無防呆、契約上允許）
        var resp = ApiResponseHelper.Error(null!, 500);

        resp.Success.Should().BeFalse();
        resp.Message.Should().BeNull();
        resp.ErrorCode.Should().Be(500);
    }

    [Fact]
    public void Error_EmptyMessage_PreservesEmptyString()
    {
        var resp = ApiResponseHelper.Error(string.Empty);

        resp.Success.Should().BeFalse();
        resp.Message.Should().BeEmpty();
        resp.ErrorCode.Should().Be(400);
    }

    // ---------- TraceId 塑形 ----------

    [Fact]
    public void Success_NoAmbientActivity_FallsBackToGuidN()
    {
        // 確保測試在無 ambient Activity 下執行：暫停 Current
        var saved = Activity.Current;
        Activity.Current = null;
        try
        {
            var resp = ApiResponseHelper.Success("x");

            resp.TraceId.Should().NotBeNullOrEmpty();
            // Guid.ToString("N") = 32 個 hex、無連字號
            resp.TraceId.Should().HaveLength(32);
            resp.TraceId.Should().MatchRegex("^[0-9a-f]{32}$", "GUID 'N' 格式 fallback");
        }
        finally
        {
            Activity.Current = saved;
        }
    }

    [Fact]
    public void Error_NoAmbientActivity_FallsBackToGuidN()
    {
        var saved = Activity.Current;
        Activity.Current = null;
        try
        {
            var resp = ApiResponseHelper.Error("boom", 500);

            resp.TraceId.Should().HaveLength(32);
            resp.TraceId.Should().MatchRegex("^[0-9a-f]{32}$");
        }
        finally
        {
            Activity.Current = saved;
        }
    }

    [Fact]
    public void Success_WithAmbientActivity_UsesActivityId()
    {
        // 有 ambient Activity 時 TraceId 應透傳 Activity.Id（不是新 GUID）
        using var activity = new Activity("test-op");
        activity.Start();
        try
        {
            activity.Id.Should().NotBeNullOrEmpty("Start() 後應有 Id、否則此測試前提不成立");

            var resp = ApiResponseHelper.Success("x");
            resp.TraceId.Should().Be(activity.Id);
        }
        finally
        {
            activity.Stop();
        }
    }

    [Fact]
    public void Success_EachCallWithoutActivity_ProducesDistinctTraceIds()
    {
        var saved = Activity.Current;
        Activity.Current = null;
        try
        {
            var a = ApiResponseHelper.Success("x");
            var b = ApiResponseHelper.Success("x");

            a.TraceId.Should().NotBe(b.TraceId, "無 Activity 時每次都新 GUID、應互異");
        }
        finally
        {
            Activity.Current = saved;
        }
    }
}
