using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyLilliaDesktop.Utils;
using ReactiveUI;

namespace LuckyLilliaDesktop.ViewModels;

/// <summary>
/// 字段选项定义
/// </summary>
public record FieldOption(string Value, string Label, string Type, SelectOption[]? Options = null);
public record SelectOption(string Value, string Label);

/// <summary>
/// 操作符选项定义
/// </summary>
public record OperatorOption(string Value, string Label);

/// <summary>
/// 单条过滤规则的 ViewModel
/// </summary>
public class FilterRuleViewModel : ReactiveObject
{
    public int Id { get; }

    private string _field = "post_type";
    public string Field
    {
        get => _field;
        set
        {
            if (_field == value) return;
            var previousValue = _value;
            _field = value;
            // 换字段后旧值往往对新字段无意义: select 字段的值下拉里找不到旧值只会显示空, 但 Value 还留着,
            // 于是写出 {"message_type":"message"} 这种永远匹配不上的条件
            if (!IsValueCompatibleWithField()) _value = "";
            this.RaisePropertyChanged(nameof(Field));
            this.RaisePropertyChanged(nameof(CurrentFieldDef));
            this.RaisePropertyChanged(nameof(IsSelectField));
            this.RaisePropertyChanged(nameof(IsNumberField));
            this.RaisePropertyChanged(nameof(IsTextField));
            this.RaisePropertyChanged(nameof(FieldOptions));
            this.RaisePropertyChanged(nameof(ValuePlaceholder));
            this.RaisePropertyChanged(nameof(SelectedFieldOption));
            if (_value != previousValue) this.RaisePropertyChanged(nameof(Value));
            this.RaisePropertyChanged(nameof(SelectedValueOption));
            NotifyModified();
        }
    }

    // 换字段后已有值是否还配得上新字段的类型
    private bool IsValueCompatibleWithField()
    {
        if (_value.Length == 0) return true;
        var def = CurrentFieldDef;
        if (def == null) return true;

        var items = IsListOperator
            ? _value.Split(',', '，').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray()
            : new[] { _value };
        if (items.Length == 0) return true;

        return def.Type switch
        {
            "select" => def.Options != null && items.All(i => def.Options.Any(o => o.Value == i)),
            "number" => items.All(i => long.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
            _ => true, // text 字段接受任意值
        };
    }

    public FieldOption? SelectedFieldOption
    {
        get => EventFilterViewModel.FieldOptions.FirstOrDefault(f => f.Value == Field);
        set
        {
            if (value != null) Field = value.Value;
        }
    }

    private string _operator = "$eq";
    public string Operator
    {
        get => _operator;
        set
        {
            if (_operator == value) return;
            var wasListOp = _operator is "$in" or "$nin";
            var isListOp = value is "$in" or "$nin";
            this.RaiseAndSetIfChanged(ref _operator, value);
            this.RaisePropertyChanged(nameof(SelectedOperatorOption));
            this.RaisePropertyChanged(nameof(IsListOperator));
            this.RaisePropertyChanged(nameof(IsNotListOperator));
            this.RaisePropertyChanged(nameof(ValuePlaceholder));
            // 列表→单值：取第一项
            if (wasListOp && !isListOp)
            {
                var items = _value.Split(',', '，').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                if (items.Length > 0) Value = items[0];
            }
            NotifyModified();
        }
    }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            this.RaiseAndSetIfChanged(ref _value, value ?? "");
            this.RaisePropertyChanged(nameof(SelectedValueOption));
            NotifyModified();
        }
    }

    public OperatorOption? SelectedOperatorOption
    {
        get => EventFilterViewModel.OperatorOptions.FirstOrDefault(o => o.Value == Operator);
        set
        {
            if (value != null) Operator = value.Value;
        }
    }

    public SelectOption? SelectedValueOption
    {
        get => FieldOptions?.FirstOrDefault(o => o.Value == Value);
        set
        {
            if (value != null) Value = value.Value;
        }
    }

    public FieldOption? CurrentFieldDef => EventFilterViewModel.FieldOptions.FirstOrDefault(f => f.Value == Field);
    public bool IsSelectField => CurrentFieldDef?.Type == "select" && !IsListOperator;
    public bool IsNumberField => CurrentFieldDef?.Type == "number" && !IsListOperator;
    public bool IsTextField => !IsSelectField && !IsNumberField && !IsListOperator;
    public bool IsListOperator => Operator is "$in" or "$nin";
    public bool IsNotListOperator => !IsListOperator;
    public SelectOption[]? FieldOptions => CurrentFieldDef?.Options;

    public string ValuePlaceholder => Operator == "$regex" ? "正则表达式"
        : IsListOperator ? "逗号分隔，如: 123, 456"
        : CurrentFieldDef?.Type == "number" ? "输入数字"
        : "输入值";

    public event Action? Modified;
    private void NotifyModified() => Modified?.Invoke();

    public FilterRuleViewModel(int id)
    {
        Id = id;
    }

    public FilterRuleViewModel(int id, string field, string op, string value) : this(id)
    {
        _field = field;
        _operator = op;
        _value = value;
    }
}

