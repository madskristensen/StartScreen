using System.Diagnostics;
using System.IO;

namespace StartScreen.Services
{
    /// <summary>
    /// Static helper for invoking built-in VS commands.
    /// </summary>
    public static class VsCommandService
    {
        /// <summary>
        /// Opens a terminal (pwsh or cmd) at the given directory.
        /// </summary>
        public static void OpenTerminalAtPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return;

            // Prefer pwsh, fall back to cmd
            var shell = FindPowerShell() ?? "cmd.exe";

            Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = folder,
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Finds the pwsh executable path, or null if not installed.
        /// </summary>
        private static string FindPowerShell()
        {
            // Check common install locations for PowerShell 7+
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pwshPath = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");

            if (File.Exists(pwshPath))
                return pwshPath;

            // Fall back to PATH lookup
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Opens the New Project dialog.
        /// </summary>
        public static async Task NewProjectAsync()
        {
            await VS.Commands.ExecuteAsync("File.NewProject");
        }

        /// <summary>
        /// Opens the New Project dialog with a specific template pre-selected (if possible).
        /// Falls back to opening the dialog normally if template selection isn't supported.
        /// </summary>
        public static async Task NewProjectFromTemplateAsync(string templateId)
        {
            await NewProjectAsync();
        }

        /// <summary>
        /// Opens the Open Project/Solution dialog.
        /// </summary>
        public static async Task OpenProjectAsync()
        {
            await VS.Commands.ExecuteAsync("File.OpenProject");
        }

        /// <summary>
        /// Opens the Open Folder dialog.
        /// </summary>
        public static async Task OpenFolderAsync()
        {
            await VS.Commands.ExecuteAsync("File.OpenFolder");
        }

        /// <summary>
        /// Opens the Clone Repository dialog.
        /// </summary>
        public static async Task CloneRepositoryAsync()
        {
            await VS.Commands.ExecuteAsync("File.CloneRepository");
        }

        /// <summary>
        /// Opens the Attach to Process dialog.
        /// </summary>
        public static async Task AttachToProcessAsync()
        {
            await VS.Commands.ExecuteAsync("Debug.AttachToProcess");
        }

        /// <summary>
        /// Reattaches the debugger to the previous process.
        /// </summary>
        public static async Task ReattachToProcessAsync()
        {
            await VS.Commands.ExecuteAsync("Debug.ReattachToProcess");
        }

        /// <summary>
        /// Opens a solution, project, or folder in a new Visual Studio instance.
        /// </summary>
        public static void OpenInNewInstance(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var devenvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devenv.exe");
            if (!File.Exists(devenvPath))
                return;

            Process.Start(devenvPath, $"\"{path}\"");
        }

        /// <summary>
        /// Opens a solution/project/folder from a file path or folder path.
        /// </summary>
        public static async Task OpenPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            EnvDTE.DTE dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            if (dte == null)
                return;

            var extension = Path.GetExtension(path)?.ToLowerInvariant();

            if (Directory.Exists(path))
            {
                // Quote the path so spaces don't break command argument parsing
                await VS.Commands.ExecuteAsync("File.OpenFolder", $"\"{path}\"");
            }
            else if (extension == ".sln" || extension == ".slnx")
            {
                // Use StartOnIdle to defer opening until the UI thread is idle.
                // This prevents race conditions during document restoration by ensuring
                // VS has fully initialized its text view infrastructure before we
                // trigger solution opening with its document restoration process.
                await ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    dte.Solution.Open(path);
                }).Task;
            }
            else
            {
                // Project file or other - use open project command
                dte.Solution.AddFromFile(path);
            }
        }
    }
}
