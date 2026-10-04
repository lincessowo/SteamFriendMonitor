using System.Text;
using SteamFriendMonitor.Core;

namespace SteamFriendMonitor.Cli;

// 纯命令行版本：跨平台（Windows / Linux / macOS），与 GUI 共享同一套核心逻辑与 config.json 结构
internal static class Program {
	static async Task<int> Main() {
		try {
			Console.OutputEncoding = Encoding.UTF8;
		} catch (Exception) {
			// 输出被重定向时可能失败，忽略
		}

		Console.WriteLine();
		Console.WriteLine("==============================================");
		Console.WriteLine("   Steam 好友状态监控 CLI (SteamFriendMonitor)");
		Console.WriteLine("   隐身登录 · 实时推送 · SQLite · WebSocket");
		Console.WriteLine("==============================================");
		Console.WriteLine();

		string baseDir = AppContext.BaseDirectory;
		string configPath = Path.Combine(baseDir, "config.json");

		if (!File.Exists(configPath)) {
			AppConfig.SaveTemplate(configPath);
			ConsoleLog.Error($"未找到配置文件，已生成模板: {configPath}");
			ConsoleLog.Error("请填写 Username / Password 后重新运行（可选填 SharedSecret 实现令牌自动验证）");
			return 1;
		}

		AppConfig config;

		try {
			config = AppConfig.Load(configPath);
		} catch (Exception e) {
			ConsoleLog.Error($"配置文件解析失败: {e.Message}");
			return 1;
		}

		string username = config.Username;

		if (string.IsNullOrWhiteSpace(username)) {
			username = ConsoleLog.Prompt("请输入 Steam 用户名: ");
		} else {
			ConsoleLog.Info($"Steam 用户: {username}");
		}

		string password = config.Password;

		if (string.IsNullOrWhiteSpace(password)) {
			password = ConsoleLog.ReadPassword("请输入 Steam 密码: ");
		}

		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) {
			ConsoleLog.Error("用户名或密码为空，无法登录（首次登录需要密码，之后可只保留本地令牌）");
			return 1;
		}

		string logDirectory = Path.Combine(baseDir, string.IsNullOrWhiteSpace(config.LogDirectory) ? "logs" : config.LogDirectory);
		string databasePath = Path.Combine(baseDir, string.IsNullOrWhiteSpace(config.DatabasePath) ? "friends.db" : config.DatabasePath);
		var fileLog = new DailyFileLogger(logDirectory);
		var database = new SqliteEventStore(databasePath);
		database.Initialize();

		ConsoleLog.Info($"日志目录: {logDirectory}（按天生成 yyyyMMdd.txt）");
		ConsoleLog.Info($"数据库: {databasePath}");
		ConsoleLog.Info("按 Ctrl+C 退出");

		// WebSocket 接口（默认关闭）
		WsServer? wsServer = null;
		FriendSession? session = null;

		if (config.WsEnabled) {
			bool requireToken = !config.WsLoopbackOnly;
			string host = string.IsNullOrWhiteSpace(config.WsHost) ? "127.0.0.1" : config.WsHost.Trim();

			if (!config.WsLoopbackOnly) {
				ConfirmNonLoopback(host, config.WsToken);
			}

			wsServer = new WsServer(host, config.WsPort, requireToken, config.WsToken, () => session!.GetCurrentStates(), () => session!.SelfSteamId);
		}

		session = new FriendSession(config, fileLog, database, new CliAuthenticator(config.SharedSecret));

		if (wsServer != null) {
			wsServer.SystemLogged += entry => PrintLog(entry.Level, entry.Message);

			try {
				wsServer.Start();
			} catch (Exception e) {
				ConsoleLog.Error($"WebSocket 服务启动失败: {e.Message}");
				wsServer = null;
			}
		}

		session.StatusChanged += status => {
			ConsoleLog.Info($"[状态] {status}");
			wsServer?.PushStatus(status);
		};
		session.SystemLogged += entry => PrintLog(entry.Level, entry.Message);
		session.FriendEventRecorded += record => {
			ConsoleLog.Record($"（{record.Time:HH:mm:ss}） {record.Name} {record.SteamId} {record.Status}");
			wsServer?.PushEvent(record);
		};
		session.FriendCountChanged += count => ConsoleLog.Info($"当前好友 {count} 位");
		session.SessionStopped += () => ConsoleLog.Error("会话已停止（见上方错误信息，处理后重新启动）");

		using var cts = new CancellationTokenSource();
		Console.CancelKeyPress += (_, eventArgs) => {
			eventArgs.Cancel = true;
			cts.Cancel();
		};

		try {
			await session.RunAsync(cts.Token).ConfigureAwait(false);
		} catch (Exception e) {
			ConsoleLog.Error($"监控线程异常退出: {e.Message}");
		}

		wsServer?.Stop();
		database.Dispose();
		ConsoleLog.Info("已退出");
		return 0;
	}

	// 非 127.0.0.1 绑定：明显提示 + 10 秒倒计时（Ctrl+C 可中止整个进程）
	static void ConfirmNonLoopback(string host, string token) {
		ConsoleLog.Error($"警告：WebSocket 接口将绑定到非 127.0.0.1 地址（{host}），网络内的其他程序都能访问好友数据！");
		ConsoleLog.Warn($"客户端连接需携带令牌: ?token={token}");
		ConsoleLog.Warn("10 秒后自动继续，按 Ctrl+C 取消");

		for (int i = 10; i >= 1; i--) {
			Console.Write($"{i} ");
			Thread.Sleep(1000);
		}

		Console.WriteLine();
	}

	static void PrintLog(LogLevel level, string message) {
		string prefix = level switch {
			LogLevel.Warning => "[警告] ",
			LogLevel.Error => "[错误] ",
			_ => string.Empty
		};

		if (level == LogLevel.Error) {
			ConsoleLog.Error($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}");
		} else if (level == LogLevel.Warning) {
			ConsoleLog.Warn($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}");
		} else {
			ConsoleLog.Info($"[{DateTime.Now:HH:mm:ss}] {message}");
		}
	}
}
