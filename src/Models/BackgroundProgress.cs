using System.Collections.Generic;
using System.Linq;

namespace StartScreen.Models
{
    internal sealed class BackgroundProgress
    {
        private readonly object _sync = new object();
        private readonly List<Operation> _operations = new List<Operation>();
        private long _stepSequence;

        internal event EventHandler Changed;

        private (int Completed, int Total, bool Busy) State
        {
            get
            {
                lock (_sync)
                    return (_operations.Sum(operation => operation.Completed), _operations.Sum(operation => operation.Total),
                        _operations.Any(operation => !operation.Finished));
            }
        }

        internal bool IsBusy => State.Busy;
        internal string CurrentStep
        {
            get
            {
                lock (_sync)
                    return _operations.Where(operation => !operation.Finished)
                        .OrderByDescending(operation => operation.StepSequence).FirstOrDefault()?.Step;
            }
        }

        internal double PercentComplete
        {
            get
            {
                var state = State;
                return state.Total == 0 ? 0 : 100d * state.Completed / state.Total;
            }
        }

        internal string Details
        {
            get
            {
                var state = State;
                return $"{state.Completed} of {state.Total} work items finished.";
            }
        }

        internal Operation Start(int total = 1, string step = "Updating...")
        {
            if (total < 1)
                throw new ArgumentOutOfRangeException(nameof(total));
            if (string.IsNullOrWhiteSpace(step))
                throw new ArgumentException("A progress step is required.", nameof(step));
            Operation operation;
            lock (_sync)
            {
                if (_operations.All(item => item.Finished))
                    _operations.Clear();
                operation = new Operation(this, total) { Step = step, StepSequence = ++_stepSequence };
                _operations.Add(operation);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return operation;
        }

        internal void Reset()
        {
            lock (_sync)
            {
                if (_operations.Count == 0)
                    return;
                foreach (var operation in _operations)
                    operation.Finished = true;
                _operations.Clear();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal sealed class Operation : IProgress<(int Completed, int Total)>, IProgress<string>, IDisposable
        {
            private readonly BackgroundProgress _owner;
            internal int Completed;
            internal int Total;
            internal bool Finished;
            internal string Step;
            internal long StepSequence;
            private bool _hasReported;

            internal Operation(BackgroundProgress owner, int total)
            {
                _owner = owner;
                Total = total;
            }

            public void Report((int Completed, int Total) value)
            {
                if (value.Total < 1 || value.Completed < 0 || value.Completed > value.Total)
                    throw new ArgumentOutOfRangeException(nameof(value));
                lock (_owner._sync)
                {
                    if (Finished)
                        return;
                    _hasReported = true;
                    Total = Math.Max(Total, value.Total);
                    Completed = Math.Max(Completed, value.Completed);
                }
                _owner.Changed?.Invoke(_owner, EventArgs.Empty);
            }

            public void Report(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("A progress step is required.", nameof(value));
                lock (_owner._sync)
                {
                    if (Finished || Step == value)
                        return;
                    Step = value;
                    StepSequence = ++_owner._stepSequence;
                }
                _owner.Changed?.Invoke(_owner, EventArgs.Empty);
            }

            internal void Complete()
            {
                lock (_owner._sync)
                {
                    if (Finished)
                        return;
                    Completed = Total;
                    _hasReported = true;
                }
                Dispose();
            }

            public void Dispose()
            {
                lock (_owner._sync)
                {
                    if (Finished)
                        return;
                    // A stopped reported operation must not count unfinished requests as completed.
                    if (_hasReported)
                        Total = Completed;
                    else
                        Completed = Total;
                    Finished = true;
                }
                _owner.Changed?.Invoke(_owner, EventArgs.Empty);
            }
        }
    }
}