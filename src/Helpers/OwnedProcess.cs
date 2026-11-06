using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32.SafeHandles;

namespace StartScreen.Helpers
{
    internal sealed class OwnedProcessResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool OutputTruncated { get; set; }
        public bool ErrorTruncated { get; set; }
    }

    /// <summary>
    /// Runs an owned Windows process tree. The root is suspended until it belongs to
    /// a kill-on-close job, so even immediately spawned credential/SSH helpers are owned.
    /// Both pipes are drained to EOF while retaining only a bounded prefix of each.
    /// </summary>
    internal static class OwnedProcess
    {
        internal const int MaximumOutputCharacters = 64 * 1024;

        internal static async Task<OwnedProcessResult> RunAsync(ProcessStartInfo startInfo,
            TimeSpan timeout, CancellationToken cancellationToken = default,
            string input = null, Action<int> processStarted = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            cancellationToken.ThrowIfCancellationRequested();

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var child = new ChildProcess(startInfo))
            {
                deadline.CancelAfter(timeout);
                var result = new OwnedProcessResult();
                Task completion = CompleteAsync(child, input, result);
                try
                {
                    processStarted?.Invoke(child.Id);
                    Task winner = await Task.WhenAny(completion, Task.Delay(Timeout.Infinite, deadline.Token));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (winner != completion)
                    {
                        result.TimedOut = true;
                        return result;
                    }

                    await completion;
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                finally
                {
                    // Closing the job terminates all descendants, including any that
                    // inherited pipes after the root exited. Always observe the drains.
                    child.KillTree();
                    deadline.Cancel();
                    // Bound cleanup as well: a foreign process could have inherited
                    // a pipe handle while another extension was starting a process.
                    Task observed = ObserveCompletionAsync(completion);
                    if (await Task.WhenAny(observed, Task.Delay(2000)) == observed)
                        await observed;
                    else
                        observed.Forget();
                }
            }
        }

        private static Task ObserveCompletionAsync(Task completion)
        {
            return completion.ContinueWith(completed => completed.IsFaulted
                ? completed.Exception.LogAsync() : Task.CompletedTask, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }

        private static async Task CompleteAsync(ChildProcess child, string input, OwnedProcessResult result)
        {
            Task output = DrainAsync(child.Output, text => result.Output = text, truncated => result.OutputTruncated = truncated);
            Task error = DrainAsync(child.Error, text => result.Error = text, truncated => result.ErrorTruncated = truncated);
            Task write = WriteInputAsync(child.Input, input);
            Task exit = WaitForExitAsync(child, result);
            await Task.WhenAll(output, error, write, exit);
        }

        private static async Task WriteInputAsync(StreamWriter writer, string input)
        {
            using (writer)
            {
                if (input != null)
                    await writer.WriteAsync(input);
            }
        }

        private static async Task DrainAsync(StreamReader reader, Action<string> publish, Action<bool> truncated)
        {
            var retained = new StringBuilder();
            var buffer = new char[4096];
            var wasTruncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                var keep = Math.Min(count, MaximumOutputCharacters - retained.Length);
                retained.Append(buffer, 0, keep);
                wasTruncated |= keep != count;
            }

            publish(retained.ToString());
            truncated(wasTruncated);
        }

        private static async Task WaitForExitAsync(ChildProcess child, OwnedProcessResult result)
        {
            while (true)
            {
                var wait = WaitForSingleObject(child.Handle, 0);
                if (wait == 0)
                    break;
                if (wait != 258)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                await Task.Delay(25);
            }

            if (!GetExitCodeProcess(child.Handle, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            result.ExitCode = unchecked((int)exitCode);
            child.KillTree();
        }

        private sealed class ChildProcess : IDisposable
        {
            private SafeFileHandle _job;
            public SafeFileHandle Handle { get; private set; }
            public int Id { get; private set; }
            public StreamReader Output { get; private set; }
            public StreamReader Error { get; private set; }
            public StreamWriter Input { get; private set; }

            public ChildProcess(ProcessStartInfo startInfo)
            {
                SafeFileHandle outputRead = null, outputWrite = null;
                SafeFileHandle errorRead = null, errorWrite = null;
                SafeFileHandle inputRead = null, inputWrite = null;
                SafeFileHandle thread = null;
                var attributes = IntPtr.Zero;
                var handleList = IntPtr.Zero;
                var environment = IntPtr.Zero;
                var initialized = false;
                var resumed = false;
                try
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    Check(!_job.IsInvalid);
                    var limits = new JobLimits();
                    limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    Check(SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf(typeof(JobLimits))));

                    var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                    Check(CreatePipe(out outputRead, out outputWrite, ref security, 0));
                    Check(CreatePipe(out errorRead, out errorWrite, ref security, 0));
                    Check(CreatePipe(out inputRead, out inputWrite, ref security, 0));
                    Check(SetHandleInformation(outputRead, 1, 0));
                    Check(SetHandleInformation(errorRead, 1, 0));
                    Check(SetHandleInformation(inputWrite, 1, 0));

                    // Only these three handles may be inherited, even if another
                    // extension concurrently creates inheritable handles in VS.
                    var size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(size);
                    Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                    initialized = true;
                    handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
                    Marshal.WriteIntPtr(handleList, 0, inputRead.DangerousGetHandle());
                    Marshal.WriteIntPtr(handleList, IntPtr.Size, outputWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, errorWrite.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handleList, new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));

                    var startup = new StartupInfoEx
                    {
                        Startup = new StartupInfo
                        {
                            Size = Marshal.SizeOf(typeof(StartupInfoEx)),
                            Flags = 0x100,
                            StandardInput = inputRead.DangerousGetHandle(),
                            StandardOutput = outputWrite.DangerousGetHandle(),
                            StandardError = errorWrite.DangerousGetHandle(),
                        },
                        Attributes = attributes,
                    };
                    var entries = startInfo.EnvironmentVariables.Cast<DictionaryEntry>()
                        .Select(entry => $"{entry.Key}={entry.Value}")
                        .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
                    environment = Marshal.StringToHGlobalUni(string.Join("\0", entries) + "\0\0");
                    var command = new StringBuilder($"\"{startInfo.FileName}\" {startInfo.Arguments}");
                    Check(CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x00080000 | 0x00000400 | 0x08000000 | 0x00000004,
                        environment, string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                        ref startup, out var process));
                    Handle = new SafeFileHandle(process.Process, true);
                    thread = new SafeFileHandle(process.Thread, true);
                    Id = process.Id;
                    Check(AssignProcessToJobObject(_job, Handle));

                    Output = new StreamReader(new FileStream(outputRead, FileAccess.Read), startInfo.StandardOutputEncoding ?? Encoding.UTF8);
                    Error = new StreamReader(new FileStream(errorRead, FileAccess.Read), startInfo.StandardErrorEncoding ?? Encoding.UTF8);
                    Input = new StreamWriter(new FileStream(inputWrite, FileAccess.Write), new UTF8Encoding(false));
                    Check(ResumeThread(thread) != uint.MaxValue);
                    resumed = true;
                }
                catch (Exception ex)
                {
                    ex.LogAsync().FireAndForget();
                    // Assignment can fail (for example due to an enclosing job's
                    // restrictions). Never let an unowned suspended process escape.
                    if (Handle != null && !resumed && !TerminateProcess(Handle, 1))
                        new Win32Exception(Marshal.GetLastWin32Error()).LogAsync().FireAndForget();
                    Dispose();
                    outputRead?.Dispose();
                    errorRead?.Dispose();
                    inputWrite?.Dispose();
                    throw;
                }
                finally
                {
                    thread?.Dispose();
                    outputWrite?.Dispose();
                    errorWrite?.Dispose();
                    inputRead?.Dispose();
                    if (initialized)
                        DeleteProcThreadAttributeList(attributes);
                    if (attributes != IntPtr.Zero)
                        Marshal.FreeHGlobal(attributes);
                    if (handleList != IntPtr.Zero)
                        Marshal.FreeHGlobal(handleList);
                    if (environment != IntPtr.Zero)
                        Marshal.FreeHGlobal(environment);
                }
            }

            public void KillTree()
            {
                _job?.Dispose();
            }

            public void Dispose()
            {
                KillTree();
                DisposeStream(Input);
                DisposeStream(Output);
                DisposeStream(Error);
                Handle?.Dispose();
            }

            private static void DisposeStream(IDisposable stream)
            {
                try
                {
                    stream?.Dispose();
                }
                catch (Exception ex)
                {
                    ex.LogAsync().FireAndForget();
                }
            }

            private static void Check(bool success)
            {
                if (!success)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo Startup;
            public IntPtr Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public int Id, ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicJobLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, int flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeFileHandle handle, int milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    }
}