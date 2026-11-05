using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using StartScreen.Models;

namespace StartScreen.Services
{
    // These are optional, internal VS contracts. Bind only their public surface at runtime,
    // without shipping Copilot assemblies or imposing an assembly dependency on older VS versions.
    internal sealed class CopilotHarnessAdapter : IDisposable
    {
        internal const string ContractNamespace = "Microsoft.VisualStudio.Copilot.Harness.";
        internal const string BackendsContract = ContractNamespace + "ICopilotBackends";
        internal const string NavigatorRegistryContract = "Microsoft.VisualStudio.Copilot.UI.HarnessUi.IHarnessNavigatorRegistry";
        private readonly Action<Action> _dispatch;
        private readonly Action<Exception> _onError;
        private readonly SubscriptionBag _subscriptions = new();
        private readonly Dictionary<object, SessionWatch> _sessions = new(ReferenceComparer.Instance);
        private object _cache;
        private bool _disposed;
        private bool _started;

        internal CopilotHarnessAdapter(Action<Action> dispatch, Action<Exception> onError)
        {
            _dispatch = dispatch;
            _onError = onError;
        }

        internal event EventHandler Changed;
        internal bool IsReady { get; private set; }
        internal IReadOnlyList<CopilotActivitySession> Sessions => _sessions.Values.Select(session => session.Snapshot()).ToArray();

        internal void Start(object backends)
        {
            if (_started || _disposed)
                throw new InvalidOperationException("This Copilot activity connection cannot be started again.");
            _started = true;
            var contract = RequireContract(backends, BackendsContract);
            var backend = contract.GetMethod("GetDefault", Type.EmptyTypes)?.Invoke(backends, null)
                ?? throw new NotSupportedException("The selected Copilot backend is unavailable.");
            RequireContract(backend, ContractNamespace + "ICopilotBackend");
            _cache = Read(backend, "Sessions");
            _subscriptions.Add(Observe(Read(backend, "SessionsReady"), value =>
            {
                IsReady = (bool)value;
                NotifyChanged();
            }));
            _subscriptions.Add(Observe(Connect(_cache), _ => SynchronizeSessions()));
            SynchronizeSessions();
        }

        private void SynchronizeSessions()
        {
            if (_disposed)
                return;
            var current = Items(_cache);
            var currentSet = new HashSet<object>(current, ReferenceComparer.Instance);
            foreach (var removed in _sessions.Keys.Where(key => !currentSet.Contains(key)).ToArray())
            {
                _sessions[removed].Dispose();
                _sessions.Remove(removed);
            }
            foreach (var session in current)
            {
                if (_sessions.ContainsKey(session))
                    continue;
                var watch = new SessionWatch(this, session);
                _sessions.Add(session, watch);
                watch.Start();
            }
            NotifyChanged();
        }

        internal bool HasSession(string id) => !_disposed && _sessions.Values.Any(session => session.Id == id);

        internal object FindSession(string id)
        {
            if (_disposed)
                throw new InvalidOperationException("Copilot activity is no longer connected. Refresh and try again.");
            return _sessions.Values.FirstOrDefault(session => session.Id == id)?.Source
                ?? throw new InvalidOperationException("This Copilot session is no longer available. Refresh and try again.");
        }

        internal void OpenSession(object navigator, string id)
        {
            var session = FindSession(id);
            var contract = RequireContract(navigator, "Microsoft.VisualStudio.Copilot.UI.HarnessUi.IHarnessSessionNavigator");
            var method = contract.GetMethod("OpenSession")
                ?? throw new NotSupportedException("Copilot session navigation is unavailable.");
            var parameters = method.GetParameters();
            if (method.ReturnType != typeof(void) || parameters.Length != 2
                || !parameters[0].ParameterType.IsInstanceOfType(session) || parameters[1].ParameterType != typeof(bool))
                throw new NotSupportedException("The installed Copilot navigation contract is incompatible.");
            method.Invoke(navigator, new[] { session, (object)true });
        }

        private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

