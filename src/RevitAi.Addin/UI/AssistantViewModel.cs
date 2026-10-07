using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using RevitAi.Addin.Context;
using RevitAi.Addin.Dispatch;
using RevitAi.Addin.Infrastructure;
using RevitAi.Addin.Planning;
using RevitAi.Addin.Tools;
using RevitAi.Core.Ai;
using RevitAi.Core.Context;
using RevitAi.Core.Infrastructure;
using RevitAi.Core.Localization;
using RevitAi.Core.Planning;

namespace RevitAi.Addin.UI;

public sealed record ChatMessage(string Author, string Text);

/// <summary>
/// Panel state. The AI answers questions and proposes plans; the model only changes when the user clicks Apply (ADR-024).
/// All panel texts come from <see cref="UiText"/> (English or Romanian).
/// </summary>
public sealed class AssistantViewModel : INotifyPropertyChanged
{
    private const string AssistantName = "Revit AI";
    private const int RecentActionsForContext = 5;

    private readonly RevitDispatcher _dispatcher;
    private readonly Orchestrator _orchestrator;
    private readonly PlanExecutor _executor;
    private readonly ActionHistory _history;
    private readonly ApiKeyStore _keyStore;
    private readonly string _settingsPath;
    private readonly FileLog _log;
    private readonly Conversation _conversation = new();

    private AddinSettings _settings;
    private UiText _text;
    private ModelContext _context = ModelContext.NoDocument;
    private string _input = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _showKeyPanel;
    private CancellationTokenSource? _cancellation;
    private PendingPlan? _plan;
    private bool _previewSucceeded;
    private string _previewText = string.Empty;

    public AssistantViewModel(
        RevitDispatcher dispatcher,
        Orchestrator orchestrator,
        PlanExecutor executor,
        ActionHistory history,
        ApiKeyStore keyStore,
        AddinSettings settings,
        string settingsPath,
        FileLog log)
    {
        _dispatcher = dispatcher;
        _orchestrator = orchestrator;
        _executor = executor;
        _history = history;
        _keyStore = keyStore;
        _settings = settings;
        _settingsPath = settingsPath;
        _log = log;
        _text = new UiText(settings.Language);
        _showKeyPanel = !keyStore.HasKey;

        SendCommand = new AsyncCommand(SendAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(async () => await ReadContextAsync());
        PreviewCommand = new AsyncCommand(PreviewAsync, () => _plan is not null && !IsBusy);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => CanApply);
        DiscardCommand = new RelayCommand(() => DiscardPlan(T["Discarded"]), () => _plan is not null && !IsBusy);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        AcceptConsentCommand = new RelayCommand(AcceptConsent);
        ToggleKeyPanelCommand = new RelayCommand(() => ShowKeyPanel = !ShowKeyPanel);
        RemoveKeyCommand = new RelayCommand(RemoveApiKey, () => HasApiKey);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);

        Say(T["Welcome"]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Panel texts; XAML binds to <c>T[Key]</c>.</summary>
    public UiText T => _text;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public AsyncCommand SendCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AcceptConsentCommand { get; }

    public RelayCommand ToggleKeyPanelCommand { get; }

    public RelayCommand RemoveKeyCommand { get; }

    public RelayCommand ToggleLanguageCommand { get; }

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand ApplyCommand { get; }

    public RelayCommand DiscardCommand { get; }

    public ObservableCollection<string> PlanItems { get; } = [];

    public bool HasPlan => _plan is not null;

    public string PlanHeader => _plan is null
        ? string.Empty
        : T.Format("PlanHeader", _plan.Operations.Count) + (_plan.RequiresPreview ? T["PlanPreviewRequired"] : "");

    public string PreviewText
    {
        get => _previewText;
        private set
        {
            Set(ref _previewText, value);
            OnPropertyChanged(nameof(HasPreviewText));
        }
    }

    public bool HasPreviewText => _previewText.Length > 0;

    private bool CanApply => _plan is not null && !IsBusy && (!_plan.RequiresPreview || _previewSucceeded);

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
            RaisePlanCommandsChanged();
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
        if (context.DocumentTitle != _context.DocumentTitle && _plan is not null && !IsBusy)
        {
            DiscardPlan(T["ProjectChangedPlan"]);
        }

        if (context.DocumentTitle != _context.DocumentTitle && _conversation.TurnCount > 0)
        {
            _conversation.Clear();
            Say(T["ProjectChangedConversation"]);
        }

        _context = context;
        OnPropertyChanged(nameof(ContextText));
    }

