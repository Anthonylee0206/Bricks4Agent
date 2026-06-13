using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// AzureIisDeploymentSecretResolver.Resolve 解析優先順序（純邏輯、確定性）：
///   1. 空白/缺 secretRef → null
///   2. Mappings 命中（且 username/password 皆非空）→ 回 secret（mapping 最優先）
///   3. Mappings 不完整（缺 user 或 pass / 空字串）→ 視為未命中、往下走
///   4. 環境變數 BRICKS4AGENT_DEPLOY_SECRET__{NORMALIZED}__USERNAME/PASSWORD fallback
///   5. 都沒有 → null
///
/// 注意：全部使用假值，絕不放真實密鑰。
/// 環境變數測試以 try/finally 清理，比照 AutoTraderServiceTests 既有風格。
/// </summary>
public class AzureIisDeploymentSecretResolverTests
{
    private static AzureIisDeploymentSecretResolver Build(
        params (string Key, string User, string Pass)[] mappings)
    {
        var options = new AzureIisDeploymentSecretResolverOptions();
        foreach (var (key, user, pass) in mappings)
        {
            options.Mappings[key] = new AzureIisDeploymentSecretEntry
            {
                UserName = user,
                Password = pass
            };
        }
        return new AzureIisDeploymentSecretResolver(options);
    }

    // ---- 缺/空輸入 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Resolve_BlankSecretRef_ReturnsNull(string? secretRef)
    {
        var resolver = Build(("any", "u", "p"));

        resolver.Resolve(secretRef!).Should().BeNull();
    }

    // ---- Mappings 命中 ----

    [Fact]
    public void Resolve_MappingHit_ReturnsSecret()
    {
        var resolver = Build(("site-a", "fake-user", "fake-pass"));

        var result = resolver.Resolve("site-a");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("fake-user");
        result.Password.Should().Be("fake-pass");
    }

    [Fact]
    public void Resolve_MappingLookupIsCaseInsensitive()
    {
        // Mappings dictionary 建構為 StringComparer.OrdinalIgnoreCase
        var resolver = Build(("Site-A", "fake-user", "fake-pass"));

        var result = resolver.Resolve("SITE-A");

        result.Should().NotBeNull();
        result!.UserName.Should().Be("fake-user");
    }

    [Fact]
    public void Resolve_MappingHit_ReturnsFreshInstanceNotEntryReference()
    {
        var resolver = Build(("site-a", "fake-user", "fake-pass"));

        var result = resolver.Resolve("site-a");

        result.Should().BeOfType<AzureIisDeploymentSecret>();
    }

    // ---- Mappings 不完整 → 視為未命中 ----

    [Theory]
    [InlineData("", "fake-pass")]   // 缺 user
    [InlineData("fake-user", "")]   // 缺 pass
    [InlineData("   ", "fake-pass")] // 空白 user
    [InlineData("fake-user", "   ")] // 空白 pass
    [InlineData("", "")]            // 兩者皆缺
    public void Resolve_IncompleteMapping_DoesNotReturnSecret(string user, string pass)
    {
        // mapping key 命中但內容不完整；無 env fallback → null
        var resolver = Build(("site-a", user, pass));

        // 確保不會誤抓到殘留環境變數
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__SITE_A__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__SITE_A__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, null);
        Environment.SetEnvironmentVariable(envPass, null);
        try
        {
            resolver.Resolve("site-a").Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    [Fact]
    public void Resolve_NoMappingNoEnv_ReturnsNull()
    {
        var resolver = Build();

        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__UNKNOWN__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__UNKNOWN__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, null);
        Environment.SetEnvironmentVariable(envPass, null);
        try
        {
            resolver.Resolve("unknown").Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    // ---- 環境變數 fallback ----

    [Fact]
    public void Resolve_EnvFallback_WhenNoMapping_ReturnsSecret()
    {
        var resolver = Build();
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__MYSITE__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__MYSITE__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, "env-fake-user");
        Environment.SetEnvironmentVariable(envPass, "env-fake-pass");
        try
        {
            var result = resolver.Resolve("mysite");

            result.Should().NotBeNull();
            result!.UserName.Should().Be("env-fake-user");
            result.Password.Should().Be("env-fake-pass");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    [Fact]
    public void Resolve_EnvFallback_NormalizesNonAlphanumericToUnderscoreAndUppercases()
    {
        // Normalize: 非字母數字 → '_'，字母 → 大寫
        // secretRef "my-site.prod" → "MY_SITE_PROD"
        var resolver = Build();
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__MY_SITE_PROD__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__MY_SITE_PROD__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, "norm-fake-user");
        Environment.SetEnvironmentVariable(envPass, "norm-fake-pass");
        try
        {
            var result = resolver.Resolve("my-site.prod");

            result.Should().NotBeNull();
            result!.UserName.Should().Be("norm-fake-user");
            result.Password.Should().Be("norm-fake-pass");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    [Fact]
    public void Resolve_EnvFallback_WhenOnlyUserNameSet_ReturnsNull()
    {
        var resolver = Build();
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__PARTIAL__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__PARTIAL__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, "only-user");
        Environment.SetEnvironmentVariable(envPass, null);
        try
        {
            resolver.Resolve("partial").Should().BeNull(
                "缺 password 不應視為完整密鑰");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    [Fact]
    public void Resolve_EnvFallback_WhenOnlyPasswordSet_ReturnsNull()
    {
        var resolver = Build();
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__PARTIAL2__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__PARTIAL2__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, null);
        Environment.SetEnvironmentVariable(envPass, "only-pass");
        try
        {
            resolver.Resolve("partial2").Should().BeNull(
                "缺 username 不應視為完整密鑰");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    // ---- 多來源：Mappings 優先於 Env ----

    [Fact]
    public void Resolve_MappingTakesPrecedenceOverEnv()
    {
        var resolver = Build(("dual", "mapping-user", "mapping-pass"));
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__DUAL__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__DUAL__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, "env-user");
        Environment.SetEnvironmentVariable(envPass, "env-pass");
        try
        {
            var result = resolver.Resolve("dual");

            result.Should().NotBeNull();
            result!.UserName.Should().Be("mapping-user",
                "mapping 命中時應優先於環境變數");
            result.Password.Should().Be("mapping-pass");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }

    [Fact]
    public void Resolve_IncompleteMappingFallsThroughToEnv()
    {
        // mapping key 命中但內容不完整 → 應 fallback 到 env
        var resolver = Build(("fallthru", "", ""));
        var envUser = "BRICKS4AGENT_DEPLOY_SECRET__FALLTHRU__USERNAME";
        var envPass = "BRICKS4AGENT_DEPLOY_SECRET__FALLTHRU__PASSWORD";
        var prevU = Environment.GetEnvironmentVariable(envUser);
        var prevP = Environment.GetEnvironmentVariable(envPass);
        Environment.SetEnvironmentVariable(envUser, "env-fallthru-user");
        Environment.SetEnvironmentVariable(envPass, "env-fallthru-pass");
        try
        {
            var result = resolver.Resolve("fallthru");

            result.Should().NotBeNull();
            result!.UserName.Should().Be("env-fallthru-user");
            result.Password.Should().Be("env-fallthru-pass");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envUser, prevU);
            Environment.SetEnvironmentVariable(envPass, prevP);
        }
    }
}
