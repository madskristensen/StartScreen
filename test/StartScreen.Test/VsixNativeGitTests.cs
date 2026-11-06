using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibGit2Sharp;

namespace StartScreen.Test
{
    [TestClass]
    public class VsixNativeGitTests
    {
        [TestMethod]
        [DataRow("x64")]
        [DataRow("arm64")]
        [DataRow("x86")]
        public void Vsix_IncludesVersionMatchedNativeGitAtItsRuntimePath(string architecture)
        {
            using var package = ZipFile.OpenRead(GetVsixPath());
            var nativeName = GetNativeLibraryName();
            var entry = package.GetEntry($"lib/win32/{architecture}/{nativeName}.dll");

            Assert.IsNotNull(entry, $"The VSIX must ship {nativeName} for {architecture} beside the managed Git assembly.");
            Assert.IsGreaterThan(0L, entry.Length);
            Assert.IsNotNull(package.GetEntry("LibGit2Sharp.dll"));
        }

        [TestMethod]
        public async Task PackagedGit_OpensRepositoryWithoutBuildOutputOrMachineNativeLibrariesAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "StartScreen-PackagedGit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using (var package = ZipFile.OpenRead(GetVsixPath()))
                {
                    foreach (var entry in package.Entries.Where(entry => entry.FullName == "LibGit2Sharp.dll"
                        || entry.FullName.StartsWith("lib/win32/", StringComparison.Ordinal)))
                    {
                        var target = Path.Combine(directory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target);
                    }
                }

                const string script = "$ErrorActionPreference = 'Stop'; "
                    + "Add-Type -Path (Join-Path $PWD 'LibGit2Sharp.dll'); "
                    + "$path = Join-Path $PWD 'repository'; "
                    + "[LibGit2Sharp.Repository]::Init($path) | Out-Null; "
                    + "$repo = [LibGit2Sharp.Repository]::new($path); "
                    + "try { if ($repo.Info.IsBare) { throw 'Expected a working repository' }; "
                    + "Write-Output 'Native Git loaded' } finally { $repo.Dispose() }";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                            @"WindowsPowerShell\v1.0\powershell.exe"),
                        Arguments = "-NoProfile -NonInteractive -EncodedCommand "
                            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                        WorkingDirectory = directory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                Assert.IsTrue(process.Start());
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => process.WaitForExit(30000));
                if (!exited)
                {
                    process.Kill();
                    await Task.Run(() => process.WaitForExit());
                }

                var standardOutput = await output;
                var standardError = await error;
                Assert.IsTrue(exited, "The isolated native Git smoke test timed out.");
                Assert.AreEqual(0, process.ExitCode, standardError);
                StringAssert.Contains(standardOutput, "Native Git loaded");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string GetNativeLibraryName()
        {
            var method = typeof(Repository).Assembly.GetType("LibGit2Sharp.Core.NativeMethods")!
                .GetMethod("git_libgit2_init", BindingFlags.NonPublic | BindingFlags.Static)!;
            return method.GetCustomAttribute<DllImportAttribute>()!.Value;
        }

        private static string GetVsixPath([CallerFilePath] string sourceFile = "")
        {
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..",
                "src", "bin", configuration, "net48", "StartScreen.vsix"));
        }
    }
}