using System.Text.RegularExpressions;

namespace Serpy.Core.Versions;

/// <summary>
/// Pulls the version number out of what each guest tool prints for <c>--version</c>. The R9 gate and
/// the replacement comparison work on bare versions, and the tools print banners. The first real
/// build measured these exact outputs:
/// <code>
/// Python 3.14.7
/// v24.2.0
/// mariadb from 11.8.3-MariaDB, client 15.2 for debian-linux-gnu (x86_64) using  EditLine wrapper
/// Redis server v=8.0.2 sha=00000000:0 malloc=jemalloc-5.3.0 bits=64 build=b52b02bf0759f5b4
/// </code>
/// A banner that has no version is a hard failure that quotes the output, never a guess.
/// </summary>
public static partial class GuestVersionParser
{
    private const string Number = @"\d+(?:\.\d+)+";

    [GeneratedRegex(@"^Python\s+(?<v>" + Number + ")", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PythonRegex();

    [GeneratedRegex(@"^v?(?<v>" + Number + ")", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex NodeRegex();

    // MariaDB 11.8 prints "mariadb from 11.8.3-MariaDB, client ..."; earlier series print
    // "Ver 15.1 Distrib 10.11.6-MariaDB, for ...". The server version is the one after "from" or "Distrib".
    [GeneratedRegex(@"(?:\bfrom|\bDistrib)\s+(?<v>" + Number + ")", RegexOptions.CultureInvariant)]
    private static partial Regex MariaDbRegex();

    [GeneratedRegex(@"\bv=?(?<v>" + Number + ")", RegexOptions.CultureInvariant)]
    private static partial Regex RedisRegex();

    public static string Python(string output) => Extract("Python", PythonRegex(), output);

    public static string Node(string output) => Extract("Node.js", NodeRegex(), output);

    public static string MariaDb(string output) => Extract("MariaDB", MariaDbRegex(), output);

    public static string Redis(string output) => Extract("Redis", RedisRegex(), output);

    private static string Extract(string tool, Regex pattern, string output)
    {
        var match = pattern.Match(output.Trim());
        if (!match.Success)
            throw new InvalidDataException(
                $"The {tool} version could not be read from its output: {output.Trim()}");
        return match.Groups["v"].Value;
    }
}
