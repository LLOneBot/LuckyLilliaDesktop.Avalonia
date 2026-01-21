# UI 主题规范

## 主题适配要求

所有 UI 的新建或更改都必须考虑主题（深色/浅色）适配。

### 禁止硬编码颜色

❌ **错误示例**：
```xml
<Border Background="#2D2D2D">
    <TextBlock Foreground="#E0E0E0" Text="示例"/>
</Border>
```

✅ **正确示例**：
```xml
<Border Background="{DynamicResource SystemControlBackgroundAltHighBrush}">
    <TextBlock Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}" Text="示例"/>
</Border>
```

### 常用主题资源

#### 背景色
- `SystemControlBackgroundAltHighBrush` - 主要背景（卡片、对话框）
- `SystemControlBackgroundBaseLowBrush` - 次要背景
- `CardBackground` - 自定义卡片背景

#### 前景色（文字）
- `SystemControlForegroundBaseHighBrush` - 主要文字
- `SystemControlForegroundBaseMediumBrush` - 次要文字
- `TextPrimary` - 自定义主要文字
- `TextSecondary` - 自定义次要文字

#### 边框
- `SystemControlForegroundBaseLowBrush` - 边框颜色
- `BorderColor` - 自定义边框颜色

#### 强调色
- `PrimaryBrush` - 主要按钮、强调元素
- `DangerColor` - 危险操作按钮

### 检查清单

创建或修改 UI 时，确保：
- [ ] 所有背景色使用动态资源
- [ ] 所有文字颜色使用动态资源
- [ ] 所有边框颜色使用动态资源
- [ ] 在深色和浅色主题下都测试过
- [ ] 对话框、弹窗也要适配主题

### 特殊情况

某些颜色可以硬编码（需要明确理由）：
- 品牌色（如主按钮的 `#6C7BFF`）
- 二维码背景（必须是白色）
- 特定功能需要的固定颜色

但即使是这些情况，也要考虑在不同主题下的对比度和可读性。
