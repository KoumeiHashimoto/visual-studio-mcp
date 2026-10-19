namespace VisualStudioMcpServer.Tools;

/// <summary>式をディスパッチする前にデバッガ疑似変数を分類します。</summary>
internal static class ExpressionSensitivityPolicy
{
    /// <summary>
    /// 実 frame から読んだ言語で判定します。EnvDTE の frame は dynamic のため、呼び出し側の値が dynamic でも
    /// 結果の Code/Message を実行時に解決できるよう、tuple ではなく名前付き型で返します。
    /// </summary>
    internal static ExpressionPolicyDecision ValidateForFrame(
        string expression,
        object? frameLanguage,
        bool allowSensitiveRead,
        bool allowSideEffects)
    {
        var (code, message) = Validate(expression, frameLanguage as string ?? string.Empty, allowSensitiveRead, allowSideEffects);
        return new ExpressionPolicyDecision(code, message);
    }

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

            // コメントは評価器が読み飛ばす一方で本スキャナの引用符の対応を壊せるため、fail closed で拒否する。
            if (current == '/' && index + 1 < expression.Length && expression[index + 1] is '/' or '*')
                return (values, "Comments are not scanned for pseudovariables safely.");
            if (current == '\'')
            {
                // 有効な文字リテラル（'x'、'\n'、'\x41' など）だけを許す。C++ の桁区切りなど他の用途は引用符の対応を壊すため拒否する。
                var length = CharLiteralLength(expression, index);
                if (length == 0) return (values, "An apostrophe that is not a valid character literal is not scanned safely.");
                index += length - 1;
                continue;
            }
            if (current == '"')
            {
                // verbatim・補間 verbatim・C++ raw 文字列は escape 規則が異なり、本スキャナでは境界を決められないため拒否する。
                if (index > 0 && expression[index - 1] is '$' or '@' or 'R')
                    return (values, "Interpolated, verbatim, and raw string literals are not scanned for pseudovariables safely.");
                // C# 11 の raw 文字列（"""）は escape 規則が異なり境界を決められないため、連続する 3 個以上の引用符を拒否する。
                if (index + 2 < expression.Length && expression[index + 1] == '"' && expression[index + 2] == '"')
                    return (values, "Interpolated, verbatim, and raw string literals are not scanned for pseudovariables safely.");
                quote = current;
                continue;
            }
            if (current == '@' && index + 1 < expression.Length && expression[index + 1] is '"' or '$')
                return (values, "Interpolated, verbatim, and raw string literals are not scanned for pseudovariables safely.");
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

    /// <summary>index の位置の引用符から始まる有効な文字リテラルの長さ。有効でなければ 0。</summary>
    private static int CharLiteralLength(string text, int index)
    {
        var next = index + 1;
        if (next >= text.Length) return 0;
        if (text[next] != '\\')
            return text[next] != '\'' && next + 1 < text.Length && text[next + 1] == '\'' ? 3 : 0;
        // 単純 escape（'\n'）または英数字だけの長い escape（'\x41'、'\u0041'、'\0'）。
        var end = next + 1;
        if (end >= text.Length || text[end] == '\'') return 0;
        end++;
        while (end < text.Length && char.IsLetterOrDigit(text[end]) && end - next <= 10) end++;
        return end < text.Length && text[end] == '\'' ? end - index + 1 : 0;
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';
    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private enum TokenKind { Safe, Sensitive, Mutating, Unknown }
}

/// <summary>疑似変数ポリシーの判定結果。dynamic 経由でも名前で読めるよう record class とします。</summary>
internal sealed record ExpressionPolicyDecision(string? Code, string? Message);
