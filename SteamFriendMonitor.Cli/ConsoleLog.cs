// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace SteamFriendMonitor.Cli;

internal static class ConsoleLog {
	static readonly object Gate = new();

	public static void Info(string message) => Write(ConsoleColor.Gray, message);
	public static void Warn(string message) => Write(ConsoleColor.Yellow, message);
	public static void Error(string message) => Write(ConsoleColor.Red, message);

	// 与日志文件内容一致的原始记录行
	public static void Record(string line) => Write(ConsoleColor.Cyan, line);

	public static string Prompt(string message) {
		lock (Gate) {
			Console.ResetColor();
			Console.Write(message);
			return Console.ReadLine()?.Trim() ?? string.Empty;
		}
	}

	// 隐藏输入的密码读取；输入被重定向（脚本/管道）时退化为普通读取
	public static string ReadPassword(string message) {
		if (Console.IsInputRedirected) {
			return Prompt(message);
		}

		lock (Gate) {
			Console.ResetColor();
			Console.Write(message);

			var buffer = new StringBuilder();

			while (true) {
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);

				switch (key.Key) {
					case ConsoleKey.Enter:
						Console.WriteLine();
						return buffer.ToString();
					case ConsoleKey.Backspace when buffer.Length > 0:
						buffer.Length--;
						Console.Write("\b \b");
						break;
					default:
						if (!char.IsControl(key.KeyChar)) {
							buffer.Append(key.KeyChar);
							Console.Write('*');
						}

						break;
				}
			}
		}
	}

	static void Write(ConsoleColor color, string message) {
		lock (Gate) {
			Console.ForegroundColor = color;
			Console.WriteLine(message);
			Console.ResetColor();
		}
	}
}
