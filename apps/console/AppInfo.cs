namespace WorkBuddyRemote.WinUI;

/// <summary>
/// 与 WorkBuddy 客户端相关的版本约定。升级 WorkBuddy 后先跑一遍兼容性验证
/// （桥接 /readyz、页面加载、shim 注入），再改这里的版本号。
/// </summary>
internal static class AppInfo
{
    /// <summary>当前已验证适配的 WorkBuddy 版本。</summary>
    public const string AdaptedWorkBuddyVersion = "5.9.2";

    /// <summary>同一套桥接逻辑验证过的版本区间，用于提示用户。</summary>
    public const string AdaptedWorkBuddyRange = "5.9.0 – 5.9.2";

    public static string AdaptedWorkBuddyLabel =>
        $"{AdaptedWorkBuddyVersion}（已验证 {AdaptedWorkBuddyRange}）";
}
