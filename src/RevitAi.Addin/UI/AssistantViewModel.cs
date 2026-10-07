using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RevitAi.Addin.Context;
using RevitAi.Addin.Dispatch;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;

namespace RevitAi.Addin.UI;

public sealed record ChatMessage(string Author, string Text);

/// <summary>
/// Phase 0: no AI. Send echoes the input together with context read through the dispatcher,
/// which proves the panel → dispatcher → Revit API → panel round trip.
/// </summary>
public sealed class AssistantViewModel : INotifyPropertyChanged
{
    private readonly RevitDispatcher _dispatcher;
    private readonly FileLog _log;
    private ModelContext _context = ModelContext.NoDocument;
    private string _input = string.Empty;

    public AssistantViewModel(RevitDispatcher dispatcher, FileLog log)
    {
        _dispatcher = dispatcher;
        _log = log;
        SendCommand = new AsyncCommand(SendAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        Messages.Add(new ChatMessage("Revit AI", "Ask me to work with your Revit model. (Phase 0: messages are echoed, no AI yet.)"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public AsyncCommand SendCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public string Input
    {
        get => _input;
        set
        {
            _input = value;
            OnPropertyChanged();
        }
    }

    public string ContextText => FormatContext(_context);

    /// <summary>Called from Revit events (UI thread, API context).</summary>
    public void UpdateContext(ModelContext context)
    {
        _context = context;
        OnPropertyChanged(nameof(ContextText));
    }

    private async Task SendAsync()
    {
        string text = Input.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Input = string.Empty;
        Messages.Add(new ChatMessage("You", text));

        ModelContext? context = await ReadContextAsync();
        if (context is not null)
        {
            Messages.Add(new ChatMessage("Revit AI", $"Echo: {text}\nRead from Revit just now: {FormatContext(context)}"));
        }
    }

    private async Task RefreshAsync() => await ReadContextAsync();

    /// <summary>Reads the context through the dispatcher and updates the indicator; reports failures in the chat.</summary>
    private async Task<ModelContext?> ReadContextAsync()
    {
        try
        {
            ModelContext context = await _dispatcher.InvokeAsync(ContextReader.Read);
            UpdateContext(context);
            return context;
        }
        catch (TimeoutException ex)
        {
            _log.Warning($"Context read timed out: {ex.Message}");
            Messages.Add(new ChatMessage("Revit AI",
                "Revit didn't respond in time. It may be busy or showing a dialog; close it and try again."));
        }
        catch (Exception ex)
        {
            _log.Error("Context read failed.", ex);
            Messages.Add(new ChatMessage("Revit AI", $"I couldn't read the model: {ex.Message}"));
        }

        return null;
    }

    private static string FormatContext(ModelContext context)
    {
        if (!context.HasDocument)
        {
            return "No project open";
        }

        string level = context.LevelName is null ? "" : $" · {context.LevelName}";
        string selection = context.SelectionCount == 1 ? "1 selected element" : $"{context.SelectionCount} selected elements";
        return $"{context.DocumentTitle}\n{context.ViewName} ({context.ViewType}){level}\n{selection}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
