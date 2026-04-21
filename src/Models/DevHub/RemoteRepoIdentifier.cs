using System;
using System.Text.RegularExpressions;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Identifies a remote repository by host, owner, and repo name.
    /// For Azure DevOps, also includes the project name.
    /// </summary>
    public sealed class RemoteRepoIdentifier : IEquatable<RemoteRepoIdentifier>
    {
        /// <summary>
        /// The hostname (e.g., "github.com", "dev.azure.com").
        /// </summary>
        public string Host { get; }

        /// <summary>
        /// The repository owner or organization.
        /// </summary>
        public string Owner { get; }

        /// <summary>
        /// The repository name.
        /// </summary>
        public string Repo { get; }

        /// <summary>
        /// The project name (Azure DevOps only, null for other hosts).
        /// </summary>
        public string Project { get; }

        public RemoteRepoIdentifier(string host, string owner, string repo, string project = null)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Repo = repo ?? throw new ArgumentNullException(nameof(repo));
            Project = project;
        }

        /// <summary>
        /// Returns a display string like "owner/repo" or "org/project/repo" for ADO.
        /// </summary>
        public string DisplayName =>
            string.IsNullOrEmpty(Project)
                ? $"{Owner}/{Repo}"
                : $"{Owner}/{Project}/{Repo}";

        /// <summary>
        /// Attempts to parse a git remote URL into a RemoteRepoIdentifier.
        /// Supports HTTPS and SSH URLs for GitHub, Azure DevOps, Bitbucket, and GitLab.
        /// Returns null if the URL cannot be parsed.
        /// </summary>
        public static RemoteRepoIdentifier TryParse(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return null;

            remoteUrl = remoteUrl.Trim();

            // Azure DevOps SSH: git@ssh.dev.azure.com:v3/org/project/repo
            // Must be checked before the general SSH pattern
            var adoSshMatch = Regex.Match(remoteUrl, @"^git@ssh\.dev\.azure\.com:v3/([^/]+)/([^/]+)/(.+?)/?$");
            if (adoSshMatch.Success)
            {
                return new RemoteRepoIdentifier(
                    "dev.azure.com",
                    adoSshMatch.Groups[1].Value,
                    adoSshMatch.Groups[3].Value,
                    adoSshMatch.Groups[2].Value);
            }

            // SSH format: git@host:owner/repo.git
            var sshMatch = Regex.Match(remoteUrl, @"^git@([^:]+):(.+?)(?:\.git)?/?$");
            if (sshMatch.Success)
            {
                return ParseFromHostAndPath(sshMatch.Groups[1].Value, sshMatch.Groups[2].Value);
            }

            // HTTPS format: https://host/path.git
            if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
            {
                return ParseFromHostAndPath(uri.Host, uri.AbsolutePath.TrimStart('/'));
            }

            return null;
        }

        private static RemoteRepoIdentifier ParseFromHostAndPath(string host, string path)
        {
            // Remove trailing .git
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);

            // Remove trailing slash
            path = path.TrimEnd('/');

            // Azure DevOps HTTPS: dev.azure.com/org/project/_git/repo
            if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteRepoIdentifier("dev.azure.com", parts[0], parts[3], parts[1]);
                }
                // Fallback: dev.azure.com/org/repo (without project)
                if (parts.Length >= 2)
                {
                    return new RemoteRepoIdentifier("dev.azure.com", parts[0], parts[parts.Length - 1], parts.Length > 2 ? parts[1] : null);
                }
                return null;
            }

            // Legacy Visual Studio Online: org.visualstudio.com/project/_git/repo
            if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                var org = host.Substring(0, host.IndexOf('.'));
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1].Equals("_git", StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteRepoIdentifier("dev.azure.com", org, parts[2], parts[0]);
                }
                if (parts.Length >= 1)
                {
                    return new RemoteRepoIdentifier("dev.azure.com", org, parts[parts.Length - 1], parts.Length > 1 ? parts[0] : null);
                }
                return null;
            }

            // GitHub, Bitbucket, GitLab: host/owner/repo
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                return new RemoteRepoIdentifier(host, segments[0], segments[1]);
            }

            return null;
        }

        public bool Equals(RemoteRepoIdentifier other)
        {
            if (other is null) return false;
            return string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Repo, other.Repo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Project, other.Project, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => Equals(obj as RemoteRepoIdentifier);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Host);
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Owner);
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Repo);
                if (Project != null)
                    hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Project);
                return hash;
            }
        }

        public override string ToString() => $"{Host}/{DisplayName}";
    }
}