/// <summary>
/// 事件过滤器编辑器 ViewModel
/// </summary>
public class EventFilterViewModel : ReactiveObject
{
    private static int _nextRuleId = 1;

    #region 字段和操作符定义

    public static readonly FieldOption[] FieldOptions =
    {
        new("post_type", "事件类型", "select", new SelectOption[]
        {
            new("message", "消息"),
            new("message_sent", "自己发送的消息"),
            new("notice", "通知"),
            new("request", "请求"),
            new("meta_event", "元事件"),
        }),
        new("message_type", "消息类型", "select", new SelectOption[]
        {
            new("private", "私聊"),
            new("group", "群聊"),
        }),
        new("notice_type", "通知类型", "select", new SelectOption[]
        {
            new("group_upload", "群文件上传"),
            new("group_admin", "群管理员变动"),
            new("group_decrease", "群成员减少"),
            new("group_increase", "群成员增加"),
            new("group_ban", "群禁言"),
            new("group_recall", "群消息撤回"),
            new("friend_recall", "好友消息撤回"),
            new("notify", "群内提示"),
            new("group_card", "群名片变更"),
            new("essence", "精华消息"),
        }),
        new("request_type", "请求类型", "select", new SelectOption[]
        {
            new("friend", "好友请求"),
            new("group", "群请求"),
        }),
        new("group_id", "群号", "number"),
        new("user_id", "用户 QQ 号", "number"),
        new("sub_type", "子类型", "text"),
        new("raw_message", "消息内容", "text"),
    };

    public static readonly OperatorOption[] OperatorOptions =
    {
        new("$eq", "等于"),
        new("$ne", "不等于"),
        new("$in", "在列表中"),
        new("$nin", "不在列表中"),
        new("$regex", "正则匹配"),
        new("$gt", "大于"),
        new("$lt", "小于"),
    };

    // 给 AXAML ComboBox 绑定
    public FieldOption[] AvailableFields => FieldOptions;
    public OperatorOption[] AvailableOperators => OperatorOptions;

    #endregion

    public ObservableCollection<FilterRuleViewModel> Rules { get; } = new();

    private string _jsonText = "";
    public string JsonText
    {
        get => _jsonText;
        set
        {
            if (_jsonText == value) return;
            this.RaiseAndSetIfChanged(ref _jsonText, value);
            OnJsonTextChanged(value);
        }
    }

    private string _jsonError = "";
    public string JsonError
    {
        get => _jsonError;
        set => this.RaiseAndSetIfChanged(ref _jsonError, value);
    }

    public bool HasJsonError => !string.IsNullOrEmpty(JsonError);

    private string _ruleConflictWarning = "";
    public string RuleConflictWarning
    {
        get => _ruleConflictWarning;
        private set
        {
            if (_ruleConflictWarning == value) return;
            this.RaiseAndSetIfChanged(ref _ruleConflictWarning, value);
            this.RaisePropertyChanged(nameof(HasRuleConflict));
        }
    }

