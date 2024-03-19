using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PullRequestBot.Common
{
    public static class Extensions
    {
        private static Regex AccountUrlLegacyPattern = new Regex(@"https://(?<account>[^.]+)\.visualstudio\.com", RegexOptions.IgnoreCase);
        private static Regex AccountUrlPattern = new Regex(@"https://dev\.azure\.com/(?<account>[^.]+)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Extracts the account (aka org) name from an Azure DevOps url
        /// </summary>
        /// <param name="accountUrl">ADO url</param>
        /// <returns>account (aka org) name</returns>
        public static string ExtractAccountName(this string accountUrl)
        {
            var newUrlPatternMatch = AccountUrlPattern.Match(accountUrl);
            if (newUrlPatternMatch.Success)
            {
                return newUrlPatternMatch.Groups["account"].Value;
            }
            var legacyUrlPatternMatch = AccountUrlLegacyPattern.Match(accountUrl);
            if (legacyUrlPatternMatch.Success)
            {
                return legacyUrlPatternMatch.Groups["account"].Value;
            }
            return null;
        }

        /// <summary>
        /// Take a path in Git format ("/src/foo/a/b/c.txt") and return the parent directory,
        /// also in Git format ("/src/foo/a/b"). Case and forward slashes need to be preserved.
        /// </summary>
        /// <param name="path">Input path (file or directory)</param>
        /// <returns>Parent directory name or null if there isn't one</returns>
        public static string GetParentGitDirectoryName(this string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return null;
            }

            // Force paths to use forward-slashes, because Path.GetDirectoryName return results
            // with the current OS's default path separator, which on Windows is a backslash.
            return Path.GetDirectoryName(path)?.Replace(@"\", "/");
        }

        /// <summary>
        /// Finds the unique parent directories for a set of paths
        /// </summary>
        /// <param name="paths">set of paths</param>
        /// <returns>Unique parent directories in git format (using forward slashes for separators)</returns>
        public static IEnumerable<string> GetUniqueParentGitDirectories(this IEnumerable<string> paths)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var directories = paths.Select(x => x.GetParentGitDirectoryName())
                .Where(x => x != null)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in directories)
            {
                var directory = candidate;
                while (!string.IsNullOrWhiteSpace(directory))
                {
                    if (!visited.Contains(directory))
                    {
                        yield return directory;
                        visited.Add(directory);
                    }
                    directory = directory.GetParentGitDirectoryName();
                }
            }
        }
    }
}
