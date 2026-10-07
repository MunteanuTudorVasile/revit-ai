using System.Windows.Input;

namespace RevitAi.Addin.UI;

/// <summary>Button command for async work. Disabled while running so a request cannot be sent twice.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isRunning;

    public AsyncCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter)
    {
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            // _execute must handle its own errors; an exception escaping async void would crash Revit.
            await _execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
