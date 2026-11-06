using System.Reflection;
using System.Threading;

namespace StartScreen.Services
{
    // Keep the optional harness dependency late-bound, like CopilotHarnessAdapter.
    internal static class CopilotChatLauncher
    {
        internal static async System.Threading.Tasks.Task<string> StartAsync(object backends, object navigator, string prompt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("A Copilot prompt is required.", nameof(prompt));
            cancellationToken.ThrowIfCancellationRequested();
            var backend = CopilotHarnessAdapter.RequireContract(backends, CopilotHarnessAdapter.BackendsContract)
                .GetMethod("GetDefault", Type.EmptyTypes)?.Invoke(backends, null)
                ?? throw new NotSupportedException("The selected Copilot backend is unavailable.");
            var backendType = CopilotHarnessAdapter.RequireContract(backend, CopilotHarnessAdapter.ContractNamespace + "ICopilotBackend");
            var create = RequireMethod(backendType, "CreateNewSessionAsync", 1);
            var sessionType = create.ReturnType.GetGenericArguments()[0];
            var send = RequireMethod(sessionType, "SendAsync", 2);
            var navigatorType = CopilotHarnessAdapter.RequireContract(navigator, "Microsoft.VisualStudio.Copilot.UI.HarnessUi.IHarnessSessionNavigator");
            var open = navigatorType.GetMethod("OpenSession", new[] { sessionType, typeof(bool) })
                ?? throw new NotSupportedException("Copilot session navigation is unavailable.");
            var ready = RequireMethod(navigatorType, "WaitForSessionReadyAsync", 1);
            if (send.GetParameters()[1].ParameterType != typeof(CancellationToken)
                || ready.GetParameters()[0].ParameterType != typeof(CancellationToken))
                throw new NotSupportedException("The installed Copilot chat contract is incompatible.");

            var options = Activator.CreateInstance(create.GetParameters()[0].ParameterType);
            var feature = RequireProperty(options, "Feature");
            var featureId = Activator.CreateInstance(feature.PropertyType, new object[] { "StartScreen" });
            feature.SetValue(options, featureId);
            RequireProperty(options, "IsEphemeral").SetValue(options, false);

            var request = Activator.CreateInstance(send.GetParameters()[0].ParameterType);
            RequireProperty(request, "Prompt").SetValue(request, prompt);
            var correlation = RequireProperty(request, "CorrelationId");
            correlation.SetValue(request, Activator.CreateInstance(correlation.PropertyType, new object[] { Guid.NewGuid() }));
            var attachments = RequireProperty(request, "Attachments");
            attachments.SetValue(request, Array.CreateInstance(attachments.PropertyType.GetGenericArguments()[0], 0));
            var requestOptions = RequireProperty(request, "Options");
            requestOptions.SetValue(request, Activator.CreateInstance(requestOptions.PropertyType));
            RequireProperty(request, "Feature").SetValue(request, featureId);

            var session = await InvokeAsync(create, backend, options);
            cancellationToken.ThrowIfCancellationRequested();
            open.Invoke(navigator, new[] { session, (object)true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var readyId = await InvokeAsync(ready, navigator, timeout.Token);
            if (readyId == null || !Equals(readyId, CopilotHarnessAdapter.Read(session, "SessionId")))
                throw new InvalidOperationException("The new Copilot chat did not become active. Try again.");
            var response = await InvokeAsync(send, session, request, cancellationToken);
            if (CopilotHarnessAdapter.Read(response, "Status")?.ToString() != "Success")
            {
                var error = CopilotHarnessAdapter.Read(response, "Error");
                var message = error == null ? null : CopilotHarnessAdapter.Read(error, "Message") as string;
                throw new InvalidOperationException("Copilot could not complete the request. " + message);
            }
            return ((Guid)CopilotHarnessAdapter.Read(CopilotHarnessAdapter.Read(session, "SessionId"), "Guid")).ToString("D");
        }

        internal static async System.Threading.Tasks.Task<bool> TryResumeAsync(object backends, object navigator, string sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception failure = null;
            var gate = new object();
            using var adapter = new CopilotHarnessAdapter(callback =>
            {
                lock (gate)
                    callback();
            }, error => failure = error);
            lock (gate)
                adapter.Start(backends);
            while (true)
            {
                lock (gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failure != null)
                        throw new InvalidOperationException("Copilot conversations could not be verified.", failure);
                    if (adapter.HasSession(sessionId))
                    {
                        adapter.OpenSession(navigator, sessionId);
                        return true;
                    }
                    if (adapter.IsReady)
                        return false;
                }
                await System.Threading.Tasks.Task.Delay(100, cancellationToken);
            }
        }

        private static MethodInfo RequireMethod(Type type, string name, int parameterCount)
        {
            var method = type.GetMethod(name);
            if (method == null || method.GetParameters().Length != parameterCount
                || !method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(System.Threading.Tasks.Task<>))
                throw new NotSupportedException($"The installed Copilot chat contract is missing {name}.");
            return method;
        }

        private static PropertyInfo RequireProperty(object source, string name)
        {
            var property = source.GetType().GetProperty(name);
            return property?.CanWrite == true ? property
                : throw new NotSupportedException($"The installed Copilot chat contract is missing {name}.");
        }

        private static async System.Threading.Tasks.Task<object> InvokeAsync(MethodInfo method, object target, params object[] arguments)
        {
            var task = method.Invoke(target, arguments) as Task
                ?? throw new NotSupportedException("Copilot did not return the expected operation.");
            await task;
            return CopilotHarnessAdapter.Read(task, "Result");
        }
    }
}