// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Win32;

namespace SteamFriendMonitor.Core;

// 开机自启动：写入当前用户注册表 Run 键
public static class AutoStart {
	const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string ValueName = "SteamFriendMonitor";

	public static bool IsEnabled() {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
			return key?.GetValue(ValueName) != null;
		} catch (Exception) {
			return false;
		}
	}

	// 返回 null 表示成功，否则返回错误信息
	public static string? SetEnabled(bool enabled) {
		try {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

			if (enabled) {
				key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
			} else {
				key.DeleteValue(ValueName, throwOnMissingValue: false);
			}

			return null;
		} catch (Exception e) {
			return e.Message;
		}
	}
}
