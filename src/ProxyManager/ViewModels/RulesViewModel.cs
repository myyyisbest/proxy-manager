using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyManager.Models;

namespace ProxyManager.ViewModels;

/// <summary>应用规则列表。批量操作只保存一次，图标更新不触发配置写入。</summary>
public partial class RulesViewModel : ObservableObject
{
    public ObservableCollection<AppRule> Rules => AppServices.Config.Current.Rules;
    readonly HashSet<AppRule> _hooked = new();
    List<AppRule> _savedRules = new();
    bool _batching;

    [ObservableProperty] private string _summaryText = "";

    public RulesViewModel()
    {
        Rules.CollectionChanged += OnRulesChanged;
        foreach (var rule in Rules) HookRule(rule);
        RememberRules();
        UpdateSummary();
    }

    void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var removed in _hooked) removed.PropertyChanged -= OnRuleChanged;
            _hooked.Clear();
            foreach (var rule in Rules) HookRule(rule);
        }
        else if (e.OldItems != null)
            foreach (AppRule removed in e.OldItems) { removed.PropertyChanged -= OnRuleChanged; _hooked.Remove(removed); }
        if (e.NewItems != null)
            foreach (AppRule rule in e.NewItems) HookRule(rule);
        if (_batching) return;
        if (Save()) UpdateSummary();
    }

    void HookRule(AppRule rule)
    {
        if (_hooked.Add(rule)) rule.PropertyChanged += OnRuleChanged;
    }

    void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppRule.Enabled)) return;
        if (Save()) UpdateSummary();
    }

    static AppRule CopyRule(AppRule rule) => new() { Id = rule.Id, ProcessName = rule.ProcessName, DisplayName = rule.DisplayName,
        ExePath = rule.ExePath, Enabled = rule.Enabled, Icon = rule.Icon };
    void RememberRules() => _savedRules = Rules.Select(CopyRule).ToList();
    bool Save()
    {
        try { AppServices.Config.Save(); RememberRules(); return true; }
        catch (Exception ex)
        {
            _batching = true;
            try { Rules.Clear(); foreach (var rule in _savedRules.Select(CopyRule)) Rules.Add(rule); }
            finally { _batching = false; }
            SummaryText = "修改未保存，已恢复原规则：" + ex.Message;
            AppServices.Log.Error(SummaryText);
            return false;
        }
    }

    void UpdateSummary()
    {
        var total = Rules.Count;
        var enabled = Rules.Count(r => r.Enabled);
        SummaryText = total == 0
            ? "添加需要走代理的应用，支持按完整路径区分同名程序"
            : $"共 {total} 个应用，{enabled} 个已启用 · 开启分流后生效，其他应用直连";
    }

    static string RuleKey(AppRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ExePath)) return "name:" + rule.ProcessName;
        try { return "path:" + Path.GetFullPath(rule.ExePath); }
        catch { return "path:" + rule.ExePath; }
    }

    /// <summary>有路径按完整路径去重，无路径才按进程名去重。</summary>
    public (int added, int skipped) AddRules(IEnumerable<AppRule> incoming)
    {
        var keys = new HashSet<string>(Rules.Select(RuleKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        _batching = true;
        try
        {
            foreach (var rule in incoming)
            {
                if (!keys.Add(RuleKey(rule))) { skipped++; continue; }
                Rules.Add(rule);
                added++;
            }
        }
        finally { _batching = false; }
        if (added > 0 && !Save()) return (0, skipped);
        UpdateSummary();
        return (added, skipped);
    }

    public void RemoveRule(AppRule rule) => Rules.Remove(rule);
}
