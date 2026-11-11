using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualStudioMcpServer.Providers;

/// <summary>
/// v1.4: 1 回限りの起動上書き（引数・作業ディレクトリ・環境変数）の値域と、request ID の衝突検出に使う SHA-256。
/// 値はログ・応答・台帳へ出さない。このファイルは VSIX と companion で同一の内容を保つ（hash の既知解を両方で検査する）。
/// </summary>
internal static class LaunchOverrideRules
{
    internal const int MaxArgumentsLength = 4096;
    internal const int MaxWorkingDirectoryLength = 1024;
    internal const int MaxEnvironmentKeys = 32;
    internal const int MaxEnvironmentTotalLength = 8192;
    internal const int MaxKeyLength = 128;

    private static readonly Regex KeyPattern = new Regex("^[A-Za-z_][A-Za-z0-9_().-]{0,127}\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// 値域の検査。違反の理由を英語の固定文で返す（値そのものは含めない）。問題がなければ null。
    /// 作業ディレクトリの実在確認は呼び出し側が行う。
    /// </summary>
    internal static string? Validate(string? arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        if (arguments is not null)
        {
            if (arguments.Length > MaxArgumentsLength) return "arguments exceeds 4096 characters";
            if (arguments.IndexOf('\0') >= 0) return "arguments contains a NUL character";
        }
        if (workingDirectory is not null)
        {
            if (workingDirectory.Length == 0 || workingDirectory.Length > MaxWorkingDirectoryLength)
                return "workingDirectory must be 1 to 1024 characters";
            if (workingDirectory.IndexOf('\0') >= 0) return "workingDirectory contains a NUL character";
            if (!IsAbsoluteWindowsPath(workingDirectory)) return "workingDirectory must be an absolute path";
        }
        if (environment is not null)
        {
            if (environment.Count > MaxEnvironmentKeys) return "environment exceeds 32 keys";
            var total = 0;
            foreach (var pair in environment)
            {
                if (pair.Key is null || !KeyPattern.IsMatch(pair.Key)) return "environment contains an invalid variable name";
                if (pair.Value is null) return "environment contains a null value";
                if (pair.Value.IndexOf('\0') >= 0) return "environment contains a NUL character";
                total += pair.Key.Length + pair.Value.Length;
                if (total > MaxEnvironmentTotalLength) return "environment exceeds 8192 characters in total";
            }
            // Windows の環境変数名は大文字小文字を区別しない。
            var distinct = new HashSet<string>(environment.Keys, StringComparer.OrdinalIgnoreCase);
            if (distinct.Count != environment.Count) return "environment contains names that differ only by case";
        }
        return null;
    }

    internal static bool HasAny(string? arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment) =>
        arguments is not null || workingDirectory is not null || environment is { Count: > 0 };

    /// <summary>
    /// 3 つの値の正準形の SHA-256（小文字 hex）。キーは大文字小文字を無視して並べ、長さつきで区切るため値の連結で衝突しない。
    /// null と空文字列は区別する。
    /// </summary>
    internal static string ComputeHash(string? arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        var builder = new StringBuilder();
        Append(builder, "a", arguments);
        Append(builder, "w", workingDirectory);
        if (environment is null) builder.Append("e:null\n");
        else
        {
            builder.Append("e:").Append(environment.Count).Append('\n');
            foreach (var pair in environment.OrderBy(static pair => pair.Key.ToUpperInvariant(), StringComparer.Ordinal))
            {
                Append(builder, "k", pair.Key.ToUpperInvariant());
                Append(builder, "v", pair.Value);
            }
        }
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes) hex.Append(value.ToString("x2"));
        return hex.ToString();
    }

    private static void Append(StringBuilder builder, string tag, string? value)
    {
        if (value is null) builder.Append(tag).Append(":null\n");
        else builder.Append(tag).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');
    }

    // 絶対パスはドライブ文字のものだけを受け付ける。UNC とデバイスパスは UI thread で応答を待たされ、外部へ接続も起こし得るため拒否する。
    private static bool IsAbsoluteWindowsPath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
