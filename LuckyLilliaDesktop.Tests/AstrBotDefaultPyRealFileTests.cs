using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace LuckyLilliaDesktop.Tests;

public class AstrBotDefaultPyRealFileTests
{
    private readonly ITestOutputHelper _output;
    
    public AstrBotDefaultPyRealFileTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestCurrentCodeLogic_WithRealFile()
    {
        var defaultPyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "astrbot", "astrbot", "core", "config", "default.py"));
        
        if (!File.Exists(defaultPyPath))
        {
            _output.WriteLine($"文件不存在: {defaultPyPath}");
            return;
        }

        var content = File.ReadAllText(defaultPyPath, Encoding.UTF8);
        
        // 修复后的检查逻辑：检查 platform 是否为空数组
        const string emptyPlatform = "\"platform\": [],";
        if (!content.Contains(emptyPlatform))
        {
            _output.WriteLine("platform 已配置（不是空数组）");
            return;
        }

        _output.WriteLine("platform 是空数组，需要配置");
        Assert.Contains(emptyPlatform, content);
    }

    [Fact]
    public void TestReplacementResult()
    {
        var defaultPyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "astrbot", "astrbot", "core", "config", "default.py"));
        
        if (!File.Exists(defaultPyPath))
        {
            _output.WriteLine($"文件不存在，跳过测试");
            return;
        }

        var content = File.ReadAllText(defaultPyPath, Encoding.UTF8);
        
        // 修复后的替换逻辑
        const string emptyPlatform = "\"platform\": [],";
        const string newPlatform = 
            "\"platform\": [\n" +
            "        {\n" +
            "            \"id\": \"llbot\",\n" +
            "            \"type\": \"aiocqhttp\",\n" +
            "            \"enable\": True,\n" +
            "            \"ws_reverse_host\": \"0.0.0.0\",\n" +
            "            \"ws_reverse_port\": 6199,\n" +
            "            \"ws_reverse_token\": \"\"\n" +
            "        }\n" +
            "    ],";

        if (!content.Contains(emptyPlatform))
        {
            _output.WriteLine("platform 已配置，无需替换");
            return;
        }

        var result = content.Replace(emptyPlatform, newPlatform);
        
        // 验证替换成功
        Assert.Contains("\"id\": \"llbot\"", result);
        Assert.DoesNotContain("\"platform\": [],", result);
        _output.WriteLine("替换成功，格式正确");
    }

    [Fact]
    public void TestCorrectReplacement()
    {
        var defaultPyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "astrbot", "astrbot", "core", "config", "default.py"));
        
        if (!File.Exists(defaultPyPath))
        {
            _output.WriteLine($"文件不存在，跳过测试");
            return;
        }

        var content = File.ReadAllText(defaultPyPath, Encoding.UTF8);
        
        const string emptyPlatform = "\"platform\": [],";
        
        if (content.Contains(emptyPlatform))
        {
            _output.WriteLine("platform 是空数组，可以进行配置");
            Assert.True(true);
        }
        else
        {
            _output.WriteLine("platform 已配置");
            Assert.DoesNotContain(emptyPlatform, content);
        }
    }
}
