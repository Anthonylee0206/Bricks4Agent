using Broker.Services.Observability;

// 放在 Broker.Services 命名空間：Program.cs 已經 using 這個命名空間，註冊時不必再改它的 using
namespace Broker.Services;

public static class IncidentReportingServiceCollectionExtensions
{
    /// <summary>
    /// 註冊異常偵測報告（見 <see cref="IncidentReportService"/>）。
    /// 排程預設關閉（<c>Observability:IncidentReport:Enabled=false</c>），只註冊服務不會改變任何既有行為；
    /// HTTP 狀態碼統計要另外在管線最外層掛上 <c>app.UseHttpStatusMetrics()</c>。
    /// </summary>
    public static IServiceCollection AddIncidentReporting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(IncidentReportOptions.SectionName).Get<IncidentReportOptions>()
            ?? new IncidentReportOptions();

        services.AddSingleton(options);
        services.AddSingleton<HttpStatusMetrics>();
        services.AddSingleton<AnomalyDigestBuilder>();
        services.AddSingleton<IncidentNarrator>();
        services.AddSingleton<IncidentReportStore>();
        services.AddSingleton<IncidentReportService>();
        services.AddHostedService(sp => sp.GetRequiredService<IncidentReportService>());
        return services;
    }
}
