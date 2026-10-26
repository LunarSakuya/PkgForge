namespace PkgForge.Cli;

internal sealed class ParsedCommandLine
{
    public const int MaxArgumentLength = 8192;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => _flags.Contains(name);

    public static ParsedCommandLine Parse(
        IReadOnlyList<string> arguments,
        IReadOnlySet<string> valueOptions,
        IReadOnlySet<string> flagOptions,
        IReadOnlyDictionary<string, string> aliases)
    {
        var result = new ParsedCommandLine();
        var optionsEnded = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Length > MaxArgumentLength)
            {
                throw PkgException.Usage($"An argument exceeds the {MaxArgumentLength} character limit.");
            }

            if (optionsEnded)
            {
                result._positionals.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = argument.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? argument[2..] : argument[2..equals];
                var inlineValue = equals < 0 ? null : argument[(equals + 1)..];
                name = aliases.TryGetValue(name, out var alias) ? alias : name;
                if (flagOptions.Contains(name))
                {
                    if (inlineValue is not null)
                    {
                        throw PkgException.Usage($"Option '--{name}' does not take a value.");
                    }

                    if (!result._flags.Add(name))
                    {
                        throw PkgException.Usage($"Option '--{name}' was specified more than once.");
                    }

                    continue;
                }

                if (!valueOptions.Contains(name))
                {
                    throw PkgException.Usage($"Unknown option '--{name}'.");
                }

                string value;
                if (inlineValue is not null)
                {
                    value = inlineValue;
                }
                else
                {
                    if (index + 1 >= arguments.Count)
                    {
                        throw PkgException.Usage($"Option '--{name}' requires a value.");
                    }

                    var candidate = arguments[index + 1];
                    if (candidate.Length > 1 && candidate.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw PkgException.Usage(
                            $"Option '--{name}' requires a value; use --{name}=VALUE for values beginning with '-'.");
                    }

                    value = arguments[++index];
                }

                if (value.Length == 0)
                {
                    throw PkgException.Usage($"Option '--{name}' requires a non-empty value.");
                }

                if (!result._values.TryAdd(name, value))
                {
                    throw PkgException.Usage($"Option '--{name}' was specified more than once.");
                }

                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal) && argument.Length > 1)
            {
                var name = aliases.TryGetValue(argument[1..], out var alias) ? alias : argument[1..];
                if (!flagOptions.Contains(name))
                {
                    throw PkgException.Usage($"Unknown option '{argument}'.");
                }

                if (!result._flags.Add(name))
                {
                    throw PkgException.Usage($"Option '{argument}' was specified more than once.");
                }

                continue;
            }

            result._positionals.Add(argument);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> CommonAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["y"] = "yes",
            ["h"] = "help",
        };
}
