using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LineWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Workers.LineWorker;

public class InboundSenderAuthorizationTests
{
    public static TheoryData<string, string, string, bool> AuthorizationCases => new()
    {
        { "sender-1", "sender-1", "sender-1", true },
        { "sender-1", "group:chat-1", "sender-1", true },
        { "sender-1", "room:chat-1", "SENDER-1", true },
        { "sender-1", "group:chat-1", "guest", false },
        { "group:chat-1", "group:chat-1", "guest", false },
        { "room:chat-1", "room:chat-1", "guest", false },
        { "sender-1", "sender-1", "", false },
        { "group:chat-1", "group:chat-1", "", false },
        { "room:chat-1", "room:chat-1", "", false },
        { "", "group:chat-1", "guest", true },
        { "", "room:chat-1", "", true }
    };

    [Theory]
    [MemberData(nameof(AuthorizationCases))]
    public async Task ProcessMessage_CommandsAuthorizeSenderAndKeepChatRouting(
        string whitelist, string chatId, string senderId, bool allowed)
    {
        using var harness = new DispatcherHarness(whitelist);

        await harness.Process(chatId, senderId, "/clear");

        if (allowed)
        {
            harness.BrokerRequests.Should().Equal((HttpMethod.Delete, $"/dev/conversations/{chatId}", ""));
            harness.LineRecipients.Should().Equal(LineRecipient(chatId));
        }
        else
        {
            harness.BrokerRequests.Should().BeEmpty();
            harness.LineRecipients.Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(AuthorizationCases))]
    public async Task ProcessMessage_ApprovalsAuthorizeAndRecordSender(
        string whitelist, string chatId, string senderId, bool allowed)
    {
        using var harness = new DispatcherHarness(whitelist);
        harness.Dispatcher.RegisterApproval("approval-1", "request-1", "Need approval");

        await harness.Process(chatId, senderId, "approve approval-1");

        var result = harness.Dispatcher.GetApprovalResult("approval-1");
        if (allowed)
        {
            result.Should().NotBeNull();
            result!.Approved.Should().BeTrue();
            result.RespondedBy.Should().Be(senderId);
            harness.BrokerRequests.Should().BeEmpty();
            harness.LineRecipients.Should().BeEmpty();
        }
        else
        {
            result.Should().BeNull();
            AssertForwarded(harness, chatId, "approve approval-1");
        }
    }

    [Theory]
    [InlineData("sender-1", "sender-1")]
    [InlineData("group:chat-1", "guest")]
    [InlineData("room:chat-1", "")]
    public async Task ProcessMessage_NormalChatKeepsChatIdentityRegardlessOfWhitelist(
        string chatId, string senderId)
    {
        using var harness = new DispatcherHarness("sender-1");

        await harness.Process(chatId, senderId, "hello");

        AssertForwarded(harness, chatId, "hello");
    }

    [Theory]
    [InlineData("{\"type\":\"user\",\"userId\":\"sender-1\"}", "sender-1", "sender-1")]
    [InlineData("{\"type\":\"group\",\"groupId\":\"chat-1\",\"userId\":\"sender-1\"}", "group:chat-1", "sender-1")]
    [InlineData("{\"type\":\"room\",\"roomId\":\"chat-1\",\"userId\":\"sender-1\"}", "room:chat-1", "sender-1")]
    [InlineData("{\"type\":\"group\",\"groupId\":\"chat-1\"}", "group:chat-1", "")]
    [InlineData("{\"type\":\"room\",\"roomId\":\"chat-1\",\"userId\":null}", "room:chat-1", "")]
    [InlineData("{\"type\":\"group\",\"groupId\":\"chat-1\",\"userId\":\"\"}", "group:chat-1", "")]
    public async Task ParseAndEnqueue_AllMessageTypesPreserveSenderAndChatIdentity(
        string sourceJson, string chatId, string senderId)
    {
        using var lineHttp = new HttpClient(new StubHttpHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing audio")
            })));
        using var lineApi = new LineApiClient("test-token", "test-secret");
        ReplaceHttpClient(lineApi, "_http", lineHttp);
        using var receiver = new WebhookReceiver(
            5000, "localhost", lineApi, Path.GetTempPath(), "http://broker.test", NullLogger.Instance);
        using var source = JsonDocument.Parse(sourceJson);
        var messageTypes = new[] { "text", "audio", "image" };
        var body = JsonSerializer.Serialize(new
        {
            events = messageTypes.Select(type => new
            {
                type = "message",
                source = source.RootElement,
                message = new { type, id = type, text = "hello" }
            })
        });

        var method = typeof(WebhookReceiver).GetMethod(
            "ParseAndEnqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(receiver, new object[] { body, CancellationToken.None })!;

        var messages = receiver.InboundQueue.ToArray();
        messages.Select(message => message.Type).Should().Equal(
            InboundMessageType.Text, InboundMessageType.Audio, InboundMessageType.Other);
        messages.Should().OnlyContain(message => message.UserId == chatId && message.SenderUserId == senderId);
    }

    private static void AssertForwarded(DispatcherHarness harness, string chatId, string text)
    {
        harness.BrokerRequests.Should().ContainSingle();
        var request = harness.BrokerRequests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/high-level/line/process");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("user_id").GetString().Should().Be(chatId);
        body.RootElement.GetProperty("message").GetString().Should().Be(text);
        harness.LineRecipients.Should().Equal(LineRecipient(chatId));
    }

    private static string LineRecipient(string chatId) => chatId.Split(':').Last();

    private static void ReplaceHttpClient(object target, string fieldName, HttpClient replacement)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(target)!).Dispose();
        field.SetValue(target, replacement);
    }

    private sealed class DispatcherHarness : IDisposable
    {
        private readonly HttpClient _brokerHttp;
        private readonly LineApiClient _lineApi;

        public InboundDispatcher Dispatcher { get; }
        public List<(HttpMethod Method, string Path, string Body)> BrokerRequests { get; } = new();
        public List<string> LineRecipients { get; } = new();

        public DispatcherHarness(string whitelist)
        {
            _brokerHttp = new HttpClient(new StubHttpHandler(async request =>
            {
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                BrokerRequests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"reply\":\"reply\"}", Encoding.UTF8, "application/json")
                };
            }));
            var lineHttp = new HttpClient(new StubHttpHandler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                LineRecipients.Add(body.RootElement.GetProperty("to").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            _lineApi = new LineApiClient("test-token", "test-secret");
            ReplaceHttpClient(_lineApi, "_http", lineHttp);
            Dispatcher = new InboundDispatcher(null!, _lineApi, whitelist, "http://broker.test", NullLogger.Instance);
            ReplaceHttpClient(Dispatcher, "_httpClient", _brokerHttp);
        }

        public async Task Process(string chatId, string senderId, string text)
        {
            var method = typeof(InboundDispatcher).GetMethod(
                "ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var message = new InboundMessage
            {
                UserId = chatId,
                SenderUserId = senderId,
                Type = InboundMessageType.Text,
                Text = text
            };
            await (Task)method.Invoke(Dispatcher, new object[] { message, CancellationToken.None })!;
        }

        public void Dispose()
        {
            _brokerHttp.Dispose();
            _lineApi.Dispose();
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
