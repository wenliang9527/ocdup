namespace Ocdup.Config;

/// <summary>
/// 配置的进程内持有者（单例）。
/// 所有工具都从这里取「当前有效配置」，而不是各自调用 OcdConfig.Load()：
/// 否则会出现「有的工具用启动时的快照、有的工具每次重读」的时机不一致，
/// 将来 configure/set_permissions 这类运行时修改也会互相看不见（设计文档 4.3 的分层需要单一入口）。
/// </summary>
public sealed class ConfigStore
{
    private static readonly Lazy<ConfigStore> LazyInstance = new(() => new ConfigStore(OcdConfig.Load()));

    public static ConfigStore Instance => LazyInstance.Value;

    private OcdConfig _current;

    private ConfigStore(OcdConfig initial) => _current = initial;

    /// <summary>当前有效配置。</summary>
    public OcdConfig Current => Volatile.Read(ref _current);

    /// <summary>重新从磁盘加载（对应设计文档 4.3 的「运行时修改」层；编辑 ocd.json 后无需重启服务器）。</summary>
    public void Reload() => Volatile.Write(ref _current, OcdConfig.Load());

    /// <summary>
    /// 运行时权限补丁（set_permissions 通道）：基于当前配置应用 JSON 变更并原子换入。
    /// 调用方（工具层）负责闸门检查与键白名单；reload from disk 会丢弃 runtime 补丁。
    /// </summary>
    public OcdConfig ApplyRuntimePatch(string json)
    {
        var next = _current.WithRuntimeChanges(json);
        Volatile.Write(ref _current, next);
        return next;
    }
}
