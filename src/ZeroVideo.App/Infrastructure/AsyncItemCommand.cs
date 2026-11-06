using System.Windows.Input;

namespace ZeroVideo.App.Infrastructure;

/// <summary>
/// An async WPF command accepting the exact item from an ItemsControl card.
/// It does not depend on whatever video is selected elsewhere in the app.
/// </summary>
public sealed class AsyncItemCommand<T>(
    Func<T,Task> execute,Func<T,bool>? canExecute=null) : ICommand where T : class
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) =>
        !_running && parameter is T item && (canExecute?.Invoke(item)??true);
    public async void Execute(object? parameter)
    {
        if(parameter is not T item || !CanExecute(parameter))return;
        _running=true;
        CanExecuteChanged?.Invoke(this,EventArgs.Empty);
        try { await execute(item); }
        finally
        {
            _running=false;
            CanExecuteChanged?.Invoke(this,EventArgs.Empty);
        }
    }
}
