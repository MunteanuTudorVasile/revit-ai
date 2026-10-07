namespace RevitAi.Core.Ai;

/// <summary>
/// Conversation history for one document. Stored as whole turns so a tool call is never
/// separated from its output when old turns are dropped.
/// </summary>
public sealed class Conversation
{
    private readonly List<IReadOnlyList<AiItem>> _turns = [];
    private readonly int _maxTurns;

    public Conversation(int maxTurns = 10)
    {
        _maxTurns = maxTurns;
    }

    public IReadOnlyList<AiItem> Items => _turns.SelectMany(turn => turn).ToList();

    public int TurnCount => _turns.Count;

    public void AddTurn(IReadOnlyList<AiItem> items)
    {
        _turns.Add(items);
        while (_turns.Count > _maxTurns)
        {
            _turns.RemoveAt(0);
        }
    }

    public void Clear() => _turns.Clear();
}