        private IDisposable Observe(object source, Action<object> onNext, Action onCompleted = null)
        {
            bool active = true;
            var observable = source?.GetType().GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IObservable<>))
                ?? throw new NotSupportedException("The installed Copilot contract does not expose the expected observable.");
            var observer = Activator.CreateInstance(typeof(Observer<>).MakeGenericType(observable.GetGenericArguments()),
                new Action<object>(value => Dispatch(() =>
                {
                    if (active)
                        onNext(value);
                })), new Action<Exception>(error => Dispatch(() =>
                {
                    if (active)
                        _onError(error);
                })), new Action(() => Dispatch(() =>
                {
                    if (active)
                        onCompleted?.Invoke();
                })));
            var subscription = (IDisposable)observable.GetMethod("Subscribe").Invoke(source, new[] { observer });
            return new CallbackDisposable(() =>
            {
                active = false;
                subscription.Dispose();
            });
        }

        private void Dispatch(Action callback)
        {
            _dispatch(() =>
            {
                if (_disposed)
                    return;
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    _onError(ex);
                }
            });
        }

        internal static Type RequireContract(object source, string name)
        {
            return source?.GetType().GetInterfaces().SingleOrDefault(type => type.FullName == name)
                ?? throw new NotSupportedException($"The installed Copilot does not expose {name}.");
        }

        internal static object Read(object source, string name)
        {
            var property = source?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? source?.GetType().GetInterfaces().Select(type => type.GetProperty(name)).FirstOrDefault(value => value != null)
                ?? throw new NotSupportedException($"The installed Copilot contract is missing {name}.");
            return property.GetValue(source);
        }

        private static object Connect(object source)
        {
            var method = source.GetType().GetInterfaces().SelectMany(type => type.GetMethods())
                .FirstOrDefault(candidate => candidate.Name == "Connect" && candidate.GetParameters().All(parameter => parameter.IsOptional))
                ?? throw new NotSupportedException("The installed Copilot collection cannot be observed.");
            return method.Invoke(source, method.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        }

        private static object[] Items(object source) => ((IEnumerable)Read(source, "Items")).Cast<object>().ToArray();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsReady = false;
            _subscriptions.Dispose();
            foreach (var session in _sessions.Values)
                session.Dispose();
            _sessions.Clear();
        }

        private sealed class SessionWatch : IDisposable
        {
            private readonly CopilotHarnessAdapter _owner;
            private readonly SubscriptionBag _subscriptions = new();
            private readonly HashSet<object> _permissions = new(ReferenceComparer.Instance);
            private readonly HashSet<object> _questions = new(ReferenceComparer.Instance);
            private string _title;
            private string _directory;
            private DateTimeOffset _lastActivity;
            private bool? _responding;
            private bool _transcript;

            internal SessionWatch(CopilotHarnessAdapter owner, object source)
            {
                _owner = owner;
                Source = source;
            }

            internal string Id { get; private set; }
            internal object Source { get; }

            internal void Start()
            {
                RequireContract(Source, ContractNamespace + "IBackendSession");
                Id = ((Guid)Read(Read(Source, "SessionId"), "Guid")).ToString("D");
                _lastActivity = (DateTimeOffset)Read(Source, "CreatedAt");
                _transcript = (bool)Read(Source, "IsTranscript");
                Subscribe("Title", value => _title = (string)value);
                Subscribe("WorkingDirectory", value => _directory = (string)value);
                Subscribe("LastActivity", value => _lastActivity = (DateTimeOffset)value);
                _subscriptions.Add(_owner.Observe(Read(Source, "IsResponding"), value =>
                {
                    _responding = (bool)value;
                    _owner.NotifyChanged();
                }, () =>
                {
                    // Listed, unhydrated sessions publish Return(false), not a live activity stream.
                    _responding = null;
                    _owner.NotifyChanged();
                }));
                _subscriptions.Add(WatchEntries(Read(Source, "ChatEvents"), 0));
            }

            private void Subscribe(string property, Action<object> update)
            {
                _subscriptions.Add(_owner.Observe(Read(Source, property), value =>
                {
                    update(value);
                    _owner.NotifyChanged();
                }));
            }

            private IDisposable WatchEntries(object entries, int depth)
            {
                if (depth > 16)
                    throw new NotSupportedException("The Copilot subagent hierarchy is too deep to observe safely.");
                var bag = new SubscriptionBag();
                bool connected = false;
                var watches = new Dictionary<object, IDisposable>(ReferenceComparer.Instance);
                bag.Add(new CallbackDisposable(() =>
                {
                    foreach (var watch in watches.Values)
                        watch.Dispose();
                    watches.Clear();
                }));
                try
                {
                    bag.Add(_owner.Observe(Connect(entries), _ =>
                    {
                        var current = Items(entries);
                        var currentSet = new HashSet<object>(current, ReferenceComparer.Instance);
                        foreach (var removed in watches.Keys.Where(key => !currentSet.Contains(key)).ToArray())
                        {
                            watches[removed].Dispose();
                            watches.Remove(removed);
                        }
                        foreach (var entry in current)
                        {
                            if (!watches.ContainsKey(entry))
                                watches.Add(entry, WatchEntry(entry, depth));
                        }
                        _owner.NotifyChanged();
                    }));
                    connected = true;
                    return bag;
                }
                finally
                {
                    if (!connected)
                        bag.Dispose();
                }
            }

            private IDisposable WatchEntry(object entry, int depth)
            {
                var interfaces = entry.GetType().GetInterfaces().Select(type => type.FullName).ToArray();
                var permission = interfaces.Contains(ContractNamespace + "IBackendChatConfirmation");
                var question = interfaces.Contains(ContractNamespace + "IBackendChatUserInput");
                var bag = new SubscriptionBag();
                bool connected = false;
                try
                {
                    if (permission || question)
                    {
                        var pending = permission ? _permissions : _questions;
                        bag.Add(new CallbackDisposable(() => pending.Remove(entry)));
                        bag.Add(_owner.Observe(Read(entry, "State"), value =>
                        {
                            var state = value.ToString();
                            if (state == "Pending")
                                pending.Add(entry);
                            else if (state == "Accepted" || state == "Rejected" || state == "Cancelled" || state == "Answered")
                                pending.Remove(entry);
                            else
                                throw new NotSupportedException($"Unknown Copilot request state: {state}.");
                            _owner.NotifyChanged();
                        }));
                    }
                    if (interfaces.Contains(ContractNamespace + "IBackendChatSubagentCall"))
                    {
                        IDisposable child = null;
                        bag.Add(new CallbackDisposable(() => child?.Dispose()));
                        bag.Add(_owner.Observe(Read(entry, "Subagent"), value =>
                        {
                            child?.Dispose();
                            child = null;
                            if (value != null)
                                child = WatchEntries(Read(value, "ChatEvents"), depth + 1);
                            _owner.NotifyChanged();
                        }));
                    }
                    connected = true;
                    return bag;
                }
                finally
                {
                    if (!connected)
                        bag.Dispose();
                }
            }

            internal CopilotActivitySession Snapshot() => new()
            {
                Id = Id,
                Title = string.IsNullOrWhiteSpace(_title) ? "Untitled conversation" : _title,
                WorkingDirectory = _directory,
                LastActivity = _lastActivity,
                IsResponding = _responding,
                IsTranscript = _transcript,
                PendingPermissions = _permissions.Count,
                PendingQuestions = _questions.Count,
            };

            public void Dispose() => _subscriptions.Dispose();
        }

        private sealed class Observer<T>(Action<object> onNext, Action<Exception> onError, Action onCompleted) : IObserver<T>
        {
            public void OnNext(T value) => onNext(value);
            public void OnError(Exception error) => onError(error);
            public void OnCompleted() => onCompleted();
        }

        private sealed class CallbackDisposable(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }

        private sealed class SubscriptionBag : IDisposable
        {
            private readonly List<IDisposable> _items = new();
            private bool _disposed;

            internal void Add(IDisposable item)
            {
                if (_disposed)
                    item.Dispose();
                else
                    _items.Add(item);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                for (var index = _items.Count - 1; index >= 0; index--)
                    _items[index].Dispose();
                _items.Clear();
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}