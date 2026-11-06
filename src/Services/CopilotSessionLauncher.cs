using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using StartScreen.Models;

namespace StartScreen.Services
{
    internal static class CopilotSessionLauncher
    {
        internal static bool Supports(CopilotSessionOrigin source, CopilotSessionOrigin destination) => source == destination
            || destination == CopilotSessionOrigin.Cli && (source == CopilotSessionOrigin.VisualStudio || source == CopilotSessionOrigin.VisualStudioCode);

        internal static string CliUnavailableReason(CopilotActivitySession session, bool codeRunning,
            Func<string, string> readSavedState = null)
        {
            if (!Supports(session.Origin, CopilotSessionOrigin.Cli))
                return "This session cannot be opened in CLI.";
            if (session.PendingPermissions > 0 || session.PendingQuestions > 0 || session.IsResponding == true || session.IsCached)
                return "Close the original session first; its ownership has not been released.";
            // A live activity stream means VS still has the session loaded, even while idle.
            if (session.Origin == CopilotSessionOrigin.VisualStudio && session.IsResponding.HasValue)
                return "Close the original session first; Visual Studio still has it loaded.";
            if (session.Origin == CopilotSessionOrigin.VisualStudioCode && codeRunning)
                return "Close VS Code first; its agent host may still own this session.";
            return (readSavedState ?? SavedSessionUnavailableReason)(SessionDirectory(session));
        }

        internal static string SavedSessionUnavailableReason(string directory)
        {
            if (!Directory.Exists(directory))
                return "The saved session is no longer available.";
            if (Directory.EnumerateFiles(directory, "inuse.*").Any())
                return "The session has an ownership marker. Close its owner normally before resuming.";
            // The fork-lock file can outlive its handle. Probe it exclusively without modifying or deleting it.
            var forkLock = Path.Combine(directory, ".workspace-fork.lock");
            using var forkLockStream = File.Exists(forkLock)
                ? new FileStream(forkLock, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var events = Path.Combine(directory, "events.jsonl");
            if (!File.Exists(events))
                return "Session ownership is unknown. Open it in its original app.";
            // Absence of a lock is not proof of release. Only an explicit final shutdown qualifies.
            using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - 65536);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var buffer = new char[65537];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length)
                return "The session is still changing. Close its owner before resuming.";
            var tail = new string(buffer, 0, count);
            var lines = tail.Split('\n');
            var last = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (last == null || offset > 0 && lines.Length < 2)
                return "Session ownership could not be verified.";
            using var json = JsonDocument.Parse(last);
            return json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "session.shutdown"
                ? null : "Close the original session first; no final shutdown was recorded.";
        }

        internal static string SessionDirectory(CopilotActivitySession session)
        {
            if (!Guid.TryParseExact(session.Id, "D", out var id))
                throw new InvalidDataException("Invalid Copilot session ID.");
            var home = session.Origin == CopilotSessionOrigin.VisualStudio ? CopilotLocalSessions.VisualStudioHome : CopilotLocalSessions.DefaultHome;
            if (session.SessionHome != null && !string.Equals(Path.GetFullPath(session.SessionHome), Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The session belongs to an unsupported Copilot home.");
            return Path.Combine(home, "session-state", id.ToString("D"));
        }

        internal static string FindCliExecutable() => FindOnPath("copilot.exe")
            ?? throw new FileNotFoundException("GitHub Copilot CLI was not found. Install it and add copilot.exe to PATH, then refresh.");

        internal static string FindCodeExecutable()
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe");
            if (File.Exists(local))
                return local;
            if (File.Exists(system))
                return system;
            var executable = FindOnPath("Code.exe");
            if (executable != null)
                return executable;
            var command = FindOnPath("code.cmd");
            executable = command == null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(command), "..", "Code.exe"));
            return executable != null && File.Exists(executable) ? executable
                : throw new FileNotFoundException("VS Code was not found. Install VS Code with native Agents support, then refresh.");
        }

        private static string FindOnPath(string name)
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                var path = directory.Trim().Trim('"');
                if (!Path.IsPathRooted(path))
                    continue;
                var candidate = Path.Combine(path, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        internal static ProcessStartInfo CreateStartInfo(CopilotActivitySession session, CopilotSessionOrigin destination, string executable)
        {
            if (!Supports(session.Origin, destination))
                throw new InvalidOperationException("This cross-app session destination has not been verified.");
            if (!Guid.TryParseExact(session.Id, "D", out var id))
                throw new InvalidDataException("Invalid Copilot session ID.");
            string arguments;
            string home;
            if (destination == CopilotSessionOrigin.Cli)
            {
                SessionDirectory(session);
                arguments = "--resume=" + id.ToString("D");
                home = session.Origin == CopilotSessionOrigin.VisualStudio ? CopilotLocalSessions.VisualStudioHome : CopilotLocalSessions.DefaultHome;
            }
            else if (destination == CopilotSessionOrigin.VisualStudioCode && Guid.TryParseExact(session.NativeSessionId, "D", out var nativeId))
            {
                arguments = "--open-url vscode://agents/agent-host-session/ahp-session/" + nativeId.ToString("D");
                home = CopilotLocalSessions.DefaultHome;
            }
            else
                throw new InvalidOperationException("No compatible native session destination was found.");
            var start = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            start.EnvironmentVariables["COPILOT_HOME"] = home;
            start.EnvironmentVariables.Remove("COPILOT_AGENT_SESSION_ID");
            start.EnvironmentVariables.Remove("COPILOT_CLI_RESOLVED_DIST_DIR");
            return start;
        }

        internal static bool IsCodeRunning()
        {
            var processes = Process.GetProcessesByName("Code");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }
}