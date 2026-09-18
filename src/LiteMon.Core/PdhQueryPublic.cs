namespace LiteMon.Core.Interop;

/// <summary>诊断用公开包装。</summary>
public static class PdhQueryPublic
{
    public static List<string>? ExpandTest(string wildPath) => PdhQuery.ExpandWildCard(wildPath);
}
