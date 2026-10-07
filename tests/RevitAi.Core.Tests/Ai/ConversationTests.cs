using RevitAi.Core.Ai;

namespace RevitAi.Core.Tests.Ai;

public class ConversationTests
{
    [Fact]
    public void Drops_oldest_whole_turns_beyond_the_limit()
    {
        var conversation = new Conversation(maxTurns: 2);

        conversation.AddTurn([new UserMessage("1"), new ToolCall("a", "t", "{}"), new ToolOutput("a", "{}"), new AssistantMessage("r1")]);
        conversation.AddTurn([new UserMessage("2"), new AssistantMessage("r2")]);
        conversation.AddTurn([new UserMessage("3"), new AssistantMessage("r3")]);

        Assert.Equal(2, conversation.TurnCount);
        Assert.Equal(
            [new UserMessage("2"), new AssistantMessage("r2"), new UserMessage("3"), new AssistantMessage("r3")],
            conversation.Items);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        var conversation = new Conversation();
        conversation.AddTurn([new UserMessage("1")]);

        conversation.Clear();

        Assert.Empty(conversation.Items);
    }
}
