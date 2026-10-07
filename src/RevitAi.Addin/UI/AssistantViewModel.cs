using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using RevitAi.Addin.Context;
using RevitAi.Addin.Dispatch;
using RevitAi.Addin.Infrastructure;
using RevitAi.Core.Ai;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;

namespace RevitAi.Addin.UI;

public sealed record ChatMessage(string Author, string Text);

/// <summary>Panel state. Phase 1: read-only questions answered by the AI through registered tools.</summary>
public sealed class AssistantViewModel : INotifyPropertyChanged
{
    private const string AssistantName = "Revit AI";

    private readonly RevitDispatcher _dispatcher;
    private readonly Orchestrator _orchestrator;
    private readonly ApiKeyStore _keyStore;
    private readonly string _settingsPath;
    private readonly FileLog _log;
    private readonly Conversation _conversation = new();

    private AddinSettings _settings;
    private ModelContext _context = ModelContext.NoDocument;
    private string _input = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _showKeyPanel;
    private CancellationTokenSource? _cancellation;

    public AssistantViewModel(
        RevitDispatcher dispatcher,
        Orchestrator orchestrator,
        ApiKeyStore keyStore,
        AddinSettings settings,
        string settingsPath,
        FileLog log)
    {
        _dispatcher = dispatcher;
        _orchestrator = orchestrator;
        _keyStore = keyStore;
        _settings = settings;
        _settingsPath = settingsPath;
        _log = log;
        _showKeyPanel = !keyStore.HasKey;

        SendCommand = new AsyncCommand(SendAsync);
        RefreshCommand = new AsyncCommand(async () => await ReadContextAsync());
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        AcceptConsentCommand = new RelayCommand(AcceptConsent);
        ToggleKeyPanelCommand = new RelayCommand(() => ShowKeyPanel = !ShowKeyPanel);
        RemoveKeyCommand = new RelayCommand(RemoveApiKey, () => HasApiKey);

        Messages.Add(new ChatMessage(AssistantName,
            "Ask me about your Revit model, for example: \"What did I select?\", \"What level am I on?\", \"How many doors are on this level?\""));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public AsyncCommand SendCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AcceptConsentCommand { get; }

    public RelayCommand ToggleKeyPanelCommand { get; }

    public RelayCommand RemoveKeyCommand { get; }

    public string Input
    {
        get => _input;
        set => Set(ref _input, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool NeedsConsent => _settings.ConsentAcceptedAt is null;

    public bool HasApiKey => _keyStore.HasKey;

    public bool ShowKeyPanel
    {
        get => _showKeyPanel;
        set => Set(ref _showKeyPanel, value);
    }

    public string ContextText => FormatContext(_context);

    /// <summary>Called from Revit events (UI thread, API context) and after dispatcher reads.</summary>
    public void UpdateContext(ModelContext context)
    {
        if (context.DocumentTitle != _context.DocumentTitle && _conversation.TurnCount > 0)
        {
            _conversation.Clear();
            Messages.Add(new ChatMessage(AssistantName, "The active project changed, so I started a new conversation."));
        }

        _context = context;
        OnPropertyChanged(nameof(ContextText));
    }

    /// <summary>Called by the view with the PasswordBox content; the key is never bound or stored in the view model.</summary>
    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Messages.Add(new ChatMessage(AssistantName, "Paste your OpenAI API key first."));
            return;
        }

        try
        {
            _keyStore.Save(apiKey);
            ShowKeyPanel = false;
            OnPropertyChanged(nameof(HasApiKey));
            RemoveKeyCommand.RaiseCanExecuteChanged();
            Messages.Add(new ChatMessage(AssistantName, "API key saved, encrypted for your Windows account."));
            _log.Info("OpenAI API key saved.");
        }
        catch (Exception ex)
        {
            _log.Error("Saving the API key failed.", ex);
            Messages.Add(new ChatMessage(AssistantName, $"I couldn't save the API key: {ex.Message}"));
        }
    }

    private void RemoveApiKey()
    {
        try
        {
            _keyStore.Delete();
            OnPropertyChanged(nameof(HasApiKey));
            RemoveKeyCommand.RaiseCanExecuteChanged();
            ShowKeyPanel = true;
            Messages.Add(new ChatMessage(AssistantName, "API key removed."));
            _log.Info("OpenAI API key removed.");
        }
        catch (Exception ex)
        {
            _log.Error("Removing the API key failed.", ex);
            Messages.Add(new ChatMessage(AssistantName, $"I couldn't remove the API key: {ex.Message}"));
        }
    }

    private void AcceptConsent()
    {
        AddinSettings accepted = _settings with { ConsentAcceptedAt = DateTimeOffset.Now };
        try
        {
            accepted.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still honour the acceptance for this session; it will be asked again next time.
            _log.Error("Saving consent failed.", ex);
        }

        _settings = accepted;
        OnPropertyChanged(nameof(NeedsConsent));
        _log.Info("Data notice accepted.");
    }

    private async Task SendAsync()
    {
        string text = Input.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (NeedsConsent)
        {
            Messages.Add(new ChatMessage(AssistantName, "Please read and accept the data notice above first."));
            return;
        }

        if (!HasApiKey)
        {
            ShowKeyPanel = true;
            Messages.Add(new ChatMessage(AssistantName, "Add your OpenAI API key first, in the API key panel above."));
            return;
        }

        Input = string.Empty;
        Messages.Add(new ChatMessage("You", text));

        ModelContext? context = await ReadContextAsync();
        if (context is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        var progress = new Progress<string>(label => Status = label);
        try
        {
            AssistantReply reply = await _orchestrator.RunAsync(_conversation, text, context, progress, cancellation.Token);
            Messages.Add(new ChatMessage(AssistantName, reply.Text));
            _log.Info($"Answered using {reply.ToolCalls.Count} tool call(s): {string.Join(", ", reply.ToolCalls.Select(c => c.Name))}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Messages.Add(new ChatMessage(AssistantName, "Cancelled."));
        }
        catch (TaskCanceledException)
        {
            Messages.Add(new ChatMessage(AssistantName,
                $"OpenAI didn't answer within {_settings.AiRequestTimeoutSeconds} seconds. Please try again."));
        }
        catch (AiServiceException ex)
        {
            _log.Warning($"OpenAI request failed ({ex.Failure}): {ex.Message}");
            Messages.Add(new ChatMessage(AssistantName, Describe(ex)));
            if (ex.Failure is AiFailure.MissingApiKey or AiFailure.InvalidApiKey)
            {
                ShowKeyPanel = true;
            }
        }
        catch (HttpRequestException ex)
        {
            _log.Warning($"Could not reach OpenAI: {ex.Message}");
            Messages.Add(new ChatMessage(AssistantName, "I couldn't reach OpenAI. Check your internet connection and try again."));
        }
        catch (Exception ex)
        {
            _log.Error("Answering failed.", ex);
            Messages.Add(new ChatMessage(AssistantName, $"Something went wrong: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
            _cancellation = null;
        }
    }

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
            Messages.Add(new ChatMessage(AssistantName,
                "Revit didn't respond in time. It may be busy or showing a dialog; close it and try again."));
        }
        catch (Exception ex)
        {
            _log.Error("Context read failed.", ex);
            Messages.Add(new ChatMessage(AssistantName, $"I couldn't read the model: {ex.Message}"));
        }

        return null;
    }

    private static string Describe(AiServiceException ex) => ex.Failure switch
    {
        AiFailure.MissingApiKey => "Add your OpenAI API key first, in the API key panel above.",
        AiFailure.InvalidApiKey => "OpenAI rejected the API key. Check it in the API key panel above.",
        AiFailure.RateLimited => $"OpenAI's rate limit or quota was reached ({ex.Message}). Wait a moment, or check your OpenAI billing.",
        AiFailure.BadResponse => "OpenAI returned an answer I couldn't read. Please try again.",
        _ => $"The OpenAI request failed: {ex.Message}",
    };

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

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
