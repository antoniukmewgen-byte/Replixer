using Replixer.Services;
using System.Diagnostics;
using System.Windows.Input;

namespace Replixer.Infrastructure;

public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    private readonly Action<Exception>? _onException;

    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute   = null,
        Action<Exception>? onException = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute     = execute;
        _canExecute  = canExecute;
        _onException = onException;
    }

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isExecuting) return;
        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncRelayCommand] Unhandled exception: {ex}");
            if (_onException is not null)
                _onException(ex);
            else
                NotificationService.ShowError($"Помилка: {ex.Message}");
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
