using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LineWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Workers.LineWorker;

public class InboundDispatcherReplyTests
{
    [Theory]
    [InlineData("{\"data\":{\"reply\":\"\",\"follow_up_messages\":null,\"error\":null}}", false)]
    [InlineData("{\"data\":{\"reply\":null,\"follow_up_messages\":[],\"error\":null}}", false)]
    [InlineData("{\"data\":{\"reply\":\" \",\"follow_up_messages\":[\"\",\" \"],\"error\":null}}", false)]
    [InlineData("{\"data\":{\"reply\":\"\"}}", false)]
    [InlineData("{\"reply\":\"\",\"error\":null}", false)]
    [InlineData("{\"data\":{\"reply\":\"\",\"follow_up_messages\":[],\"error\":\"nya_failure\"}}", true)]
    [InlineData("{\"reply\":\"\",\"error\":\"nya_failure\"}", true)]
    public async Task ForwardToBrokerAndReply_EmptyResponse_OnlySendsFallbackForBrokerError(
        string responseBody, bool expectsFallback)
    {
        var messages = await ForwardAndRecordMessages(responseBody);

        if (expectsFallback)
            messages.Should().Equal("系統目前沒有產生可回覆內容，請稍後再試。");
        else
            messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ForwardToBrokerAndReply_WithFollowUp_DoesNotSuppressMessages()
    {
        var messages = await ForwardAndRecordMessages(
            "{\"data\":{\"reply\":\"\",\"follow_up_messages\":[\"follow-up\"],\"error\":null}}");

        messages.Should().Equal("系統目前沒有產生可回覆內容，請稍後再試。", "follow-up");
    }

    private static async Task<List<string>> ForwardAndRecordMessages(string responseBody)
    {
        var messages = new List<string>();
        using var brokerHttp = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            })));
        using var lineHttp = new HttpClient(new StubHttpHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("to").GetString().Should().Be("guest-user");
            messages.Add(body.RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var lineApi = new LineApiClient("test-token", "test-secret");
        ReplaceHttpClient(lineApi, "_http", lineHttp);
        var dispatcher = new InboundDispatcher(
            null!, lineApi, string.Empty, "http://broker.test", NullLogger.Instance);
        ReplaceHttpClient(dispatcher, "_httpClient", brokerHttp);

        var method = typeof(InboundDispatcher).GetMethod(
            "ForwardToBrokerAndReply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(dispatcher, new object[] { "guest-user", "hello", CancellationToken.None })!;

        return messages;
    }

    private static void ReplaceHttpClient(object target, string fieldName, HttpClient replacement)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(target)!).Dispose();
        field.SetValue(target, replacement);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
