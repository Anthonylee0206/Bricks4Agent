using Broker.Endpoints;
using Broker.Services;
using FluentAssertions;

namespace Unit.Tests.Admin;

public class NyaChatEndpointAuthorizationTests
{
    [Theory]
    [InlineData("/users")]
    [InlineData("/users/user-1/overview")]
    [InlineData("/users/user-1/messages")]
    [InlineData("/users/user-1/messages/message-1")]
    [InlineData("/users/user-1/topics")]
    [InlineData("/users/user-1/facts")]
    [InlineData("/users/user-1/facts/fact-1/history")]
    [InlineData("/users/user-1/summaries")]
    public void UserReads_RequireUserManageOrAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/users/user-1/facts")]
    [InlineData("PUT", "/users/user-1/facts/fact-1")]
    [InlineData("POST", "/users/user-1/facts/fact-1/rollback")]
    [InlineData("DELETE", "/users/user-1/facts")]
    [InlineData("DELETE", "/users/user-1/facts/fact-1")]
    [InlineData("POST", "/users/user-1/messages")]
    [InlineData("PUT", "/users/user-1/messages/message-1")]
    [InlineData("DELETE", "/users/user-1/messages")]
    [InlineData("DELETE", "/users/user-1/messages/message-1")]
    [InlineData("POST", "/users/user-1/messages/soft-delete")]
    [InlineData("POST", "/users/user-1/topics")]
    [InlineData("PUT", "/users/user-1/topics/topic-1")]
    [InlineData("POST", "/users/user-1/topics/topic-1/switch")]
    [InlineData("POST", "/users/user-1/topics/topic-1/rename")]
    [InlineData("DELETE", "/users/user-1/topics/topic-1")]
    [InlineData("POST", "/users/user-1/summaries/trigger")]
    [InlineData("POST", "/users/user-1/summaries/summary-1/rollback")]
    [InlineData("PUT", "/users/user-1/summaries")]
    [InlineData("DELETE", "/users/user-1/summaries")]
    [InlineData("POST", "/users/user-1/soul")]
    [InlineData("PUT", "/users/user-1/soul")]
    [InlineData("DELETE", "/users/user-1/soul")]
    [InlineData("DELETE", "/users/user-1")]
    public void UserMutations_RequireUserManage(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/audit")]
    [InlineData("/users/user-1/audit")]
    public void AuditReads_RequireAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/config")]
    [InlineData("/prompts")]
    [InlineData("/tools")]
    [InlineData("/souls")]
    [InlineData("/souls/soul-1")]
    public void SystemReads_RequireStatusReadOrAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemStatusRead, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", "/config")]
    [InlineData("POST", "/prompts/reload")]
    [InlineData("POST", "/tools/tool-1/toggle")]
    [InlineData("POST", "/souls")]
    [InlineData("POST", "/souls/reload")]
    [InlineData("PUT", "/souls/soul-1")]
    [InlineData("DELETE", "/souls/soul-1")]
    public void SystemMutations_RequireWorkerManage(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemWorkerManage);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/llm/param-catalog")]
    [InlineData("/llm/templates")]
    [InlineData("/llm/call-sites")]
    [InlineData("/llm/profiles")]
    [InlineData("/llm/routes")]
    public void LlmReads_RequireStatusRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemStatusRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("DELETE", "/audit")]
    [InlineData("DELETE", "/audit/log-1")]
    [InlineData("DELETE", "/users/user-1/audit")]
    [InlineData("POST", "/llm/profiles")]
    [InlineData("PUT", "/llm/profiles")]
    [InlineData("DELETE", "/llm/profiles")]
    [InlineData("POST", "/llm/profiles/profile-1")]
    [InlineData("PUT", "/llm/profiles/profile-1")]
    [InlineData("DELETE", "/llm/profiles/profile-1")]
    [InlineData("PUT", "/llm/routes")]
    public void AuditDeletionsAndLlmMutations_RequireSuperAdmin(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().BeEmpty();
        superAdminOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData("/roles")]
    [InlineData("/roles/role-1")]
    [InlineData("/capabilities")]
    [InlineData("/users/user-1/roles")]
    [InlineData("/users/user-1/roles/role-1")]
    public void RoleRoutes_AreDeniedForEveryMethod(string path)
    {
        foreach (var method in new[] { "GET", "POST", "PUT", "DELETE" })
        {
            var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

            resolved.Should().BeFalse();
            permissions.Should().BeEmpty();
            superAdminOnly.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("GET", "/unknown")]
    [InlineData("POST", "/users/user-1/unknown")]
    [InlineData("DELETE", "/users/user-1/audit/log-1")]
    [InlineData("GET", "/audit/log-1")]
    [InlineData("GET", "/users/user-1/facts/fact-1")]
    [InlineData("GET", "/llm/profiles/profile-1")]
    [InlineData("DELETE", "/llm/routes")]
    [InlineData("POST", "/config")]
    [InlineData("PUT", "/prompts/reload")]
    [InlineData("PATCH", "/users/user-1/facts")]
    [InlineData("POST", "/users")]
    [InlineData("GET", "/users-extra")]
    [InlineData("GET", "/users/user-1/messages/message-1/unknown")]
    [InlineData("GET", "/users//messages")]
    [InlineData("GET", "/local-admin/nya/status")]
    [InlineData("GET", "status")]
    [InlineData("GET", "")]
    [InlineData("GET", "/")]
    public void UnmatchedRoutes_AreDenied(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeFalse();
        permissions.Should().BeEmpty();
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("get", "/UsErS/User-1/MeSsAgEs/")]
    [InlineData("GET", "/users/%E7%94%A8%E6%88%B6/messages/message-1")]
    public void RouteMatching_AcceptsCaseAndTrailingSlashAndEncodedIdentifiers(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }
}