    public bool HasRuleConflict => !string.IsNullOrEmpty(_ruleConflictWarning);

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    private bool _isJsonMode;
    public bool IsJsonMode
    {
        get => _isJsonMode;
        set => this.RaiseAndSetIfChanged(ref _isJsonMode, value);
    }
    public bool IsVisualMode => !IsJsonMode;

    private bool _isVisualUnsupported;
    public bool IsVisualUnsupported
    {
        get => _isVisualUnsupported;
        set => this.RaiseAndSetIfChanged(ref _isVisualUnsupported, value);
    }

    public int RuleCount => Rules.Count;
    public bool HasFilter => Rules.Count > 0 || (!string.IsNullOrWhiteSpace(_jsonText) && _jsonText.Trim() != "{}");
    public bool HasMultipleRules => Rules.Count > 1;

    public ReactiveCommand<Unit, Unit> AddRuleCommand { get; }
    public ReactiveCommand<FilterRuleViewModel, Unit> RemoveRuleCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToVisualCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToJsonCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchExpandCommand { get; }

    public event Action? PropertyModified;

    private bool _isSyncing; // prevent recursive updates

    public EventFilterViewModel(JsonObject? filter = null)
    {
        AddRuleCommand = ReactiveCommand.Create(AddRule, outputScheduler: AvaloniaUiScheduler.Instance);
        RemoveRuleCommand = ReactiveCommand.Create<FilterRuleViewModel>(RemoveRule, outputScheduler: AvaloniaUiScheduler.Instance);
        SwitchExpandCommand = ReactiveCommand.Create(() =>
        {
            IsExpanded = !IsExpanded;
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        SwitchToVisualCommand = ReactiveCommand.Create(() =>
        {
            if (!IsVisualUnsupported) IsJsonMode = false;
            this.RaisePropertyChanged(nameof(IsVisualMode));
        }, outputScheduler: AvaloniaUiScheduler.Instance);
        SwitchToJsonCommand = ReactiveCommand.Create(() =>
        {
            if (!IsJsonMode)
            {
                // 同步最新 rules 到 JSON
                var f = RulesToFilter(Rules);
                _jsonText = f != null ? f.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "";
                this.RaisePropertyChanged(nameof(JsonText));
            }
            IsJsonMode = true;
            this.RaisePropertyChanged(nameof(IsVisualMode));
        }, outputScheduler: AvaloniaUiScheduler.Instance);

        LoadFromJsonObject(filter);
    }

    public void LoadFromJsonObject(JsonObject? filter)
    {
        _isSyncing = true;
        try
        {
            Rules.Clear();
            if (filter != null && filter.Count > 0)
            {
                _jsonText = filter.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                this.RaisePropertyChanged(nameof(JsonText));

                var parsed = ParseFilterToRules(filter);
                if (parsed != null)
                {
                    foreach (var rule in parsed)
                    {
                        rule.Modified += OnRuleModified;
                        Rules.Add(rule);
                    }
                    IsVisualUnsupported = false;
                }
                else
                {
                    IsVisualUnsupported = true;
                    IsJsonMode = true;
                    this.RaisePropertyChanged(nameof(IsVisualMode));
                }
            }
            else
            {
                _jsonText = "";
                this.RaisePropertyChanged(nameof(JsonText));
                IsVisualUnsupported = false;
            }
            RaiseFilterChanged();
        }
        finally
        {
            _isSyncing = false;
        }
    }

    public JsonObject? ToJsonObject()
    {
        if (IsJsonMode && !string.IsNullOrWhiteSpace(_jsonText))
        {
            try
            {
                var node = JsonNode.Parse(_jsonText.Trim());
                return node as JsonObject;
            }
            catch
            {
                return null;
            }
        }
        return RulesToFilter(Rules);
    }

    private void AddRule()
    {
        var rule = new FilterRuleViewModel(_nextRuleId++);
        rule.Modified += OnRuleModified;
        Rules.Add(rule);
        SyncRulesToJson();
        RaiseFilterChanged();
    }

    private void RemoveRule(FilterRuleViewModel rule)
    {
        rule.Modified -= OnRuleModified;
        Rules.Remove(rule);
        SyncRulesToJson();
        RaiseFilterChanged();
    }

    private void OnRuleModified()
    {
        if (_isSyncing) return;
        SyncRulesToJson();
        RaiseFilterChanged();
        PropertyModified?.Invoke();
    }

    private void SyncRulesToJson()
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            var f = RulesToFilter(Rules);
            _jsonText = f != null ? f.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "";
            this.RaisePropertyChanged(nameof(JsonText));
            JsonError = "";
            this.RaisePropertyChanged(nameof(HasJsonError));
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void OnJsonTextChanged(string text)
    {
        if (_isSyncing) return;
        // 可视化模式下 JsonText 只是 SyncRulesToJson 的产物: JSON 编辑框虽然隐藏, 绑定仍然活跃并会把
        // 推过来的值回写进这个 setter。此时若重建 Rules, 就会把同字段规则合并后的 JSON 灌回规则列表,
        // 表现为"改一条规则, 其他规则跟着变"
        if (!IsJsonMode) return;
        _isSyncing = true;
        try
        {
            var trimmed = text.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == "{}")
            {
                JsonError = "";
                Rules.Clear();
                IsVisualUnsupported = false;
                RaiseFilterChanged();
                PropertyModified?.Invoke();
                return;
            }
            try
            {
                var parsed = JsonNode.Parse(trimmed);
                if (parsed is JsonObject obj)
                {
                    JsonError = "";
                    var rules = ParseFilterToRules(obj);
                    if (rules != null)
                    {
                        Rules.Clear();
                        foreach (var rule in rules)
                        {
                            rule.Modified += OnRuleModified;
                            Rules.Add(rule);
                        }
                        IsVisualUnsupported = false;
                    }
                    else
                    {
                        IsVisualUnsupported = true;
                    }
                    RaiseFilterChanged();
                    PropertyModified?.Invoke();
                }
                else
                {
                    JsonError = "必须是 JSON 对象";
                }
            }
            catch (JsonException ex)
            {
                JsonError = ex.Message;
            }
        }
        finally
        {
            _isSyncing = false;
            this.RaisePropertyChanged(nameof(HasJsonError));
        }
    }

    private void RaiseFilterChanged()
    {
        this.RaisePropertyChanged(nameof(RuleCount));
        this.RaisePropertyChanged(nameof(HasFilter));
        this.RaisePropertyChanged(nameof(HasMultipleRules));
        UpdateRuleConflicts();
    }

    /// <summary>
    /// 同字段同操作符在 MongoDB 查询语法里没法出现两次, RulesToFilter 只能保留最后一条。
    /// 与其静默丢弃, 不如把被吞掉的条件点出来。
    /// </summary>
    private void UpdateRuleConflicts()
    {
        var duplicated = Rules
            .Where(r => !string.IsNullOrEmpty(r.Field))
            .GroupBy(r => (r.Field, r.Operator))
            .Where(g => g.Count() > 1)
            .Select(g => DescribeCondition(g.Key.Field, g.Key.Operator))
            .ToList();

        RuleConflictWarning = duplicated.Count == 0
            ? ""
            : $"以下条件重复了，只有最后一条会生效：{string.Join("、", duplicated)}。" +
              "同一字段要匹配多个值，请把操作符改成「在列表中」并用逗号分隔。";
    }

    private static string DescribeCondition(string field, string op)
    {
        var fieldLabel = FieldOptions.FirstOrDefault(f => f.Value == field)?.Label ?? field;
        var opLabel = OperatorOptions.FirstOrDefault(o => o.Value == op)?.Label ?? op;
        return $"{fieldLabel} {opLabel}";
    }

    #region Parse / Convert (port of WebUI logic)

    private static List<FilterRuleViewModel>? ParseFilterToRules(JsonObject filter)
    {
        var rules = new List<FilterRuleViewModel>();
        foreach (var (field, value) in filter)
        {
            if (field.StartsWith('$')) return null; // $and/$or 等复杂查询无法可视化

            if (value == null) continue;

            if (value is JsonObject condObj)
            {
                if (condObj.Count == 0) return null;

                // 一个字段可以带多个操作符 (RulesToFilter 合并同字段规则的产物), 展开成多条规则
                foreach (var (op, val) in condObj)
                {
                    if (!IsVisualizableOperator(op)) return null;
                    rules.Add(new FilterRuleViewModel(_nextRuleId++, field, op, StringifyValue(val)));
                }
            }
            else
            {
                // 简单等于
                rules.Add(new FilterRuleViewModel(_nextRuleId++, field, "$eq", StringifyValue(value)));
            }
        }
        return rules;
    }

    private static bool IsVisualizableOperator(string op)
        => OperatorOptions.Any(o => o.Value == op);

    private static string StringifyValue(JsonNode? node) => node switch
    {
        null => "",
        JsonArray arr => string.Join(", ", arr.Select(v => v?.ToString() ?? "")),
        _ => node.ToString(),
    };

    private static JsonObject? RulesToFilter(IEnumerable<FilterRuleViewModel> rules)
    {
        // 同字段的多条规则必须合并进同一个条件对象: 直接 filter[field] = ... 会让后一条静默覆盖前一条,
        // 而新增规则的默认字段都是 post_type, 很容易撞上
        var conds = new Dictionary<string, List<(string Op, JsonNode? Value)>>();
        var fieldOrder = new List<string>();

        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Field)) continue;
            if (!TryBuildCondValue(rule, out var condValue)) continue;

