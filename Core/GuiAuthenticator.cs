using System.ComponentModel;
using SteamFriendMonitor.UI;
using SteamKit2.Authentication;

namespace SteamFriendMonitor.Core;

// GUI 版 Steam 登录认证交互：需要验证码时在界面上弹出对话框收集
// 配置了 SharedSecret 时自动生成令牌验证码，不打扰用户
sealed class GuiAuthenticator : IAuthenticator {
	readonly string sharedSecret;
	readonly ISynchronizeInvoke ui;

	public GuiAuthenticator(string sharedSecret, ISynchronizeInvoke ui) {
		this.sharedSecret = sharedSecret;
		this.ui = ui;
	}

	public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
		if (!string.IsNullOrWhiteSpace(sharedSecret)) {
			return Task.FromResult(SteamGuardCodeGenerator.Generate(sharedSecret));
		}

		return Task.FromResult(Prompt("请输入手机 Steam 令牌验证码（5 位）", previousCodeWasIncorrect ? "上一次验证码不正确，请重新输入" : null));
	}

	public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
		return Task.FromResult(Prompt($"请输入发送到 {email} 的 SteamGuard 验证码", previousCodeWasIncorrect ? "上一次验证码不正确，请查收新邮件后重新输入" : null));
	}

	public Task<bool> AcceptDeviceConfirmationAsync() {
		if (!string.IsNullOrWhiteSpace(sharedSecret)) {
			// 有 shared_secret 就走验证码校验，不等待手机确认
			return Task.FromResult(false);
		}

		bool confirmed = (bool)ui.Invoke(new Func<bool>(() =>
			MessageBox.Show(
				"Steam 需要在手机 Steam 应用上确认本次登录。\n\n点「是」：已在手机上确认（继续等待）\n点「否」：改用验证码登录",
				"Steam 设备确认",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Question) == DialogResult.Yes), null)!;

		return Task.FromResult(confirmed);
	}

	string Prompt(string label, string? hint) =>
		((string?)ui.Invoke(new Func<string>(() => InputDialog.Show("Steam 登录验证", label, hint)), null)) ?? string.Empty;
}
