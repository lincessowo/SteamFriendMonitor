using SteamFriendMonitor.Core;
using SteamKit2.Authentication;

namespace SteamFriendMonitor.Cli;

// 命令行版 Steam 登录认证交互：配置了 SharedSecret 自动生成令牌验证码，否则在控制台提示输入
sealed class CliAuthenticator : IAuthenticator {
	readonly string sharedSecret;

	public CliAuthenticator(string sharedSecret) => this.sharedSecret = sharedSecret;

	public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			ConsoleLog.Warn("上一次的 Steam 令牌验证码不正确，请重新输入");
		}

		if (!string.IsNullOrWhiteSpace(sharedSecret)) {
			string code = SteamGuardCodeGenerator.Generate(sharedSecret);
			ConsoleLog.Info($"已自动生成 Steam 令牌验证码: {code}");
			return Task.FromResult(code);
		}

		return Task.FromResult(ConsoleLog.Prompt("请输入手机 Steam 令牌验证码（5 位）: "));
	}

	public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			ConsoleLog.Warn("上一次的 SteamGuard 邮箱验证码不正确，请查收新邮件后重新输入");
		}

		return Task.FromResult(ConsoleLog.Prompt($"请输入发送到邮箱 {email} 的 SteamGuard 验证码: "));
	}

	public Task<bool> AcceptDeviceConfirmationAsync() {
		if (!string.IsNullOrWhiteSpace(sharedSecret)) {
			// 有 shared_secret 就走验证码校验，不等待手机确认
			return Task.FromResult(false);
		}

		ConsoleLog.Warn("Steam 需要在手机 Steam 应用上确认本次登录，请在手机上点击\"确认\"…");
		string input = ConsoleLog.Prompt("已在手机上确认？（y = 继续等待确认 / n = 改用验证码登录）: ");
		return Task.FromResult(input.Equals("y", StringComparison.OrdinalIgnoreCase));
	}
}
