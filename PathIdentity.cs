using System;
using System.IO;

namespace Koi
{
    public static class PathIdentity
    {
        // File identities follow Windows case-insensitive semantics. URL paths do not.
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string value = path.Trim().Trim('"');
            if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return value.ToLowerInvariant();
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.AbsoluteUri;
            value = Environment.ExpandEnvironmentVariables(value);
            try
            {
                value = Path.GetFullPath(value);
                string root = Path.GetPathRoot(value);
                if (value.Length > root.Length)
                    value = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return value.ToLowerInvariant();
        }
    }
}
