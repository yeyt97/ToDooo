namespace ToDooo.Services;

/// <summary>
/// 账号打码。设置页要显示当前连接的 Apple ID，但完整邮箱摆在那儿截图就被看光了；
/// 这里只留 @ 前第一个字符，其余逐位换成星号（长度不变），域名保留以便辨认邮箱服务商。
/// </summary>
public static class AccountMask
{
    /// <summary>把 <c>yeyt97@163.com</c> 变成 <c>y*****@163.com</c>。</summary>
    public static string Apply(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return string.Empty;
        }

        var value = account.Trim();
        var at = value.IndexOf('@');
        var name = at < 0 ? value : value[..at];
        var domain = at < 0 ? string.Empty : value[at..];

        if (name.Length <= 1)
        {
            return name + domain;
        }

        return name[..1] + new string('*', name.Length - 1) + domain;
    }
}
