using LuckyLilliaDesktop.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Services;

public interface IEmailService
{
    Task<EmailConfig> LoadConfigAsync();
    Task<bool> SaveConfigAsync(EmailConfig config);
    Task<bool> SendTestEmailAsync(EmailConfig config);
    Task<bool> SendDisconnectNotificationAsync(string uin, string nickname);
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly string _configPath;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine("bin", "llbot", "data", "email_config.json");
    }

    public async Task<EmailConfig> LoadConfigAsync()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogInformation("邮件配置文件不存在，返回默认配置");
                return new EmailConfig();
            }

            var json = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<EmailConfig>(json);
            return config ?? new EmailConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载邮件配置失败");
            return new EmailConfig();
        }
    }

    public async Task<bool> SaveConfigAsync(EmailConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var json = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(_configPath, json, Encoding.UTF8);

            _logger.LogInformation("邮件配置已保存到 {Path}", _configPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存邮件配置失败");
            return false;
        }
    }

    public async Task<bool> SendTestEmailAsync(EmailConfig config)
    {
        try
        {
            var subject = "LuckyLillia 邮件通知测试";
            var body = BuildTestEmailBody();
            return await SendEmailAsync(config, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送测试邮件失败");
            return false;
        }
    }

    public async Task<bool> SendDisconnectNotificationAsync(string uin, string nickname)
    {
        try
        {
            var config = await LoadConfigAsync();
            if (!config.Enabled)
            {
                _logger.LogDebug("邮件通知未启用，跳过发送");
                return false;
            }

            var subject = "LuckyLillia 掉线通知";
            var body = BuildDisconnectEmailBody(uin, nickname);
            return await SendEmailAsync(config, subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送掉线通知邮件失败");
            return false;
        }
    }

    private async Task<bool> SendEmailAsync(EmailConfig config, string subject, string body)
    {
        try
        {
            using var client = new SmtpClient(config.Smtp.Host, config.Smtp.Port)
            {
                EnableSsl = config.Smtp.Secure,
                Credentials = new NetworkCredential(config.Smtp.Auth.User, config.Smtp.Auth.Pass)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(config.From),
                Subject = subject,
                Body = body,
                IsBodyHtml = true,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8
            };

            message.To.Add(config.To);

            await client.SendMailAsync(message);
            _logger.LogInformation("邮件发送成功: {Subject}", subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送邮件失败");
            return false;
        }
    }

    private string BuildTestEmailBody()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 8px 8px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border-radius: 0 0 8px 8px; }}
        .info {{ background: white; padding: 15px; border-left: 4px solid #667eea; margin: 10px 0; }}
        .footer {{ text-align: center; margin-top: 20px; color: #666; font-size: 12px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>🎉 LuckyLillia 邮件通知测试</h2>
        </div>
        <div class=""content"">
            <p>您好！</p>
            <p>这是一封来自 <strong>LuckyLillia</strong> 的测试邮件。</p>
            <div class=""info"">
                <p><strong>📧 邮件配置测试成功</strong></p>
                <p>发送时间: {now}</p>
            </div>
            <p>如果您收到这封邮件，说明邮件通知功能已正常工作。</p>
            <p>当 QQ 掉线时，系统将自动向您发送通知邮件。</p>
        </div>
        <div class=""footer"">
            <p>此邮件由 LuckyLillia 自动发送，请勿回复</p>
        </div>
    </div>
</body>
</html>";
    }

    private string BuildDisconnectEmailBody(string uin, string nickname)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var displayName = string.IsNullOrEmpty(nickname) ? uin : $"{nickname} ({uin})";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%); color: white; padding: 20px; border-radius: 8px 8px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border-radius: 0 0 8px 8px; }}
        .alert {{ background: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 10px 0; }}
        .info {{ background: white; padding: 15px; margin: 10px 0; }}
        .footer {{ text-align: center; margin-top: 20px; color: #666; font-size: 12px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>⚠️ LuckyLillia 掉线通知</h2>
        </div>
        <div class=""content"">
            <div class=""alert"">
                <p><strong>⚠️ QQ 已掉线</strong></p>
            </div>
            <div class=""info"">
                <p><strong>账号信息:</strong> {displayName}</p>
                <p><strong>掉线时间:</strong> {now}</p>
            </div>
            <p>您的 QQ 机器人已掉线，请及时检查并重新登录。</p>
            <p>可能的原因：</p>
            <ul>
                <li>网络连接中断</li>
                <li>QQ 被强制下线</li>
                <li>程序异常退出</li>
            </ul>
        </div>
        <div class=""footer"">
            <p>此邮件由 LuckyLillia 自动发送，请勿回复</p>
        </div>
    </div>
</body>
</html>";
    }
}