    /// <summary>Called by the view with the PasswordBox content; the key is never bound or stored in the view model.</summary>
    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Say(T["PasteKeyFirst"]);
            return;
        }

        try
        {
            _keyStore.Save(apiKey);
            ShowKeyPanel = false;
            OnPropertyChanged(nameof(HasApiKey));
            RemoveKeyCommand.RaiseCanExecuteChanged();
            Say(T["KeySaved"]);
            _log.Info("OpenAI API key saved.");
        }
        catch (Exception ex)
        {
            _log.Error("Saving the API key failed.", ex);
            Say(T.Format("KeySaveFailed", ex.Message));
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
            Say(T["KeyRemoved"]);
            _log.Info("OpenAI API key removed.");
        }
        catch (Exception ex)
        {
            _log.Error("Removing the API key failed.", ex);
            Say(T.Format("KeyRemoveFailed", ex.Message));
        }
    }

    private void AcceptConsent()
    {
        SaveSettings(_settings with { ConsentAcceptedAt = DateTimeOffset.Now });
        OnPropertyChanged(nameof(NeedsConsent));
        _log.Info("Data notice accepted.");
    }

    private void ToggleLanguage()
    {
        string language = _text.Language == UiText.English ? UiText.Romanian : UiText.English;
        SaveSettings(_settings with { Language = language });
        _text = new UiText(language);

        // Every binding to T[...] and every computed text refreshes; earlier chat messages stay as they were.
        OnPropertyChanged(nameof(T));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(PlanHeader));
        _log.Info($"Panel language set to {language}.");
    }

    /// <summary>Saves settings; on failure the change still applies for this session.</summary>
    private void SaveSettings(AddinSettings settings)
    {
        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Saving settings failed.", ex);
        }

        _settings = settings;
    }

    private async Task SendAsync()
    {
        string text = Input.Trim();
        if (text.Length == 0 || IsBusy)
        {
            return;
        }

        if (NeedsConsent)
        {
            Say(T["AcceptNoticeFirst"]);
            return;
        }

        if (!HasApiKey)
        {
            ShowKeyPanel = true;
            Say(T["AddKeyFirst"]);
            return;
        }

        Input = string.Empty;
        if (_plan is not null)
        {
            DiscardPlan(T["PreviousPlanDiscarded"]);
        }

        Messages.Add(new ChatMessage(T["You"], text));

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
            IReadOnlyList<ActionRecord> recent = _history.RecentApplied(context.DocumentTitle, RecentActionsForContext);
            AssistantReply reply = await _orchestrator.RunAsync(_conversation, text, context, recent, T, progress, cancellation.Token);
            Say(reply.Text);
            if (reply.Plan is not null)
            {
                ShowPlan(reply.Plan);
            }

            _log.Info($"Answered using {reply.ToolCalls.Count} tool call(s): {string.Join(", ", reply.ToolCalls.Select(c => c.Name))}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Say(T["Cancelled"]);
        }
        catch (TaskCanceledException)
        {
            Say(T.Format("OpenAiTimeout", _settings.AiRequestTimeoutSeconds));
        }
        catch (AiServiceException ex)
        {
            _log.Warning($"OpenAI request failed ({ex.Failure}): {ex.Message}");
            Say(Describe(ex));
            if (ex.Failure is AiFailure.MissingApiKey or AiFailure.InvalidApiKey)
            {
                ShowKeyPanel = true;
            }
        }
        catch (HttpRequestException ex)
        {
            _log.Warning($"Could not reach OpenAI: {ex.Message}");
            Say(T["CouldNotReachOpenAi"]);
        }
        catch (Exception ex)
        {
            _log.Error("Answering failed.", ex);
            Say(T.Format("SomethingWrong", ex.Message));
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
            _cancellation = null;
        }
    }

    private void ShowPlan(PendingPlan plan)
    {
        _plan = plan;
        _previewSucceeded = false;
        PreviewText = string.Empty;
        PlanItems.Clear();
        foreach (PlannedOperation operation in plan.Operations)
        {
            PlanItems.Add($"{operation.Number}. {operation.Summary}");
        }

        OnPlanChanged();
    }

    private void DiscardPlan(string message)
    {
        _plan = null;
        _previewSucceeded = false;
        PreviewText = string.Empty;
        PlanItems.Clear();
        OnPlanChanged();
        Say(message);
    }

    private async Task PreviewAsync()
    {
        if (_plan is not { } plan || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = T["StatusPreviewing"];
        try
        {
            PlanRunResult result = await _dispatcher.InvokeAsync(
                app => _executor.Run(RevitRead.RequireDocument(app), plan, apply: false));
            if (_plan == plan)
            {
                _previewSucceeded = result.Succeeded;
                PreviewText = DescribePreview(result);
                RaisePlanCommandsChanged();
            }
        }
        catch (Exception ex)
        {
            ReportRunFailure(apply: false, ex);
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
        }
    }

    private async Task ApplyAsync()
    {
        if (_plan is not { } plan || !CanApply)
        {
            return;
        }

        IsBusy = true;
        Status = T["StatusApplying"];
        try
        {
            (string documentTitle, PlanRunResult result) = await _dispatcher.InvokeAsync(app =>
            {
                var document = RevitRead.RequireDocument(app);
                return (document.Title, _executor.Run(document, plan, apply: true));
            });

            _history.Add(new ActionRecord(Guid.NewGuid(), DateTimeOffset.Now, documentTitle, plan.UserRequest, plan.Operations, result));
            _log.Info($"Apply '{plan.UserRequest}': {(result.Applied ? "applied" : "rolled back")}, " +
                      $"{result.Steps.Count(s => s.Succeeded)}/{plan.Operations.Count} step(s) succeeded.");

            _plan = null;
            PlanItems.Clear();
            PreviewText = string.Empty;
            OnPlanChanged();
            Say(DescribeApply(result));
        }
        catch (Exception ex)
        {
            ReportRunFailure(apply: true, ex);
        }
        finally
        {
            IsBusy = false;
            Status = string.Empty;
        }
    }

    /// <summary>
    /// The dispatcher guarantees a request either never ran or ran to completion, and the executor rolls back on any
    /// failure, so an exception here means the model was not changed.
    /// </summary>
    private void ReportRunFailure(bool apply, Exception ex)
    {
        string action = apply ? "Apply" : "Preview";
        if (ex is TimeoutException)
        {
            _log.Warning($"{action} timed out waiting for Revit.");
            Say(T[apply ? "ApplyTimeout" : "PreviewTimeout"]);
            return;
        }

        _log.Error($"{action} failed.", ex);
        Say(T.Format(apply ? "ApplyError" : "PreviewError", ex.Message));
    }

    private string DescribePreview(PlanRunResult result)
    {
        var text = new StringBuilder();
        if (result.Succeeded)
        {
            text.AppendLine(T["PreviewSucceeded"]);
            AppendSteps(text, result.Steps);
        }
        else
        {
            StepResult failed = result.FailedStep!;
            text.AppendLine(T.Format("PreviewFailedAtStep", failed.Number, failed.Outcome));
            text.AppendLine(T["PreviewAdjust"]);
        }

        return text.ToString().TrimEnd();
    }

    private string DescribeApply(PlanRunResult result)
    {
        var text = new StringBuilder();
        if (result.Applied)
        {
            text.AppendLine(T.Format("Applied", result.Steps.Count));
            AppendSteps(text, result.Steps);
            text.AppendLine(T.Format("UndoHint", result.UndoName ?? ""));
        }
        else
        {
            StepResult failed = result.FailedStep!;
            text.AppendLine(T.Format("ApplyFailedAtStep", failed.Number, failed.ToolName, failed.Outcome));
            if (failed.Number > 1)
            {
                text.AppendLine(T.Format("StepsRolledBack", failed.Number - 1));
            }
        }

        return text.ToString().TrimEnd();
    }

    private void AppendSteps(StringBuilder text, IReadOnlyList<StepResult> steps)
    {
        foreach (StepResult step in steps)
        {
            text.AppendLine($"• {step.Outcome}");
            foreach (string warning in step.Warnings)
            {
                text.AppendLine("   ⚠ " + T.Format("RevitWarning", warning));
            }
        }
    }

    private void OnPlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(PlanHeader));
        RaisePlanCommandsChanged();
    }

    private void RaisePlanCommandsChanged()
    {
        SendCommand?.RaiseCanExecuteChanged();
        PreviewCommand?.RaiseCanExecuteChanged();
        ApplyCommand?.RaiseCanExecuteChanged();
        DiscardCommand?.RaiseCanExecuteChanged();
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
            Say(T["RevitTimeout"]);
        }
        catch (Exception ex)
        {
            _log.Error("Context read failed.", ex);
            Say(T.Format("ReadModelFailed", ex.Message));
        }

        return null;
    }

    private string Describe(AiServiceException ex) => ex.Failure switch
    {
        AiFailure.MissingApiKey => T["AddKeyFirst"],
        AiFailure.InvalidApiKey => T["AiInvalidKey"],
        AiFailure.RateLimited => T.Format("AiRateLimited", ex.Message),
        AiFailure.BadResponse => T["AiBadResponse"],
        _ => T.Format("AiFailed", ex.Message),
    };

    private string FormatContext(ModelContext context)
    {
        if (!context.HasDocument)
        {
            return T["NoProject"];
        }

        string level = context.LevelName is null ? "" : $" · {context.LevelName}";
        return $"{context.DocumentTitle}\n{context.ViewName} ({context.ViewType}){level}\n{T.SelectedElements(context.SelectionCount)}";
    }

    private void Say(string text) => Messages.Add(new ChatMessage(AssistantName, text));

    private void Set<TValue>(ref TValue field, TValue value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
