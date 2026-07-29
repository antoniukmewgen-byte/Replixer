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

public class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly Action<Exception>? _onException;

    // Один екземпляр команди часто прив'язаний до кількох кнопок з різними CommandParameter
    // (наприклад, Viber/Telegram/WhatsApp у MissedCallReportView). Якщо тут тримати єдиний
    // спільний прапорець "виконується", виконання для одного параметра (Viber) блокувало б
    // кнопки решти параметрів (Telegram, WhatsApp), хоча вони незалежні одна від одної. Тому
    // "виконується" відстежуємо окремо на кожне значення параметра.
    private readonly HashSet<T> _executingParams = new();

    public AsyncRelayCommand(
        Func<T?, Task> execute,
        Func<T?, bool>? canExecute      = null,
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

    public bool CanExecute(object? parameter) =>
        !IsExecuting((T?)parameter) && (_canExecute?.Invoke((T?)parameter) ?? true);

    public async void Execute(object? parameter)
    {
        var param = (T?)parameter;
        if (IsExecuting(param)) return;
        SetExecuting(param, true);
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(param);
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
            SetExecuting(param, false);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool IsExecuting(T? parameter) => parameter is not null && _executingParams.Contains(parameter);

    private void SetExecuting(T? parameter, bool executing)
    {
        // null-параметр (немає CommandParameter) не відстежуємо — таких викликів
        // у поточному коді немає, а трактувати null як "один спільний стан" безпечніше,
        // ніж кидати виняток.
        if (parameter is null) return;
        if (executing) _executingParams.Add(parameter);
        else _executingParams.Remove(parameter);
    }
}
