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
        /// The hostname (e.g., "github.com", "dev.azure.com", or an Azure DevOps Server host).
        /// For both cloud Azure DevOps and legacy *.visualstudio.com URLs, this is normalized
        /// to "dev.azure.com". For on-premises Azure DevOps Server, this is the actual server host.
        /// </summary>
        public string Host { get; }

        /// <summary>
        /// The repository owner or organization. For Azure DevOps Server (on-premises),
        /// this is the collection name.
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

        /// <summary>
        /// The base API URL for Azure DevOps repositories (cloud, legacy or on-premises).
        /// Includes the scheme, host, optional port, and the path up to and including the
        /// collection or organization. Examples:
        ///   https://dev.azure.com/myorg
        ///   https://myorg.visualstudio.com
        ///   https://tfs.contoso.com/tfs/DefaultCollection
        /// Null for non-Azure-DevOps hosts.
        /// </summary>
        public string BaseUrl { get; }

        /// <summary>
        /// True when this identifier points at an on-premises Azure DevOps Server
        /// (i.e., not dev.azure.com / visualstudio.com).
        /// </summary>
        public bool IsAzureDevOpsServer { get; }

        public RemoteRepoIdentifier(string host, string owner, string repo, string project = null, string baseUrl = null, bool isAzureDevOpsServer = false)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Repo = repo ?? throw new ArgumentNullException(nameof(repo));
            Project = project;
            BaseUrl = baseUrl;
            IsAzureDevOpsServer = isAzureDevOpsServer;
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
                var org = adoSshMatch.Groups[1].Value;
                return new RemoteRepoIdentifier(
                    "dev.azure.com",
                    org,
                    adoSshMatch.Groups[3].Value,
                    adoSshMatch.Groups[2].Value,
                    baseUrl: $"https://dev.azure.com/{org}");
            }

            // SSH format: git@host:owner/repo.git
            var sshMatch = Regex.Match(remoteUrl, @"^git@([^:]+):(.+?)(?:\.git)?/?$");
            if (sshMatch.Success)
            {
                return ParseFromHostAndPath(null, sshMatch.Groups[1].Value, sshMatch.Groups[2].Value);
            }

            // HTTPS format: https://host/path.git
            if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
            {
                return ParseFromHostAndPath(uri, uri.Host, uri.AbsolutePath.TrimStart('/'));
            }

            return null;
        }

        private static RemoteRepoIdentifier ParseFromHostAndPath(Uri uri, string host, string path)
        {
            // Remove trailing .git
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);

            // Remove trailing slash
            path = path.TrimEnd('/');

            // Authority (scheme://host[:port]) for building BaseUrl. Defaults to https for SSH-derived inputs.
            var authority = uri != null
                ? uri.GetLeftPart(UriPartial.Authority)
                : $"https://{host}";

            // Azure DevOps cloud HTTPS: dev.azure.com/org/project/_git/repo
            if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteRepoIdentifier(
                        "dev.azure.com",
                        parts[0],
                        parts[3],
                        parts[1],
                        baseUrl: $"https://dev.azure.com/{parts[0]}");
                }
                // Fallback: dev.azure.com/org/repo (without project)
                if (parts.Length >= 2)
                {
                    return new RemoteRepoIdentifier(
                        "dev.azure.com",
                        parts[0],
                        parts[parts.Length - 1],
                        parts.Length > 2 ? parts[1] : null,
                        baseUrl: $"https://dev.azure.com/{parts[0]}");
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
                    return new RemoteRepoIdentifier(
                        "dev.azure.com",
                        org,
                        parts[2],
                        parts[0],
                        baseUrl: authority);
                }
                if (parts.Length >= 1)
                {
                    return new RemoteRepoIdentifier(
                        "dev.azure.com",
                        org,
                        parts[parts.Length - 1],
                        parts.Length > 1 ? parts[0] : null,
                        baseUrl: authority);
                }
                return null;
            }

            // On-premises Azure DevOps Server: any host with a "/_git/" segment.
            // Path layout: [optional/path/segments/]Collection/Project/_git/Repo
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                int gitIdx = Array.FindIndex(parts, p => p.Equals("_git", StringComparison.OrdinalIgnoreCase));
                if (gitIdx >= 2 && gitIdx < parts.Length - 1)
                {
                    var project = parts[gitIdx - 1];
                    var repo = parts[gitIdx + 1];
                    var collection = parts[gitIdx - 2];
                    var prefixSegments = string.Join("/", parts, 0, gitIdx - 1);
                    return new RemoteRepoIdentifier(
                        host,
                        collection,
                        repo,
                        project,
                        baseUrl: $"{authority}/{prefixSegments}",
                        isAzureDevOpsServer: true);
                }
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
