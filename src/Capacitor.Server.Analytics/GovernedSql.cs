using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Server.Analytics;

internal static class GovernedSql {
    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase) {
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "UNION", "INTERSECT", "EXCEPT",
        "WINDOW", "RETURNING"
    };

    private static readonly HashSet<string> AliasBreakers = new(StringComparer.OrdinalIgnoreCase) {
        "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "UNION", "INTERSECT", "EXCEPT",
        "WINDOW", "RETURNING", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS",
        "FULL", "NATURAL", "ON", "USING", "SET", "WHEN", "FROM", "SELECT", "WITH"
    };

    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase) {
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "ATTACH", "DETACH",
        "PRAGMA", "VACUUM", "REINDEX"
    };

    private static readonly Regex CteNamePattern = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string Rewrite(
        string sql,
        IReadOnlySet<string> governedViews,
        string scopePredicate = "repo_hash = $scope OR $scope = 'global'") {
        var stripped = StripCommentsAndWhitespaceGlue(sql).Trim();
        if (stripped.Length == 0) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        var withoutTrailingSemi = stripped.TrimEnd().TrimEnd(';').TrimEnd();
        if (withoutTrailingSemi.Contains(';', StringComparison.Ordinal)) {
            throw new InvalidOperationException("Governed analytics query must be a single statement.");
        }

        var tokens = Tokenize(withoutTrailingSemi);
        if (tokens.Count == 0) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        var first = tokens[0].Text.ToUpperInvariant();
        if (first is not ("SELECT" or "WITH")) {
            throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
        }

        foreach (var token in tokens) {
            if (token.Kind == TokenKind.Ident && WriteKeywords.Contains(token.Text)) {
                throw new InvalidOperationException("Governed analytics query must be a read-only SELECT statement.");
            }
        }

        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CteNamePattern.Matches(withoutTrailingSemi)) {
            cteNames.Add(m.Groups[1].Value);
        }

        var replacements = new List<Replacement>();
        var expectingTable = false;
        var depth = 0;
        var activeFromListDepths = new HashSet<int>();
        var i = 0;
        while (i < tokens.Count) {
            var token = tokens[i];

            if (expectingTable) {
                if (token.Kind == TokenKind.Punct && token.Text == "(") {
                    expectingTable = false;
                    depth++;
                    i++;
                    continue;
                }

                if (token.Kind == TokenKind.Ident) {
                    if (i + 1 < tokens.Count && tokens[i + 1].Text == ".") {
                        var qualifier = i + 2 < tokens.Count ? tokens[i + 2].Text : "";
                        throw new InvalidOperationException(
                            $"Governed analytics query referenced a non-governed table or view: {token.Text}.{qualifier}");
                    }

                    var view = token.Text;
                    var start = token.Start;
                    var end = token.Start + token.Length;
                    i++;
                    if (i < tokens.Count && tokens[i].Text.Equals("AS", StringComparison.OrdinalIgnoreCase)) {
                        i++;
                    }

                    string? alias = null;
                    if (i < tokens.Count
                        && tokens[i].Kind == TokenKind.Ident
                        && !AliasBreakers.Contains(tokens[i].Text)) {
                        alias = tokens[i].Text;
                        end = tokens[i].Start + tokens[i].Length;
                        i++;
                    }

                    if (!cteNames.Contains(view)) {
                        if (!governedViews.Contains(view)) {
                            throw new InvalidOperationException(
                                $"Governed analytics query referenced a non-governed table or view: {view}");
                        }

                        replacements.Add(new Replacement(start, end - start, view, alias ?? view));
                    }

                    expectingTable = false;
                    continue;
                }

                throw new InvalidOperationException("Governed analytics query has an unrecognized table source.");
            }

            if (token.Kind == TokenKind.Punct && token.Text == "(") {
                depth++;
                i++;
                continue;
            }

            if (token.Kind == TokenKind.Punct && token.Text == ")") {
                depth--;
                i++;
                continue;
            }

            if (token.Kind == TokenKind.Ident && token.Text.Equals("FROM", StringComparison.OrdinalIgnoreCase)
                || token.Kind == TokenKind.Ident && token.Text.Equals("JOIN", StringComparison.OrdinalIgnoreCase)) {
                expectingTable = true;
                activeFromListDepths.Add(depth);
                i++;
                continue;
            }

            if (token.Kind == TokenKind.Punct && token.Text == "," && activeFromListDepths.Contains(depth)) {
                expectingTable = true;
                i++;
                continue;
            }

            if (token.Kind == TokenKind.Ident && ClauseKeywords.Contains(token.Text)) {
                activeFromListDepths.Remove(depth);
            }

            i++;
        }

        if (expectingTable) {
            throw new InvalidOperationException("Governed analytics query has an unrecognized table source.");
        }

        var rewritten = new StringBuilder(withoutTrailingSemi);
        for (var r = replacements.Count - 1; r >= 0; r--) {
            var rep = replacements[r];
            var fragment = $"(SELECT * FROM {rep.View} WHERE {scopePredicate}) {rep.Alias}";
            rewritten.Remove(rep.Start, rep.Length);
            rewritten.Insert(rep.Start, fragment);
        }

        return rewritten.ToString();
    }

    private static string StripCommentsAndWhitespaceGlue(string sql) {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length) {
            var c = sql[i];
            if (c is '\'' or '"') {
                var quote = c;
                sb.Append(c);
                i++;
                while (i < sql.Length) {
                    sb.Append(sql[i]);
                    if (sql[i] == quote) {
                        if (i + 1 < sql.Length && sql[i + 1] == quote) {
                            sb.Append(sql[i + 1]);
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') {
                    i++;
                }

                sb.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) {
                    i++;
                }

                i = Math.Min(i + 2, sql.Length);
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static List<Token> Tokenize(string sql) {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length) {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) {
                i++;
                continue;
            }

            if (c is '\'' or '"' or '[') {
                var close = c == '[' ? ']' : c;
                var start = i;
                i++;
                while (i < sql.Length) {
                    if (sql[i] == close) {
                        if (c != '[' && i + 1 < sql.Length && sql[i + 1] == close) {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                var raw = sql[start..i];
                var ident = c == '\''
                    ? raw
                    : Unquote(raw);
                tokens.Add(new Token(ident, start, i - start, c == '\'' ? TokenKind.String : TokenKind.Ident));
                continue;
            }

            if (char.IsLetter(c) || c == '_') {
                var start = i;
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) {
                    i++;
                }

                tokens.Add(new Token(sql[start..i], start, i - start, TokenKind.Ident));
                continue;
            }

            tokens.Add(new Token(sql[i].ToString(), i, 1, TokenKind.Punct));
            i++;
        }

        return tokens;
    }

    private static string Unquote(string raw) {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') {
            return raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']') {
            return raw[1..^1];
        }

        return raw;
    }

    private enum TokenKind { Ident, String, Punct }

    private readonly record struct Token(string Text, int Start, int Length, TokenKind Kind);

    private readonly record struct Replacement(int Start, int Length, string View, string Alias);
}
