using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace StartScreen.Services
{
    /// <summary>
    /// Static helper for invoking built-in VS commands.
    /// </summary>
    public static class VsCommandService
    {
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
            await VS.Commands.ExecuteAsync("Git.Clone");
        }

        /// <summary>
        /// Opens a solution/project/folder from a file path or folder path.
        /// </summary>
        public static async Task OpenPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            if (dte == null)
                return;

            string extension = Path.GetExtension(path)?.ToLowerInvariant();

            if (Directory.Exists(path))
            {
                // Open as folder
                await VS.Commands.ExecuteAsync("File.OpenFolder", path);
            }
            else if (extension == ".sln" || extension == ".slnx")
            {
                dte.Solution.Open(path);
            }
            else
            {
                // Project file or other — use open project command
                dte.Solution.AddFromFile(path);
            }
        }
    }
}
