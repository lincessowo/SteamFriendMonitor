// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamFriendMonitor.Core;

// 本地保存 Steam 登录凭据：refresh token + SteamGuard 设备数据（GuardData），
// 让后续登录免密码验证，同时让 Steam 认得这台设备（避免设备指纹变化导致令牌被拒）
public static class TokenStore {
	sealed class StoredCredentials {
		[JsonPropertyName("RefreshToken")]
		public string? RefreshToken { get; set; }

		[JsonPropertyName("GuardData")]
		public string? GuardData { get; set; }
	}

	public static (string? RefreshToken, string? GuardData) Load(string username) {
		string path = FilePathFor(username);

		if (!File.Exists(path)) {
			return (null, null);
		}

		try {
			string content = File.ReadAllText(path).Trim();

			if (content.Length == 0) {
				return (null, null);
			}

			// 兼容旧版本：旧文件直接存 refresh token 明文
			if (!content.StartsWith('{')) {
				return (content, null);
			}

			StoredCredentials? stored = JsonSerializer.Deserialize<StoredCredentials>(content);
			return (string.IsNullOrEmpty(stored?.RefreshToken) ? null : stored!.RefreshToken, stored?.GuardData);
		} catch (Exception) {
			return (null, null);
		}
	}

	// 返回 null 表示保存成功，否则返回错误信息由调用方记录
	public static string? Save(string username, string refreshToken, string? guardData = null) {
		try {
			var stored = new StoredCredentials {
				RefreshToken = refreshToken,
				GuardData = guardData
			};

			File.WriteAllText(FilePathFor(username), JsonSerializer.Serialize(stored), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return null;
		} catch (Exception e) {
			return e.Message;
		}
	}

	public static void Clear(string username) {
		string path = FilePathFor(username);

		try {
			if (File.Exists(path)) {
				File.Delete(path);
			}
		} catch (IOException) {
			// 删除失败无碍，下次登录会覆盖
		}
	}

	static string FilePathFor(string username) {
		string safeName = string.Concat(username.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
		return Path.Combine(AppContext.BaseDirectory, $"session.{safeName}.token");
	}
}