            if (!conds.TryGetValue(rule.Field, out var list))
            {
                list = new List<(string, JsonNode?)>();
                conds[rule.Field] = list;
                fieldOrder.Add(rule.Field);
            }

            // 同字段同操作符在 MongoDB 语法里无法表达两次, 保留最后一条
            list.RemoveAll(c => c.Op == rule.Operator);
            list.Add((rule.Operator, condValue));
        }

        var filter = new JsonObject();
        foreach (var field in fieldOrder)
        {
            var list = conds[field];
            if (list.Count == 0) continue;

            // 单条 $eq 写成裸值, 与 LLBot / WebUI 既有的配置格式保持一致
            if (list.Count == 1 && list[0].Op == "$eq")
                filter[field] = list[0].Value;
            else
                filter[field] = new JsonObject(list.Select(c => new KeyValuePair<string, JsonNode?>(c.Op, c.Value)));
        }

        return filter.Count > 0 ? filter : null;
    }

    private static bool TryBuildCondValue(FilterRuleViewModel rule, out JsonNode? value)
    {
        value = null;

        // 值还没填 (新建的规则, 或换字段时清掉的旧值) 就整条跳过: 写出 {"message_type":""} 或
        // {"group_id":{"$in":[]}} 这种条件永远匹配不上, 等于悄悄把整个连接的事件全过滤掉
        if (string.IsNullOrWhiteSpace(rule.Value)) return false;

        var isNumeric = FieldOptions.FirstOrDefault(f => f.Value == rule.Field)?.Type == "number";

        if (rule.Operator is "$in" or "$nin")
        {
            var arr = new JsonArray();
            foreach (var item in rule.Value.Split(',', '，').Select(v => v.Trim()).Where(v => v.Length > 0))
            {
                if (isNumeric)
                {
                    if (long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                        ((IList<JsonNode?>)arr).Add(JsonValue.Create(num));
                }
                else
                {
                    ((IList<JsonNode?>)arr).Add(JsonValue.Create(item));
                }
            }
            if (arr.Count == 0) return false;
            value = arr;
            return true;
        }

        if (isNumeric)
        {
            if (!long.TryParse(rule.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            value = n;
            return true;
        }

        value = rule.Value;
        return true;
    }

    #endregion
}
