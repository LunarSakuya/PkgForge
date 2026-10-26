using System.Text;
using System.Text.RegularExpressions;

namespace PkgForge;

public static partial class SecureText
{
    public static string ForConsole(string value, int maxLength = 4096)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (builder.Length >= maxLength)
            {
                builder.Append("...");
                break;
            }

            builder.Append(char.IsControl(character) ||
                           character is >= '\u202A' and <= '\u202E' or
                               >= '\u2066' and <= '\u2069' or
                               '\u200E' or '\u200F' or '\uFEFF'
                ? ' '
                : character);
        }

        return builder.ToString();
    }

    public static string RedactSecrets(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return ProxyCredentialsPattern().Replace(value, "$1://***@");
    }

    [GeneratedRegex(@"(?i)\b(https?|socks4a?|socks5)://[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex ProxyCredentialsPattern();
}
