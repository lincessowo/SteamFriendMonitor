using System.Text;

namespace SteamFriendMonitor.Core;

// 按天分文件的追加日志：文件名为本地日期 yyyyMMdd.txt，UTF-8 编码（带 BOM，方便记事本/Excel 直接打开）
public sealed class DailyFileLogger {
	readonly string directory;
	readonly object gate = new();

	public DailyFileLogger(string directory) {
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		this.directory = directory;
		Directory.CreateDirectory(directory);
	}

	public void Write(string line) {
		lock (gate) {
			string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd}.txt");
			File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		}
	}
}
