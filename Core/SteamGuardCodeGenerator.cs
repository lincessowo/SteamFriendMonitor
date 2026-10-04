using System.Security.Cryptography;

namespace SteamFriendMonitor.Core;

// 标准 Steam 令牌（SteamGuard TOTP）算法，与手机令牌器/.maFile 的 shared_secret 配套
public static class SteamGuardCodeGenerator {
	const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";
	const int PeriodSeconds = 30;
	const int CodeLength = 5;

	public static string Generate(string sharedSecretBase64, DateTimeOffset? timestamp = null) {
		long timeStep = ((timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()) / PeriodSeconds;
		byte[] secret = Convert.FromBase64String(sharedSecretBase64);

		byte[] timeBuffer = BitConverter.GetBytes(timeStep);
		if (BitConverter.IsLittleEndian) {
			Array.Reverse(timeBuffer);
		}

		using var hmac = new HMACSHA1(secret);
		byte[] hash = hmac.ComputeHash(timeBuffer);

		int start = hash[^1] & 0x0F;
		int value = ((hash[start] & 0x7F) << 24) | ((hash[start + 1] & 0xFF) << 16) | ((hash[start + 2] & 0xFF) << 8) | (hash[start + 3] & 0xFF);

		Span<char> code = stackalloc char[CodeLength];

		for (int i = 0; i < code.Length; i++) {
			code[i] = Alphabet[value % Alphabet.Length];
			value /= Alphabet.Length;
		}

		return new string(code);
	}
}
