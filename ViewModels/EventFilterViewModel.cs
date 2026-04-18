using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            this.RaiseAndSetIfChanged(ref _field, value);
            this.RaisePropertyChanged(nameof(CurrentFieldDef));
            this.RaisePropertyChanged(nameof(IsSelectField));
            this.RaisePropertyChanged(nameof(IsNumberField));
            this.RaisePropertyChanged(nameof(IsTextField));
            this.RaisePropertyChanged(nameof(FieldOptions));
            NotifyModified();
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
            this.RaisePropertyChanged(nameof(IsListOperator));
            this.RaisePropertyChanged(nameof(IsNotListOperator));
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
        set { this.RaiseAndSetIfChanged(ref _value, value ?? ""); NotifyModified(); }
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
        AddRuleCommand = ReactiveCommand.Create(AddRule);
        RemoveRuleCommand = ReactiveCommand.Create<FilterRuleViewModel>(RemoveRule);
        SwitchExpandCommand = ReactiveCommand.Create(() =>
        {
            IsExpanded = !IsExpanded;
        });
        SwitchToVisualCommand = ReactiveCommand.Create(() =>
        {
            if (!IsVisualUnsupported) IsJsonMode = false;
            this.RaisePropertyChanged(nameof(IsVisualMode));
        });
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
        });

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
                if (condObj.Count != 1) return null;
                var (op, val) = condObj.First();
                if (!op.StartsWith('$')) return null;

                string valStr;
                if (val is JsonArray arr)
                    valStr = string.Join(", ", arr.Select(v => v?.ToString() ?? ""));
                else
                    valStr = val?.ToString() ?? "";

                rules.Add(new FilterRuleViewModel(_nextRuleId++, field, op, valStr));
            }
            else
            {
                // 简单等于
                string valStr;
                if (value is JsonArray arr)
                    valStr = string.Join(", ", arr.Select(v => v?.ToString() ?? ""));
                else
                    valStr = value.ToString();

                rules.Add(new FilterRuleViewModel(_nextRuleId++, field, "$eq", valStr));
            }
        }
        return rules;
    }

    private static JsonObject? RulesToFilter(IEnumerable<FilterRuleViewModel> rules)
    {
        var ruleList = rules.ToList();
        if (ruleList.Count == 0) return null;

        var filter = new JsonObject();
        foreach (var rule in ruleList)
        {
            if (string.IsNullOrEmpty(rule.Field)) continue;
            var fieldDef = FieldOptions.FirstOrDefault(f => f.Value == rule.Field);
            var isNumeric = fieldDef?.Type == "number";

            if (rule.Operator is "$in" or "$nin")
            {
                var items = rule.Value.Split(',', '，')
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();

                var arr = new JsonArray();
                foreach (var item in items)
                {
                    if (isNumeric && long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                        arr.Add(num);
                    else if (!isNumeric)
                        arr.Add(item);
                }
                filter[rule.Field] = new JsonObject { [rule.Operator] = arr };
            }
            else if (rule.Operator == "$eq")
            {
                if (isNumeric)
                {
                    if (string.IsNullOrWhiteSpace(rule.Value)) continue;
                    if (long.TryParse(rule.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                        filter[rule.Field] = num;
                }
                else
                {
                    filter[rule.Field] = rule.Value;
                }
            }
            else // $ne, $regex, $gt, $lt
            {
                JsonNode? parsedValue;
                if (isNumeric)
                {
                    if (string.IsNullOrWhiteSpace(rule.Value)) continue;
                    if (!long.TryParse(rule.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)) continue;
                    parsedValue = num;
                }
                else
                {
                    parsedValue = rule.Value;
                }
                filter[rule.Field] = new JsonObject { [rule.Operator] = parsedValue };
            }
        }

        return filter.Count > 0 ? filter : null;
    }

    #endregion
}
