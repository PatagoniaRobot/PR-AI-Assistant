using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Implementación estándar de ICommand para MVVM puro (sincrónico sin parámetros).
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute is null || _canExecute();

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Implementación estándar de ICommand para MVVM puro (sincrónico con parámetros).
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (_canExecute is null) return true;
        if (parameter == null && typeof(T).IsValueType) return _canExecute(default);
        if (parameter == null || parameter is T) return _canExecute((T?)parameter);
        return false;
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        
        if (parameter == null && typeof(T).IsValueType)
            _execute(default);
        else
            _execute((T?)parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Implementación estándar de ICommand para MVVM puro (asíncrono con control de estado interno).
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute is null || _canExecute());
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _executeAsync();
        }
        catch (Exception ex)
        {
            // Las excepciones no deben ser tragadas silenciosamente.
            // Para MVVM puro, lo ideal es propagarla al hilo principal o un log global
            throw new InvalidOperationException("Falló la ejecución del comando asíncrono", ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Implementación estándar de ICommand para MVVM puro (asíncrono con parámetro).
///
/// Hace falta cuando la acción larga es por fila de una lista —mover los archivos de una
/// carpeta, por ejemplo— y no alcanza con un solo estado «ejecutando» en el ViewModel.
/// </summary>
public class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _executeAsync;
    private readonly Func<T?, bool>? _canExecute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<T?, Task> executeAsync, Func<T?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        if (_canExecute is null) return true;
        if (parameter is null && typeof(T).IsValueType) return _canExecute(default);
        if (parameter is null || parameter is T) return _canExecute((T?)parameter);
        return false;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _executeAsync(parameter is null && typeof(T).IsValueType ? default : (T?)parameter);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Falló la ejecución del comando asíncrono", ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
