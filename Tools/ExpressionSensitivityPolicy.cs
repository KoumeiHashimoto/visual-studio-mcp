namespace VisualStudioMcpServer.Tools;

/// <summary>式をディスパッチする前にデバッガ疑似変数を分類します。</summary>
internal static class ExpressionSensitivityPolicy
{
    internal static (string? Code, string? Message) Validate(
        string expression,
        string language,
        bool allowSensitiveRead,
        bool allowSideEffects)
    {
        var tokens = ReadTokens(expression);
        if (tokens.Error is not null)
            return ("unsupported_expression_token", tokens.Error);
        if (tokens.Values.Count == 0) return (null, null);

        var normalizedLanguage = language.Trim().ToLowerInvariant();
        if (normalizedLanguage is not ("cpp" or "csharp" or "basic"))
            return ("expression_language_unsupported", "Pseudovariables are not evaluated when the debugger language is unknown.");

        foreach (var token in tokens.Values)
        {
            var kind = Classify(normalizedLanguage, token);
            if (kind == TokenKind.Unknown)
                return ("unsupported_expression_token", $"The pseudovariable '{token}' is not supported for the current debugger language.");
            if (kind == TokenKind.Sensitive && !allowSensitiveRead)
                return ("sensitive_read_policy_required", $"The pseudovariable '{token}' reads sensitive debugger or process data. Set allowSensitiveRead=true explicitly.");
            if (kind == TokenKind.Mutating && !allowSideEffects)
                return ("side_effect_policy_required", $"The pseudovariable '{token}' can mutate debugger state. Set allowSideEffects=true explicitly.");
        }

        return (null, null);
    }

    private static TokenKind Classify(string language, string token)
    {
        if (language == "cpp")
        {
            if (token is "$env" or "$cmdline" or "$user") return TokenKind.Sensitive;
            if (token == "$returnvalue") return TokenKind.Safe;
            return TokenKind.Unknown;
        }

        if (language == "csharp")
        {
            if (token == "$user") return TokenKind.Sensitive;
            if (token is "$exception" or "$returnvalue") return TokenKind.Safe;
            return TokenKind.Unknown;
        }

        if (token is "$delete" or "$$delete") return TokenKind.Mutating;
        if (token == "$exception") return TokenKind.Safe;
        return TokenKind.Unknown;
    }

    private static (List<string> Values, string? Error) ReadTokens(string expression)
    {
        var values = new List<string>();
        char quote = '\0';
        var escaped = false;
        for (var index = 0; index < expression.Length; index++)
        {
            var current = expression[index];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (current == '\\') { escaped = true; continue; }
                if (current == quote) quote = '\0';
                continue;
            }

            if (current is '\'' or '"')
            {
                if (current == '"' && index > 0 && expression[index - 1] == '$')
                    return (values, "Interpolated strings are not scanned for pseudovariables safely.");
                quote = current;
                continue;
            }
            if (current == '@' && index + 1 < expression.Length && IsIdentifierStart(expression[index + 1]))
                return (values, "Register and escaped-identifier forms are not classified for sensitive-read access.");
            if (current != '$') continue;

            var start = index;
            while (index + 1 < expression.Length && expression[index + 1] == '$') index++;
            if (index + 1 >= expression.Length || !IsIdentifierStart(expression[index + 1]))
                return (values, "Bare, numeric, register, and object-id pseudovariables are not supported.");
            index++;
            while (index + 1 < expression.Length && IsIdentifierPart(expression[index + 1])) index++;
            values.Add(expression[start..(index + 1)].ToLowerInvariant());
        }

        if (quote != '\0') return (values, "The expression contains an unterminated string literal.");
        return (values, null);
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';
    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private enum TokenKind { Safe, Sensitive, Mutating, Unknown }
}
