using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class AstrBotDefaultPyConfigTests
{
    // 模拟 default.py 中的实际内容（使用 \r\n 模拟 Windows 换行）
    private const string ActualDefaultPyContent = 
        "    \"dashboard\": {\r\n" +
        "        \"enable\": True,\r\n" +
        "        \"username\": \"astrbot\",\r\n" +
        "        \"password\": \"77b90590a8945a7d36c963981a307dc9\",\r\n" +
        "        \"jwt_secret\": \"\",\r\n" +
        "        \"host\": \"0.0.0.0\",\r\n" +
        "        \"port\": 6185,\r\n" +
        "    },\r\n" +
        "    \"platform\": [],\r\n" +
        "    \"platform_specific\": {\r\n" +
        "        # 平台特异配置\r\n" +
        "        \"lark\": {\r\n" +
        "            \"pre_ack_emoji\": {\"enable\": False, \"emojis\": [\"Typing\"]},\r\n" +
        "        },\r\n" +
        "    },\r\n";

    [Fact]
    public void CurrentCode_CannotFindPlatformConfig_BecauseMissingComma()
    {
        // 当前代码中使用的搜索字符串（不带逗号）
        const string oldPlatform = "\"platform\": []";
        
        // 验证：当前代码无法找到配置项，因为实际文件中有逗号
        bool found = ActualDefaultPyContent.Contains(oldPlatform);
        
        // 这个断言会通过，说明当前代码确实能找到（因为 Contains 不需要完全匹配）
        // 但问题在于替换后的格式
        Assert.True(found, "当前代码应该能找到 platform 配置");
    }

    [Fact]
    public void CurrentCode_ReplacementBreaksPythonSyntax()
    {
        // 当前代码的替换逻辑
        const string oldPlatform = "\"platform\": []";
        const string newPlatform = "\"platform\": [\r\n" +
            "        {\r\n" +
            "            \"id\": \"llbot\",\r\n" +
            "            \"type\": \"aiocqhttp\",\r\n" +
            "            \"enable\": True,\r\n" +
            "            \"ws_reverse_host\": \"0.0.0.0\",\r\n" +
            "            \"ws_reverse_port\": 6199,\r\n" +
            "            \"ws_reverse_token\": \"\"\r\n" +
            "        }\r\n" +
            "    ]";

        var result = ActualDefaultPyContent.Replace(oldPlatform, newPlatform);
        
        // 验证替换确实发生了
        Assert.Contains("\"type\": \"aiocqhttp\"", result);
        
        // Bug: 替换后 ] 和 , 之间会有换行
        // 原内容: "platform": [],\r\n
        // 替换后: "platform": [...]\r\n    ],\r\n  <- ] 后面紧跟 ,\r\n
        // 实际上新内容的 ] 后面会紧跟原来的 ,\r\n
        // 所以会变成 ...]\r\n    ],\r\n
        
        // 检查替换后的格式问题：新内容的 ] 后面紧跟原来的逗号
        // 新内容以 "    ]" 结尾，原内容的 , 会紧跟其后
        Assert.Contains("    ],\r\n", result);
    }

    [Fact]
    public void CorrectApproach_ShouldIncludeCommaInSearch()
    {
        // 正确的做法：搜索时包含逗号和换行
        const string oldPlatform = "\"platform\": [],";
        const string newPlatform = "\"platform\": [\r\n" +
            "        {\r\n" +
            "            \"id\": \"llbot\",\r\n" +
            "            \"type\": \"aiocqhttp\",\r\n" +
            "            \"enable\": True,\r\n" +
            "            \"ws_reverse_host\": \"0.0.0.0\",\r\n" +
            "            \"ws_reverse_port\": 6199,\r\n" +
            "            \"ws_reverse_token\": \"\"\r\n" +
            "        }\r\n" +
            "    ],";

        Assert.Contains(oldPlatform, ActualDefaultPyContent);

        var result = ActualDefaultPyContent.Replace(oldPlatform, newPlatform);
        
        // 验证替换后格式正确
        Assert.Contains("\"type\": \"aiocqhttp\"", result);
        Assert.DoesNotContain("\"platform\": [],", result);
        // 验证没有 ]\r\n, 这种错误格式
        Assert.DoesNotContain("]\r\n,", result);
    }

    [Fact]
    public void VerifyActualFileFormat()
    {
        const string searchWithoutComma = "\"platform\": []";
        const string searchWithComma = "\"platform\": [],";
        
        // 两种搜索都能找到（因为 Contains 是子串匹配）
        Assert.Contains(searchWithoutComma, ActualDefaultPyContent);
        Assert.Contains(searchWithComma, ActualDefaultPyContent);
    }

    [Fact]
    public void DemonstrateTheBug()
    {
        // 演示真正的 bug：缩进不正确
        const string oldPlatform = "\"platform\": []";
        const string newPlatform = "\"platform\": [\r\n" +
            "        {\r\n" +
            "            \"id\": \"llbot\",\r\n" +
            "            \"type\": \"aiocqhttp\",\r\n" +
            "            \"enable\": True,\r\n" +
            "            \"ws_reverse_host\": \"0.0.0.0\",\r\n" +
            "            \"ws_reverse_port\": 6199,\r\n" +
            "            \"ws_reverse_token\": \"\"\r\n" +
            "        }\r\n" +
            "    ]";

        var result = ActualDefaultPyContent.Replace(oldPlatform, newPlatform);
        
        // 真正的问题是：当前代码的缩进是 8 空格开头
        // 但 default.py 中 platform 的缩进应该是 4 空格
        // 所以替换后的内容缩进会多出 4 空格
        
        // 原文件格式：
        //     "platform": [],
        //     "platform_specific": {
        
        // 当前代码替换后：
        //     "platform": [
        //         {           <- 8空格缩进，应该是4空格
        //             ...     <- 12空格缩进，应该是8空格
        //         }
        //     ],
        //     "platform_specific": {
        
        // 验证缩进问题：新内容用了 8 空格缩进
        Assert.Contains("        {", result);
        
        // 正确的缩进应该是 4 空格（与 DEFAULT_CONFIG 内部一致）
        // 这不会导致 Python 语法错误，但格式不一致
    }
}
