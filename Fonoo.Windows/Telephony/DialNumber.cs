namespace Fonoo.Windows.Telephony;

public static class DialNumber
{
    public static string? Normalize(string? input)
    {
        if (input is not { Length: > 0 and <= 256 }) return null;
        if (input.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) input = input[4..];
        var number = string.Concat(input.Where(c => c is not (' ' or '-' or '(' or ')' or '.' or '\u00a0' or '\u202f' or '\u2009')));
        return IsValid(number) ? number : null;
    }

    public static bool IsValid(string? number) => number is { Length: > 0 and <= 64 } &&
        number.All(c => "0123456789+*#".Contains(c)) && number.Count(c => c == '+') <= 1 &&
        (!number.Contains('+') || number[0] == '+') && number.Any(char.IsAsciiDigit);
}
