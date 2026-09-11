namespace ChatlogAI.Client.Bootstrap;

/// <summary>
/// 微信数据源暴露的一个账号，等价 launcher.py list_wechat_accounts() 里
/// {"account": ..., "name": ...} 这个字典。
///
/// 之所以要把 account 与 name 分开：account 是机器标识（wxid_xxx，会被写进 config.json
/// 供后端按账号选库），name 是给人看的昵称（可能是备注名/别名）。用户选的是人，
/// 程序记的必须是标识——两者混用会导致"选了账号但后端读的还是旧库"。
/// </summary>
/// <param name="Account">账号标识，等价 Python 的 item["account"]。</param>
/// <param name="Name">展示名，等价 Python 的 item["name"]。</param>
public sealed record WechatAccount(string Account, string Name)
{
    /// <summary>下拉/列表里的展示文案，逐字对齐 Python 的 "%s（%s）" % (name, account)。</summary>
    public string Display => $"{Name}（{Account}）";
}
