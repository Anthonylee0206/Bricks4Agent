using System.Text.Json;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Admin;

public sealed class NyaMemoryRedesignTests
{
    [Theory]
    [InlineData("statement")]
    [InlineData("correction")]
    [InlineData("forget")]
    [InlineData("retell")]
    [InlineData("cross_topic_recall")]
    [InlineData("new_topic_mid_turn")]
    [InlineData("switch_back")]
    [InlineData("A_B_C")]
    [InlineData("ordered_batch")]
    [InlineData("invalid_arguments")]
    [InlineData("tool_success_reply_failure")]
    [InlineData("retry_after_failure")]
    [InlineData("overlapping_forget")]
    [InlineData("long_message")]
    [InlineData("duplicate_title")]
    [InlineData("cross_scope")]
    [InlineData("pending_restart")]
    [InlineData("cursor_after_forget")]
    [InlineData("new_message_after_selection")]
    [InlineData("note_capacity")]
    [InlineData("expired_note_update")]
    public async Task ScriptedScenarios(string scenario)
    {
        using var f = new Fixture();
        var room = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        switch (scenario)
        {
            case "statement":
            {
                f.Script(_ => Call("note_save", new { key = "name", content = "我叫小明", expiry = new { mode = "never" } }), _ => Reply("知道了。"));
                Assert.Null((await f.Chat("我叫小明，請記住。")).Error);
                var note = Assert.Single(f.Memory.ListNotes(Fixture.UserId));
                Assert.Equal("我叫小明", note.Content);
                Assert.Single(JsonSerializer.Deserialize<string[]>(note.SourceMessageIdsJson)!);
                Assert.Contains("我叫小明", f.Text(1));
                Assert.Equal(1, f.Provider.Requests[1].Messages.Count(m => m.Role == "user" && m.Content == "我叫小明，請記住。"));
                break;
            }
            case "correction":
            case "expired_note_update":
            {
                var original = f.Note("job", "工程師", scenario == "expired_note_update" ? "2000-01-01" : null);
                f.Script(_ => Call("note_save", new { note_id = original.NoteId, content = "設計師", expiry = new { mode = "never" } }), _ => Reply("好的。"));
                await f.Chat("更正成設計師。");
                var updated = Assert.Single(f.Memory.ListNotes(Fixture.UserId));
                Assert.Equal(original.NoteId, updated.NoteId);
                Assert.Equal(2, updated.Version);
                Assert.Equal("設計師", updated.Content);
                Assert.Equal(2, f.Memory.AdminNotes(Fixture.UserId).Count);
                Assert.Null(updated.ExpiresAt);
                break;
            }
            case "forget":
            case "retell":
            {
                f.Seed("秘密是紅茶", room);
                var note = f.Note("tea", "秘密是紅茶");
                f.Script(_ => Call("forget_find", new { note_ids = new[] { note.NoteId } }), request => Call("forget", new { selection_id = LastData(request).GetProperty("selection_id").GetString() }), _ => Reply("已忘掉指定資料。"));
                var result = await f.Chat("忘記紅茶。");
                Assert.Null(result.Error);
                Assert.Empty(f.Memory.ListNotes(Fixture.UserId));
                Assert.DoesNotContain("秘密是紅茶", f.Text(2));
                Assert.DoesNotContain("秘密是紅茶", string.Join("", f.Provider.Requests[2].Messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.FunctionArguments)));
                Assert.All(f.Provider.Requests[2].Messages.Where(m => m.Role == "tool" && m.Name == "forget"), m => Assert.DoesNotContain("紅茶", m.Content));
                Assert.Contains(f.Memory.GetHideReferences(Fixture.UserId), r => r.Kind == "message");
                if (scenario == "retell")
                {
                    f.Script(_ => Call("note_save", new { key = "tea", content = "秘密是綠茶" }), _ => Reply("知道了。"));
                    await f.Chat("重新告訴你，秘密是綠茶。");
                    Assert.Equal("秘密是綠茶", Assert.Single(f.Memory.ListNotes(Fixture.UserId)).Content);
                    Assert.Contains(f.Memory.GetHideReferences(Fixture.UserId), r => r.IsActive);
                    Assert.DoesNotContain("秘密是紅茶", f.Text(3));
                    Assert.Throws<NyaMemoryException>(() => f.Memory.Unforget(Fixture.UserId, f.Memory.GetHideReferences(Fixture.UserId)[0].ForgetId));
                }
                break;
            }
            case "cross_topic_recall":
            {
                var old = f.Seed("旅程安排台南", room);
                f.Memory.CreateNewTopic(Fixture.UserId, "別的聊天室");
                f.Script(_ => Call("search_history", new { query = "台南" }), request =>
                {
                    var data = LastData(request);
                    var hit = Assert.Single(data.GetProperty("results").EnumerateArray(), item => item.GetProperty("message_id").GetString() == old.MessageId);
                    Assert.Equal(old.MessageId, hit.GetProperty("message_id").GetString());
                    Assert.False(hit.GetProperty("in_current_context").GetBoolean());
                    return Call("read_history", new { message_id = old.MessageId, before = 0, after = 1 });
                }, request => { Assert.Contains("旅程安排台南", request.Messages.Last().Content); return Reply("我們安排過台南。" ); });
                var recallResult = await f.Chat("回想台南旅程。");
                Assert.True(recallResult.Error == null, string.Join("\n", f.Services.GetRequiredService<NyaAuditLogger>().GetByTraceId(f.Memory.ListTurns(Fixture.UserId)[0].TurnId).Where(l => l.Action == "error").Select(l => l.DetailJson)));
                break;
            }
            case "new_topic_mid_turn":
            case "switch_back":
            case "A_B_C":
            {
                f.Seed("A 的歷史", room);
                var b = f.Memory.CreateNewTopic(Fixture.UserId, "B");
                f.Seed("B 的歷史", b.TopicId);
                var c = f.Memory.CreateNewTopic(Fixture.UserId, "C");
                f.Seed("C 的歷史", c.TopicId);
                f.Memory.SwitchTopic(Fixture.UserId, room);
                if (scenario == "new_topic_mid_turn")
                    f.Script(_ => Call("new_topic", new { title = "新房間" }), request => { Assert.DoesNotContain("A 的歷史", f.RequestText(request)); return Reply("好的。" ); });
                else if (scenario == "switch_back")
                    f.Script(_ => Call("switch_topic", new { topic = b.TopicId }), request => { Assert.Contains("B 的歷史", f.RequestText(request)); return Call("switch_topic", new { topic = room }); }, request => { Assert.Contains("A 的歷史", f.RequestText(request)); return Reply("好的。" ); });
                else
                    f.Script(_ => Calls(("switch_topic", new { topic = b.TopicId }), ("switch_topic", new { topic = c.TopicId })), request => { Assert.Contains("C 的歷史", f.RequestText(request)); Assert.DoesNotContain("B 的歷史", f.RequestText(request)); return Reply("好的。" ); });
                var result = await f.Chat("換聊天室。");
                Assert.Null(result.Error);
                var turn = Assert.Single(f.Memory.ListTurns(Fixture.UserId), t => t.Status == "completed" && t.StartTopicId == room && t.TurnId.StartsWith("nyat", StringComparison.Ordinal));
                Assert.Equal(result.TopicId, turn.CurrentTopicId);
                var user = f.Memory.GetAllMessages(Fixture.UserId).Single(m => m.TurnId == turn.TurnId && m.Role == "user");
                Assert.Equal(result.TopicId, user.TopicId);
                Assert.Equal(room, user.OriginTopicId);
                Assert.Equal(room, f.Memory.GetAllMessages(Fixture.UserId).Single(m => m.Content == "A 的歷史").TopicId);
                break;
            }
            case "ordered_batch":
                f.Script(_ => Calls(("new_topic", new { title = "first" }), ("rename_topic", new { title = "last" }), ("note_save", new { key = "batch", content = "批次筆記" })), _ => Reply("好的。"));
                var batchResult = await f.Chat("依序開房、更名、保存。");
                Assert.Null(batchResult.Error);
                Assert.Equal("last", f.Memory.GetActiveTopic(Fixture.UserId)!.Title);
                Assert.Equal(batchResult.TopicId, f.Memory.GetMessage(Fixture.UserId, JsonSerializer.Deserialize<string[]>(Assert.Single(f.Memory.ListNotes(Fixture.UserId)).SourceMessageIdsJson)![0])!.TopicId);
                break;
            case "invalid_arguments":
                f.Script(_ => Calls(("note_save", new { key = "bad", content = 3 }), ("topic_list", new { limit = "10" }), ("new_topic", new { surprise = true })), request =>
                {
                    Assert.All(request.Messages.Where(m => m.Role == "tool"), m => Assert.False(JsonDocument.Parse(m.Content).RootElement.GetProperty("ok").GetBoolean()));
                    Assert.Contains("content", request.Messages.First(m => m.Role == "tool").Content);
                    return Reply("參數有誤。" );
                });
                Assert.Null((await f.Chat("測試壞參數。")).Error);
                Assert.Empty(f.Memory.ListNotes(Fixture.UserId));
                Assert.Single(f.Memory.ListTopicsWithCount(Fixture.UserId));
                break;
            case "tool_success_reply_failure":
            case "retry_after_failure":
                f.Script(_ => Call("note_save", new { key = "once", content = "完成的筆記" }), _ => null);
                Assert.NotNull((await f.Chat("請記下這件事。")).Error);
                Assert.Equal("failed", Assert.Single(f.Memory.ListTurns(Fixture.UserId)).Status);
                Assert.Single(f.Memory.GetAllMessages(Fixture.UserId));
                Assert.Single(f.Memory.ListNotes(Fixture.UserId));
                if (scenario == "retry_after_failure")
                {
                    f.Script(request => { Assert.Contains("此回合未產生成功回覆；以下操作已完成：", f.RequestText(request)); Assert.Contains("note_save", f.RequestText(request)); return Reply("先前已記住這件事。" ); });
                    Assert.Null((await f.Chat("再試一次。")).Error);
                    Assert.Equal(1, Assert.Single(f.Memory.ListNotes(Fixture.UserId)).Version);
                }
                break;
            case "overlapping_forget":
            {
                var message = f.Seed("重疊秘密", room);
                f.Script(_ => Call("forget", new { message_ids = new[] { message.MessageId } }), _ => Reply("好的。"));
                await f.Chat();
                f.Script(_ => Call("forget", new { message_ids = new[] { message.MessageId } }), _ => Reply("好的。"));
                await f.Chat();
                var groups = f.Memory.GetHideReferences(Fixture.UserId).Select(r => r.ForgetId).Distinct().ToArray();
                Assert.Equal(2, groups.Length);
                Assert.True(f.Memory.Unforget(Fixture.UserId, groups[0]));
                Assert.True(f.Memory.IsHidden(Fixture.UserId, "message", message.MessageId));
                Assert.True(f.Memory.Unforget(Fixture.UserId, groups[1]));
                Assert.False(f.Memory.IsHidden(Fixture.UserId, "message", message.MessageId));
                break;
            }
            case "long_message":
            {
                var source = f.Seed(new string('長', 199) + "😀" + new string('長', 304) + "尾端", room);
                var all = "";
                f.Script(_ => Call("read_history", new { message_id = source.MessageId, before = 0, after = 0, segment_length = 200 }), request => Next(request), request => Next(request), request =>
                {
                    var data = LastData(request); all += data.GetProperty("results")[0].GetProperty("text").GetString();
                    Assert.True(data.GetProperty("eof").GetBoolean());
                    Assert.True(data.GetProperty("results")[0].GetProperty("message_eof").GetBoolean());
                    return Reply("讀完了。" );
                });
                NyaLlmResponse Next(NyaLlmRequest request)
                {
                    var data = LastData(request); all += data.GetProperty("results")[0].GetProperty("text").GetString();
                    Assert.False(data.GetProperty("eof").GetBoolean());
                    return Call("read_history", new { message_id = source.MessageId, before = 0, after = 0, segment_length = 200, cursor = data.GetProperty("cursor").GetString() });
                }
                Assert.Null((await f.Chat()).Error);
                Assert.Equal(source.Content, all);
                break;
            }
            case "duplicate_title":
                f.Memory.CreateNewTopic(Fixture.UserId, "重複");
                var latest = f.Memory.CreateNewTopic(Fixture.UserId, "重複");
                f.Script(_ => Call("switch_topic", new { topic = "重複" }), request => { var envelope = JsonDocument.Parse(request.Messages.Last().Content).RootElement; Assert.Equal("needs_confirmation", envelope.GetProperty("outcome").GetString()); Assert.Equal(2, envelope.GetProperty("data").GetProperty("candidates").GetArrayLength()); return Reply("請選擇聊天室。" ); });
                await f.Chat();
                Assert.Equal(latest.TopicId, f.Memory.GetActiveTopic(Fixture.UserId)!.TopicId);
                break;
            case "cross_scope":
            {
                var secret = f.Seed("一般秘密", room);
                var note = f.Note("scope", "一般筆記");
                f.Script(request => { Assert.Contains("外部明確會話不可用", f.RequestText(request)); Assert.DoesNotContain("一般筆記", f.RequestText(request)); return Calls(("read_history", new { message_id = secret.MessageId }), ("forget", new { note_ids = new[] { note.NoteId } }), ("new_topic", new { title = "禁止" })); }, request => { Assert.All(request.Messages.Where(m => m.Role == "tool"), m => Assert.False(JsonDocument.Parse(m.Content).RootElement.GetProperty("ok").GetBoolean())); return Reply("不能跨範圍操作。" ); });
                Assert.Null((await f.Chat("外部測試", "explicit-room")).Error);
                Assert.Single(f.Memory.ListNotes(Fixture.UserId));
                Assert.Single(f.Memory.ListTopicsWithCount(Fixture.UserId));
                break;
            }
            case "pending_restart":
                f.Memory.BeginTurn(Fixture.UserId, "pending", room, "未完成", null);
                var restarted = new NyaMemoryService(f.Services.GetRequiredService<NyaDb>().Db, f.Services.GetRequiredService<INyaConfigStore<NyaChatConfig>>(), NullLogger<NyaMemoryService>.Instance);
                Assert.Equal("failed", restarted.GetTurn(Fixture.UserId, "pending")!.Status);
                Assert.Equal("broker_restarted", restarted.GetTurn(Fixture.UserId, "pending")!.FailureReason);
                f.Script(request => { Assert.Contains("此回合未產生成功回覆", f.RequestText(request)); return Reply("可以繼續。" ); });
                Assert.Null((await f.Chat()).Error);
                break;
            case "cursor_after_forget":
            {
                var m1 = f.Seed("游標共同詞一", room); f.Seed("游標共同詞二", room);
                string? cursor = null;
                f.Script(_ => Call("search_history", new { query = "游標共同詞", limit = 1 }), request => { cursor = LastData(request).GetProperty("cursor").GetString(); return Call("forget", new { message_ids = new[] { m1.MessageId } }); }, _ => Call("search_history", new { query = "游標共同詞", limit = 1, cursor }), request => { Assert.Contains("cursor_expired", request.Messages.Last().Content); return Reply("需要重新搜尋。" ); });
                Assert.Null((await f.Chat()).Error);
                break;
            }
            case "new_message_after_selection":
            {
                var old = f.Seed("固定候選詞", room);
                NyaMessage? fresh = null;
                f.Script(_ => Call("forget_find", new { terms = new[] { "固定候選詞" } }), request => { var selection = LastData(request).GetProperty("selection_id").GetString(); fresh = f.Seed("固定候選詞新的訊息", room); return Call("forget", new { selection_id = selection }); }, _ => Reply("好的。"));
                await f.Chat();
                Assert.True(f.Memory.IsHidden(Fixture.UserId, "message", old.MessageId));
                Assert.False(f.Memory.IsHidden(Fixture.UserId, "message", fresh!.MessageId));
                break;
            }
            case "note_capacity":
                f.Config.NoteCapacity = 1;
                f.Note("full", "第一則");
                f.Script(_ => Call("note_save", new { key = "overflow", content = "第二則" }), request => { Assert.Contains("筆記已滿", JsonDocument.Parse(request.Messages.Last().Content).RootElement.GetProperty("error").GetProperty("message").GetString()); return Reply("筆記容量已滿。" ); });
                await f.Chat();
                Assert.Single(f.Memory.ListNotes(Fixture.UserId));
                Assert.Throws<NyaMemoryException>(() => f.Note("admin", "第三則"));
                break;
        }
        Assert.Equal(f.Provider.Script.Count, f.Provider.Position);
    }

    [Fact]
    public async Task HardBatchCap_AppliesToCorrectionAndCatalogueRemainsComplete()
    {
        using var f = new Fixture(); f.Config.MaxToolRounds = 1;
        f.Script(_ => Call("note_save", new { key = "first", content = "第一則" }), _ => Reply("已開新話題。"), request =>
        {
            Assert.Null(request.Tools);
            Assert.Contains("本輪工具回合已用完", f.RequestText(request));
            Assert.Contains("new_topic", f.RequestText(request));
            return Call("new_topic", new { title = "不能建立" });
        });
        await f.Chat();
        Assert.Single(f.Memory.ListTopicsWithCount(Fixture.UserId));
        Assert.Single(f.Memory.ListNotes(Fixture.UserId));
        Assert.Equal(3, f.Provider.Position);
    }

    [Fact]
    public async Task NoteVersionsAndDuplicateTopicsCanBeReadToEofWithStablePages()
    {
        using var f = new Fixture();
        var note = f.Note("versioned", "過期版本", "2000-01-01");
        f.Memory.SaveNote(new NyaToolContext { UserId = Fixture.UserId }, note.NoteId, null, "有效版本", "never", null, []);
        f.Note("another", "第二則笔記");
        for (var i = 0; i < 3; i++) f.Memory.CreateNewTopic(Fixture.UserId, "重複標題");
        var versions = new HashSet<string>();
        var topics = new HashSet<string>();
        var expiredSeen = false;
        NyaLlmResponse Notes(NyaLlmRequest request)
        {
            var data = LastData(request);
            var row = Assert.Single(data.GetProperty("results").EnumerateArray());
            versions.Add(row.GetProperty("version_id").GetString()!);
            expiredSeen |= row.GetProperty("expired").GetBoolean();
            Assert.Equal(JsonValueKind.Array, row.GetProperty("source_message_ids").ValueKind);
            return data.GetProperty("eof").GetBoolean() ? Call("topic_list", new { limit = 1 }) : Call("note_list", new { limit = 1, cursor = data.GetProperty("cursor").GetString() });
        }
        NyaLlmResponse Topics(NyaLlmRequest request)
        {
            var data = LastData(request);
            topics.Add(Assert.Single(data.GetProperty("results").EnumerateArray()).GetProperty("topic_id").GetString()!);
            return data.GetProperty("eof").GetBoolean() ? Reply("讀完了。") : Call("topic_list", new { limit = 1, cursor = data.GetProperty("cursor").GetString() });
        }
        f.Script(_ => Call("note_list", new { limit = 1 }), Notes, Notes, Notes, Topics, Topics, Topics);
        Assert.Null((await f.Chat()).Error);
        Assert.Equal(3, versions.Count);
        Assert.Equal(3, topics.Count);
        Assert.True(expiredSeen);
    }

    [Fact]
    public async Task ExternalScopeCanReuseNormalTopicIdWithoutReadingItsData()
    {
        using var f = new Fixture();
        var room = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var normal = f.Seed("normal-secret", room);
        f.Script(_ => Call("search_history", new { query = "normal-secret", topic = "current" }), request => { Assert.Equal(0, LastData(request).GetProperty("total").GetInt32()); return Reply("找不到。" ); });
        Assert.Null((await f.Chat("external-text", room)).Error);
        Assert.Contains(room, f.Memory.GetExternalConversationIds(Fixture.UserId));
        Assert.Equal(2, Assert.Single(f.Memory.ListTopicsWithCount(Fixture.UserId)).MsgCount);
        var external = f.Memory.VisibleMessages(new NyaToolContext { UserId = Fixture.UserId, Scope = "external", ExternalConversationId = room });
        Assert.DoesNotContain(external, m => m.MessageId == normal.MessageId);
        Assert.Contains(external, m => m.Content == "external-text");
    }

    [Fact]
    public async Task UserQueueAndAdminMutationsShareTheSameGate()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.Before = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        f.Script(_ => Reply("第一輪完成。"), _ => Reply("第二輪完成。"));
        var first = f.Chat("第一輪");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Chat("第二輪");
        var admin = f.Memory.LockUserAsync(Fixture.UserId);
        Assert.False(second.IsCompleted);
        Assert.False(admin.IsCompleted);
        Assert.Single(f.Memory.ListTurns(Fixture.UserId));
        release.SetResult();
        Assert.Null((await first).Error);
        Assert.Null((await second).Error);
        using var adminLease = await admin;
        Assert.Equal(2, f.Memory.ListTurns(Fixture.UserId).Count);
        Assert.Equal(new[] { "第一輪", "第二輪" }, f.Memory.GetAllMessages(Fixture.UserId).Where(m => m.Role == "user").OrderBy(m => m.Sequence).Select(m => m.Content));
    }

    [Fact]
    public void SummaryDependenciesPropagateAndVisibilityVersionsAreValidated()
    {
        using var f = new Fixture();
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var source = f.Seed("摘要來源秘密", topic);
        var first = f.Memory.CreateSummary(Fixture.UserId, topic, "第一份摘要", source.Sequence, source.Sequence, 1);
        var later = f.Seed("第二段來源", topic);
        var second = f.Memory.CreateSummary(Fixture.UserId, topic, "繼承第一份摘要", later.Sequence, later.Sequence, 1, f.Memory.VisibilityVersion(Fixture.UserId), [first.SummaryId]);
        Assert.Contains(source.MessageId, second.SourceMessageIdsJson);
        var version = f.Memory.VisibilityVersion(Fixture.UserId);
        f.Memory.Forget(new NyaToolContext { UserId = Fixture.UserId }, null, [], [source.MessageId]);
        Assert.Empty(f.Memory.GetActiveSummaries(Fixture.UserId, topic));
        Assert.Throws<NyaMemoryException>(() => f.Memory.CreateSummary(Fixture.UserId, topic, "過時提交", later.Sequence, later.Sequence, 1, version));
        Assert.Null(f.Memory.RollbackSummary(Fixture.UserId, first.SummaryId));
    }

    [Fact]
    public void RestoreAndUnforgetHonorCapacityAndOnlyUndoTheirOwnReferences()
    {
        using var f = new Fixture();
        var note = f.Note("hidden", "原始筆記");
        var context = new NyaToolContext { UserId = Fixture.UserId };
        var result = f.Memory.Forget(context, null, [note.NoteId], []);
        var id = JsonDocument.Parse(result.LlmContent).RootElement.GetProperty("data").GetProperty("forget_id").GetString()!;
        f.Config.NoteCapacity = 1;
        f.Note("current", "目前筆記");
        Assert.Throws<NyaMemoryException>(() => f.Memory.Unforget(Fixture.UserId, id));
        Assert.True(f.Memory.IsHidden(Fixture.UserId, "note", note.NoteId));
        f.Config.NoteCapacity = 2;
        Assert.True(f.Memory.Unforget(Fixture.UserId, id));
        Assert.Equal(2, f.Memory.ListNotes(Fixture.UserId).Count);
        Assert.False(f.Memory.Unforget(Fixture.UserId, id));
        var newer = f.Memory.SaveNote(context, note.NoteId, null, "第二版本", "never", null, []);
        f.Config.NoteMaxLength = 2;
        Assert.Throws<NyaMemoryException>(() => f.Memory.RestoreNote(context, note.NoteId, 1));
        Assert.Equal(newer.Version, f.Memory.ListNotes(Fixture.UserId).Single(n => n.NoteId == note.NoteId).Version);
    }

    [Fact]
    public async Task CompleteNotesAndUnavailableCatalogueNeverDisappearUnderBudgetPressure()
    {
        using var f = new Fixture();
        var note = f.Note("visible", "完整有效筆記");
        f.Services.GetRequiredService<NyaToolRegistry>().SetEnabled("note_save", false);
        f.Script(request => { Assert.Contains("完整有效筆記", f.RequestText(request)); Assert.Contains("停用", f.RequestText(request)); Assert.Contains("note_save", f.RequestText(request)); Assert.DoesNotContain(request.Tools!, t => t.Function.Name == "note_save"); return Reply("好的。" ); });
        Assert.Null((await f.Chat()).Error);
        f.Config.DefaultMaxContextTokens = 512;
        Assert.Equal("context_capacity", (await f.Chat()).Error);
        Assert.Equal(note.NoteId, Assert.Single(f.Memory.ListNotes(Fixture.UserId)).NoteId);
        Assert.Equal(1, f.Provider.Position);
        Assert.Throws<NyaMemoryException>(() => f.Memory.SaveNote(new NyaToolContext { UserId = Fixture.UserId, NoteTokenBudget = 0 }, null, "overflow", "筆記", "never", null, []));
    }

    [Fact]
    public async Task ExpiredAndForeignCursorsAreErrorsAndHistoryFilteringPrecedesLimit()
    {
        using var f = new Fixture();
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var m1 = f.Seed("term one", topic);
        var m2 = f.Seed("term two", topic);
        var hidden = f.Seed("term newest", topic);
        var context = new NyaToolContext { UserId = Fixture.UserId, CurrentContextMessageIds = new HashSet<string> { m2.MessageId } };
        f.Memory.Forget(context, null, [], [hidden.MessageId]);
        var page = f.Memory.SearchHistory(context, "term", "all", null, null, null, 1, null);
        var data = JsonDocument.Parse(page.LlmContent).RootElement.GetProperty("data");
        Assert.Equal(m2.MessageId, data.GetProperty("results")[0].GetProperty("message_id").GetString());
        Assert.True(data.GetProperty("results")[0].GetProperty("in_current_context").GetBoolean());
        var cursor = data.GetProperty("cursor").GetString()!;
        Assert.Throws<NyaMemoryException>(() => f.Memory.SearchHistory(context, "changed", "all", null, null, null, 1, cursor));
        Assert.Throws<NyaMemoryException>(() => f.Memory.SearchHistory(new NyaToolContext { UserId = "other" }, "term", "all", null, null, null, 1, cursor));
        f.Services.GetRequiredService<NyaDb>().Db.Execute("UPDATE nya_history_cursors SET expires_at = @expired WHERE cursor_id = @cursor", new { expired = DateTime.UtcNow.AddMinutes(-1), cursor });
        Assert.Throws<NyaMemoryException>(() => f.Memory.SearchHistory(context, "term", "all", null, null, null, 1, cursor));
        f.Script(_ => Call("search_history", new { query = "term", match = "all", from_date = "2099-01-01" }), request => { Assert.Equal(0, LastData(request).GetProperty("total").GetInt32()); return Reply("沒有符合日期的資料。" ); });
        Assert.Null((await f.Chat()).Error);
        Assert.False(f.Memory.IsHidden(Fixture.UserId, "message", m1.MessageId));
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("openai_chat")]
    [InlineData("openai_responses")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task EveryProviderForwardsTheFullJsonSchema(string key)
    {
        using var f = new Fixture();
        using var http = new CaptureFactory();
        INyaLlmProvider provider = key switch
        {
            "ollama" => new OllamaProvider(http, NullLogger<OllamaProvider>.Instance),
            "openai_chat" => new OpenAiChatProvider(http, NullLogger<OpenAiChatProvider>.Instance),
            "openai_responses" => new OpenAiResponsesProvider(http, NullLogger<OpenAiResponsesProvider>.Instance),
            "anthropic" => new AnthropicProvider(http, NullLogger<AnthropicProvider>.Instance),
            _ => new GeminiProvider(http, NullLogger<GeminiProvider>.Instance)
        };
        var tools = f.Services.GetRequiredService<NyaToolRegistry>().BuildLlmTools(Fixture.UserId, "web");
        await provider.SendAsync(new NyaLlmProviderRequest { Profile = new NyaLlmProfile { Provider = key, BaseUrl = "http://scripted.invalid/api", Model = "fake" },
            Request = new NyaLlmRequest { Model = "fake", Tools = tools, Messages = [new NyaLlmMessage { Role = "user", Content = "test" }] } }, CancellationToken.None);
        using var doc = JsonDocument.Parse(http.Body!);
        var wireTools = key == "gemini" ? doc.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations") : doc.RootElement.GetProperty("tools");
        var wire = wireTools.EnumerateArray().First(t => (key is "ollama" or "openai_chat" ? t.GetProperty("function") : t).GetProperty("name").GetString() == "note_save");
        var schema = key switch { "anthropic" => wire.GetProperty("input_schema"), "ollama" or "openai_chat" => wire.GetProperty("function").GetProperty("parameters"), "gemini" => wire.GetProperty("parametersJsonSchema"), _ => wire.GetProperty("parameters") };
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(2, schema.GetProperty("anyOf").GetArrayLength());
        Assert.Equal(1, schema.GetProperty("allOf").GetArrayLength());
        Assert.Equal(1, schema.GetProperty("properties").GetProperty("content").GetProperty("minLength").GetInt32());
        Assert.Equal(100000, schema.GetProperty("properties").GetProperty("content").GetProperty("maxLength").GetInt32());
        Assert.Equal(3, schema.GetProperty("properties").GetProperty("expiry").GetProperty("properties").GetProperty("mode").GetProperty("enum").GetArrayLength());
        Assert.Contains("d{4}", schema.GetProperty("properties").GetProperty("expiry").GetProperty("properties").GetProperty("date").GetProperty("pattern").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("source_message_ids").GetProperty("items").GetProperty("type").GetString());
    }

    private sealed class CaptureFactory : HttpMessageHandler, IHttpClientFactory
    {
        public string? Body { get; private set; }
        public List<string> Bodies { get; } = [];
        public Queue<string> Responses { get; } = new();
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(Body);
            if (Responses.Count > 0) return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Responses.Dequeue()) };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"ok\"},\"choices\":[{\"message\":{\"content\":\"ok\",\"reasoning_content\":\"private thinking\"}}],\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}],\"output_text\":\"ok\"}") };
        }
    }

    [Theory]
    [InlineData("noop", NyaToolExecStatus.Noop)]
    [InlineData("partial", NyaToolExecStatus.Partial)]
    [InlineData("needs_confirmation", NyaToolExecStatus.NeedsConfirmation)]
    public void PlannerDoesNotTreatNonCompletedOutcomesAsCompleted(string outcome, NyaToolExecStatus expected)
    {
        var result = NyaToolResult.Envelope(new { id = "id" }, outcome, outcome == "partial" ? new object[] { new { id = "one", outcome = "completed" }, new { id = "two", outcome = "error" } } : []);
        Assert.Equal(expected, NyaToolStatusFormatter.FromResult(result));
        Assert.DoesNotContain("操作成功", NyaToolStatusFormatter.FormatFallback([expected]));
        var ledger = new List<(NyaToolCall Call, NyaToolExecStatus Status)> { (new NyaToolCall { FunctionName = "note_save" }, expected) };
        var method = typeof(NyaToolReplyPlanner).GetMethod("FindUnbackedActions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Contains("記住", (IReadOnlyList<string>)method.Invoke(null, new object[] { "已記住。", ledger })!);
        using var json = JsonDocument.Parse(result.LlmContent);
        Assert.Equal(new[] { "ok", "outcome", "error", "data", "effects" }, json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("{\"key\":\"k\",\"content\":3}", "content")]
    [InlineData("{\"key\":\" \",\"content\":\"a\"}", "key")]
    [InlineData("{\"key\":\"k\",\"content\":\"a\",\"surprise\":true}", "surprise")]
    [InlineData("{\"note_id\":\"n\",\"key\":\"k\",\"content\":\"a\"}", "key|note_id")]
    [InlineData("{\"key\":\"k\",\"content\":\"a\",\"expiry\":{\"mode\":\"on_date\"}}", "expiry.date")]
    [InlineData("{\"key\":\"k\",\"content\":\"a\",\"source_message_ids\":[3]}", "source_message_ids[0]")]
    [InlineData("{\"content\":\"a\"}", "key|note_id")]
    [InlineData("true", "arguments")]
    public void ValidationUsesTheSameConstraintsAndReportsTheExactField(string arguments, string field)
    {
        using var f = new Fixture();
        var schema = f.Services.GetRequiredService<NyaToolRegistry>().GetAllSchemas().Single(s => s.Name == "note_save");
        var error = NyaToolArgValidator.ValidateError(schema, arguments);
        Assert.NotNull(error);
        Assert.Equal(field, error.Field);
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void SelectionsAndCursorsRejectLaterNoteEditsAndMessageDeletion()
    {
        using var f = new Fixture();
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var original = f.Seed("fixed term old", topic);
        var note = f.Note("fixed", "old content");
        var context = new NyaToolContext { UserId = Fixture.UserId };
        var selection = JsonDocument.Parse(f.Memory.FindForget(context, [], [note.NoteId], "any").LlmContent).RootElement.GetProperty("data").GetProperty("selection_id").GetString();
        var later = f.Seed("fixed term new", topic);
        f.Memory.SaveNote(context, note.NoteId, null, "new content", "never", null, [later.MessageId]);
        Assert.Throws<NyaMemoryException>(() => f.Memory.Forget(context, selection, [], []));
        Assert.False(f.Memory.IsHidden(Fixture.UserId, "message", later.MessageId));
        var first = JsonDocument.Parse(f.Memory.SearchHistory(context, "fixed term", "all", null, null, null, 1, null).LlmContent).RootElement.GetProperty("data");
        var cursor = first.GetProperty("cursor").GetString();
        Assert.NotNull(cursor);
        f.Memory.SoftDeleteMessage(Fixture.UserId, original.MessageId);
        Assert.Throws<NyaMemoryException>(() => f.Memory.SearchHistory(context, "fixed term", "all", null, null, null, 1, cursor));
    }

    [Fact]
    public async Task RestartRetainsAtomicToolEffectsBeforeTheToolReceiptWasWritten()
    {
        using var f = new Fixture();
        var room = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var turn = f.Memory.BeginTurn(Fixture.UserId, "interrupted-tool", room, "保存這件事", null);
        var source = f.Memory.GetAllMessages(Fixture.UserId).Single(m => m.TurnId == turn.TurnId);
        f.Memory.SaveNote(new NyaToolContext { UserId = Fixture.UserId, TraceId = turn.TurnId, ConversationId = room }, null, "atomic", "已完成的筆記", "never", null, [source.MessageId]);
        Assert.Single(f.Memory.GetTurnWork(turn.TurnId));
        Assert.Equal("{}", f.Memory.GetTurnWork(turn.TurnId)[0].CallJson);
        _ = new NyaMemoryService(f.Services.GetRequiredService<NyaDb>().Db, f.Services.GetRequiredService<INyaConfigStore<NyaChatConfig>>(), NullLogger<NyaMemoryService>.Instance);
        f.Script(request => { Assert.Contains("以下操作已完成：", f.RequestText(request)); Assert.Contains("note_save", f.RequestText(request)); return Reply("先前已記住這件事。"); });
        Assert.Null((await f.Chat("請再試一次。")).Error);
        Assert.Equal(1, Assert.Single(f.Memory.ListNotes(Fixture.UserId)).Version);
        Assert.Equal(1, f.Provider.Position);
    }

    [Fact]
    public void LargeHistoryUsesBoundedSqlReadsAndFiltersHiddenRowsBeforeTheWindow()
    {
        using var f = new Fixture();
        var db = f.Services.GetRequiredService<NyaDb>().Db;
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        db.InTransaction(() =>
        {
            for (var n = 1; n <= 5000; n++) db.Insert(new NyaMessage { MessageId = "large-" + n, UserId = Fixture.UserId,
                TopicId = topic, Sequence = n, Role = "user", Content = "message " + n });
            for (var n = 4801; n <= 5000; n++) db.Insert(new NyaHideReference { ReferenceId = "hide-" + n, ForgetId = "large-forget",
                UserId = Fixture.UserId, Kind = "message", TargetId = "large-" + n });
        });
        var turn = f.Memory.BeginTurn(Fixture.UserId, "large-current", topic, "new question", null);
        var before = f.Memory.SqlReadCount;
        var messages = f.Services.GetRequiredService<NyaPromptBuilder>().BuildTurn(turn, f.Memory, new SoulDefinition(), 5,
            64000, NyaTokenEstimator.Default, "web", out var ids, out _);
        var reads = f.Memory.SqlReadCount - before;
        Assert.InRange(reads, 1, 30);
        Assert.Equal(f.Config.ShortTermMessageLimit + 1, ids.Count);
        Assert.Contains("large-4800", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("large-") && int.Parse(id[6..]) > 4800);
        Assert.Contains(messages, m => m.Content == "message 4800");
    }

    [Fact]
    public void HistoryLabelsUserMessagesAfterLongGaps()
    {
        using var f = new Fixture();
        var db = f.Services.GetRequiredService<NyaDb>().Db;
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        var now = DateTime.UtcNow;
        db.InTransaction(() =>
        {
            db.Insert(new NyaMessage { MessageId = "gap-1", UserId = Fixture.UserId, TopicId = topic, Sequence = 1, Role = "user", Content = "昨天的問題", CreatedAt = now.AddDays(-1) });
            db.Insert(new NyaMessage { MessageId = "gap-2", UserId = Fixture.UserId, TopicId = topic, Sequence = 2, Role = "assistant", Content = "昨天的回答", CreatedAt = now.AddDays(-1).AddMinutes(1) });
            db.Insert(new NyaMessage { MessageId = "gap-3", UserId = Fixture.UserId, TopicId = topic, Sequence = 3, Role = "user", Content = "今天的問題", CreatedAt = now.AddMinutes(-10) });
            db.Insert(new NyaMessage { MessageId = "gap-4", UserId = Fixture.UserId, TopicId = topic, Sequence = 4, Role = "assistant", Content = "今天的回答", CreatedAt = now.AddMinutes(-9) });
            db.Insert(new NyaMessage { MessageId = "gap-5", UserId = Fixture.UserId, TopicId = topic, Sequence = 5, Role = "user", Content = "接著問", CreatedAt = now.AddMinutes(-8) });
            db.Insert(new NyaMessage { MessageId = "gap-6", UserId = Fixture.UserId, TopicId = topic, Sequence = 6, Role = "assistant", Content = "接著答", CreatedAt = now.AddMinutes(-7) });
        });
        var turn = f.Memory.BeginTurn(Fixture.UserId, "gap-current", topic, "現在的問題", null);
        var messages = f.Services.GetRequiredService<NyaPromptBuilder>().BuildTurn(turn, f.Memory, new SoulDefinition(), 5,
            64000, NyaTokenEstimator.Default, "web", out _, out _);
        string Label(DateTime at) => $"[{at.ToLocalTime():yyyy-MM-dd HH:mm}] ";
        Assert.Contains(messages, m => m.Content == Label(now.AddDays(-1)) + "昨天的問題");
        Assert.Contains(messages, m => m.Content == Label(now.AddMinutes(-10)) + "今天的問題");
        Assert.Contains(messages, m => m.Content == "接著問");
        Assert.Contains(messages, m => m.Content == "昨天的回答");
        Assert.Contains(messages, m => m.Content == "現在的問題");
    }

    [Fact]
    public async Task StablePrefixIsIdenticalAcrossToolRoundsAndConsecutiveTurns()
    {
        using var f = new Fixture();
        f.Script(_ => Call("note_list", new { }), _ => Reply("第一輪回覆。"), _ => Reply("第二輪回覆。"));
        Assert.Null((await f.Chat("查看筆記")).Error);
        Assert.Null((await f.Chat("再聊一下")).Error);
        Assert.Equal(3, f.Provider.Requests.Count);
        var prefixes = f.Provider.Requests.Select(r => System.Text.Encoding.UTF8.GetBytes(r.Messages[0].Content)).ToArray();
        Assert.Equal(prefixes[0], prefixes[1]);
        Assert.Equal(prefixes[0], prefixes[2]);
        Assert.NotEqual(f.Provider.Requests[0].Messages[1].Content, f.Provider.Requests[1].Messages[1].Content);
        Assert.DoesNotContain("idempotency", f.Provider.Requests[0].Messages[0].Content);
        Assert.DoesNotContain("broker", f.Provider.Requests[0].Messages[0].Content);
    }

    [Fact]
    public async Task BackgroundSummaryDoesNotBlockReplyAndKeepsTheUserGate()
    {
        using var f = new Fixture();
        f.Config.AutoSummarizeEnabled = true;
        f.Config.SummarizeTriggerCount = 2;
        f.Config.SummarizeKeepRecentCount = 1;
        var topic = f.Memory.GetOrCreateActiveTopic(Fixture.UserId);
        f.Seed("old conversation", topic);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.Before = async ct =>
        {
            if (f.Provider.Requests.Count == 0) return;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        f.Script(_ => Reply("reply returned"), _ => Reply("conversation summary"));
        var reply = await f.Chat().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(reply.Error);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = f.Memory.LockUserAsync(Fixture.UserId);
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        await f.Services.GetRequiredService<NyaSummarizer>().BackgroundCompletion(Fixture.UserId).WaitAsync(TimeSpan.FromSeconds(5));
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(f.Memory.GetActiveSummaries(Fixture.UserId, topic));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("messages")]
    [InlineData("tools")]
    [InlineData("stream")]
    [InlineData("array")]
    public void ExtraBodyRejectsReservedKeysAndNonObjects(string key)
    {
        using var http = new CaptureFactory();
        var entry = new NyaLlmProfileEntry { ProfileId = "deepseek", Provider = "openai_chat", BaseUrl = "https://api.deepseek.com/chat/completions", Model = "deepseek-flash",
            ParamsJson = key == "array" ? "{\"extra_body\":[]}" : JsonSerializer.Serialize(new { extra_body = new Dictionary<string, object> { [key] = "forbidden" } }) };
        Assert.NotNull(NyaLlmAdminSettings.Validate(entry, [new OpenAiChatProvider(http, NullLogger<OpenAiChatProvider>.Instance)]));
    }

    [Fact]
    public async Task DeepSeekTemplateAndEveryToolRoundSendExtraBodyWithoutReasoning()
    {
        var template = NyaLlmProviderTemplates.All.Single(t => t.Id == "deepseek");
        Assert.Equal("DeepSeek", template.Label);
        Assert.Equal("openai_chat", template.Provider);
        Assert.Equal("https://api.deepseek.com/chat/completions", template.DefaultBaseUrl);
        Assert.Equal("deepseek-flash", template.DefaultModel);
        Assert.False(template.DefaultParams.ContainsKey("temperature"));
        Assert.Equal(1.0, template.DefaultParams["top_p"]);
        var entry = new NyaLlmProfileEntry { ProfileId = "deepseek", Provider = template.Provider, BaseUrl = template.DefaultBaseUrl, Model = template.DefaultModel,
            ParamsJson = JsonSerializer.Serialize(new { top_p = 1.0, extra_body = template.ExtraBody }) };
        var profile = entry.ToProfile();
        Assert.False(profile.SendTemperature);
        using var http = new CaptureFactory();
        var provider = new OpenAiChatProvider(http, NullLogger<OpenAiChatProvider>.Instance);
        var history = new List<NyaLlmMessage> { new() { Role = "user", Content = "remember this" } };
        var call = new NyaToolCall { Id = "call-1", FunctionName = "note_list" };
        for (var round = 0; round < 2; round++)
        {
            var response = await provider.SendAsync(new NyaLlmProviderRequest { Profile = profile, Request = new NyaLlmRequest { Model = profile.Model, Messages = history, TopP = 1.0f } }, CancellationToken.None);
            Assert.Equal("ok", response!.Content);
            using var body = JsonDocument.Parse(http.Body!);
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.All(body.RootElement.GetProperty("messages").EnumerateArray(), m => Assert.False(m.TryGetProperty("reasoning_content", out _)));
            history.Add(new NyaLlmMessage { Role = "assistant", ToolCalls = [call] });
            history.Add(new NyaLlmMessage { Role = "tool", ToolCallId = call.Id, Name = call.FunctionName, Content = "result" });
        }
        var original = System.Text.Json.Nodes.JsonNode.Parse("{\"thinking\":{\"budget\":12,\"type\":\"enabled\"}}")!.AsObject();
        OpenAiChatProvider.MergeExtraBody(original, profile.ExtraBody);
        Assert.Equal(12, original["thinking"]!["budget"]!.GetValue<int>());
        Assert.Equal("disabled", original["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeepSeekExtraBodyTravelsThroughTheActualOrchestratorToolLoop()
    {
        using var http = new CaptureFactory();
        var profile = new NyaLlmProfile { Provider = "openai_chat", BaseUrl = "https://api.deepseek.com/chat/completions", Model = "deepseek-flash",
            SendTemperature = false, TopP = 1.0f, ExtraBody = System.Text.Json.Nodes.JsonNode.Parse("{\"thinking\":{\"type\":\"disabled\"}}")!.AsObject() };
        http.Responses.Enqueue("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"reasoning_content\":\"private reasoning first\",\"tool_calls\":[{\"id\":\"note-call\",\"type\":\"function\",\"function\":{\"name\":\"note_list\",\"arguments\":\"{}\"}}]}}]}");
        http.Responses.Enqueue("{\"choices\":[{\"message\":{\"content\":\"目前沒有筆記。\",\"reasoning_content\":\"private reasoning second\"}}]}");
        using var f = new Fixture(profile, http);
        var result = await f.Chat("查看筆記");
        Assert.Null(result.Error);
        Assert.Equal(2, http.Bodies.Count);
        foreach (var wire in http.Bodies)
        {
            using var body = JsonDocument.Parse(wire);
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal(1.0, body.RootElement.GetProperty("top_p").GetDouble());
            Assert.All(body.RootElement.GetProperty("messages").EnumerateArray(), m =>
                Assert.All(m.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "role", "content", "tool_calls", "tool_call_id", "name" })));
            Assert.DoesNotContain("reasoning_content", wire);
        }
        Assert.DoesNotContain(f.Memory.GetAllMessages(Fixture.UserId), m => m.Content.Contains("private reasoning"));
        Assert.DoesNotContain(f.Services.GetRequiredService<NyaAuditLogger>().GetLogs(Fixture.UserId).Logs, l => l.DetailJson.Contains("private reasoning"));
    }

    private static NyaLlmResponse Reply(string text) => new() { Content = text };
    private static NyaLlmResponse Call(string name, object args) => Calls((name, args));
    private static NyaLlmResponse Calls(params (string Name, object Args)[] calls) => new()
    {
        ToolCalls = calls.Select(c => new NyaToolCall { Id = Guid.NewGuid().ToString("N"), FunctionName = c.Name, FunctionArguments = JsonSerializer.Serialize(c.Args) }).ToList()
    };
    private static JsonElement LastData(NyaLlmRequest request) => JsonDocument.Parse(request.Messages.Last(m => m.Role == "tool").Content).RootElement.GetProperty("data");

    private sealed class Fixture : IDisposable
    {
        public const string UserId = "web:memory-redesign";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nya_redesign_" + Guid.NewGuid().ToString("N"));
        public ServiceProvider Services { get; }
        public FakeProvider Provider { get; } = new();
        public NyaChatConfig Config => Services.GetRequiredService<INyaConfigStore<NyaChatConfig>>().Current;
        public NyaMemoryService Memory => Services.GetRequiredService<NyaMemoryService>();
        public Fixture(NyaLlmProfile? profile = null, CaptureFactory? http = null)
        {
            Directory.CreateDirectory(_directory);
            var config = new NyaChatConfig { DbPath = Path.Combine(_directory, "nya.db") + ";Pooling=False", SoulsDirectory = _directory, PromptsDirectory = _directory,
                DefaultMaxContextTokens = 64000, MaxToolRounds = 10, LlmProfiles = new() { ["fake"] = profile ?? new NyaLlmProfile { Provider = "scripted-memory", Model = "fake" } }, TaskRouting = new() { ["chat"] = "fake", ["summarization"] = "fake" } };
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NyaChat:DataDirectory"] = _directory }).Build());
            services.AddNyaChat(config);
            services.AddSingleton<INyaLlmProvider>(Provider);
            if (http != null) services.AddSingleton<IHttpClientFactory>(http);
            Services = services.BuildServiceProvider();
        }
        public void Script(params Func<NyaLlmRequest, NyaLlmResponse?>[] steps) => Provider.Script.AddRange(steps);
        public Task<global::Broker.NyaChat.Abstractions.NyaChatResult> Chat(string message = "請處理。", string? conversation = null)
            => Services.GetRequiredService<INyaChatOrchestrator>().ChatAsync(new NyaChatRequest { ChannelType = "web", ChannelUserId = "memory-redesign", Message = message, ConversationId = conversation }, CancellationToken.None);
        public NyaMessage Seed(string message, string topic)
        {
            var turn = Memory.BeginTurn(UserId, Guid.NewGuid().ToString("N"), topic, message, null);
            Memory.FinishTurn(turn, "原始回覆");
            return Memory.GetAllMessages(UserId).Single(m => m.TurnId == turn.TurnId && m.Role == "user");
        }
        public NyaNote Note(string key, string content, string? date = null)
        {
            var source = Memory.GetAllMessages(UserId).FirstOrDefault(m => m.Role == "user");
            return Memory.SaveNote(new NyaToolContext { UserId = UserId }, null, key, content, date == null ? "never" : "on_date", date, source == null ? [] : [source.MessageId]);
        }
        public string Text(int index) => RequestText(Provider.Requests[index]);
        public string RequestText(NyaLlmRequest request) => string.Join("\n", request.Messages.Select(m => m.Content));
        public void Dispose() { Services.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private sealed class FakeProvider : INyaLlmProvider
    {
        public string Key => "scripted-memory";
        public List<Func<NyaLlmRequest, NyaLlmResponse?>> Script { get; } = [];
        public List<NyaLlmRequest> Requests { get; } = [];
        public int Position { get; private set; }
        public Func<CancellationToken, Task>? Before { get; set; }
        public async Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct)
        {
            if (Before != null) await Before(ct);
            Requests.Add(request.Request);
            Assert.True(Position < Script.Count, "Unexpected model call");
            return Script[Position++](request.Request);
        }
    }
}
