using System.Reflection;
using LineWorker;
using Microsoft.Extensions.Logging;

namespace Unit.Tests.Workers.LineWorker;

public class InboundDispatcherApprovalTests
{
    private static readonly MethodInfo TryHandleApprovalMethod =
        typeof(InboundDispatcher).GetMethod(
            "TryHandleApproval",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TryHandleApproval method not found.");

    private static InboundDispatcher CreateDispatcher()
    {
        return new InboundDispatcher(
            receiver: null!,
            lineApi: null!,
            allowedUserIdsCsv: string.Empty,
            brokerApiUrl: "http://localhost:5000",
            logger: Substitute.For<ILogger>());
    }

    [Theory]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("n")]
    [InlineData("no")]
    public void TryHandleApproval_WithoutPendingApproval_DoesNotConsumePlainConfirmation(string text)
    {
        var dispatcher = CreateDispatcher();
        var inbound = new InboundMessage { UserId = "user-1", Text = text };

        var consumed = (bool)TryHandleApprovalMethod.Invoke(dispatcher, new object[] { text, inbound })!;

        consumed.Should().BeFalse();
    }

    [Fact]
    public void TryHandleApproval_WithPendingApproval_ConsumesAndStoresDecision()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.RegisterApproval("approval-1", "req-1", "Need approval");
        var inbound = new InboundMessage { UserId = "user-2", Text = "y" };

        var consumed = (bool)TryHandleApprovalMethod.Invoke(dispatcher, new object[] { "y", inbound })!;
        var result = dispatcher.GetApprovalResult("approval-1");

        consumed.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Approved.Should().BeTrue();
        result.RespondedBy.Should().Be("user-2");
    }
}
