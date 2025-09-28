using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private readonly SynchronizationContext? _synchronizationContext;
        private bool _isExecuting;

        public AsyncCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _synchronizationContext = SynchronizationContext.Current;
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            private set
            {
                if (_isExecuting != value)
                {
                    _isExecuting = value;
                    RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanExecute(object? parameter)
        {
            if (IsExecuting)
            {
                return false;
            }

            return _canExecute?.Invoke(parameter) ?? true;
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            try
            {
                IsExecuting = true;
                await _execute(parameter).ConfigureAwait(false);
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            var handler = CanExecuteChanged;
            if (handler == null)
            {
                return;
            }

            if (_synchronizationContext != null && _synchronizationContext != SynchronizationContext.Current)
            {
                _synchronizationContext.Post(_ => handler(this, EventArgs.Empty), null);
            }
            else
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
