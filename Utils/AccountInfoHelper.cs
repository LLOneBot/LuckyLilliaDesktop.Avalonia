using System.Linq;

namespace LuckyLilliaDesktop.Utils;

public static class AccountInfoHelper
{
    public static bool IsValidQQUin(string? uin)
    {
        return !string.IsNullOrWhiteSpace(uin)
            && uin.Length is >= 5 and <= 12
            && uin.All(char.IsDigit);
    }
}
