// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using SteamFriendMonitor.Core;

namespace SteamFriendMonitor.UI;

internal sealed class MainForm : Form {
	readonly string configPath;
	readonly string configDir;
	AppConfig config;

	ListView lvEvents = null!;
	TextBox txtLog = null!;
	Button btnStart = null!;
	Button btnStop = null!;
	Button btnSettings = null!;
	Button btnStats = null!;
	Button btnOpenLogs = null!;
	Button btnOpenDb = null!;
	Label lblState = null!;
	ToolStripStatusLabel lblFriends = null!;
	NotifyIcon trayIcon = null!;
	ToolStripMenuItem menuStart = null!;
	ToolStripMenuItem menuStop = null!;

	FriendSession? session;
	CancellationTokenSource? cts;
	DailyFileLogger? fileLog;
	SqliteEventStore? database;
	WsServer? wsServer;
	bool realExit;

	public MainForm() {
		// 配置固定放在 exe 旁边，避免“从不同位置启动时读到不同配置”导致设置丢失
		configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
		configDir = AppContext.BaseDirectory;

		// 兼容旧版本：旧版会按启动位置读写配置（如 run.bat 会把配置存到项目根目录）。
		// 若 exe 旁还没有配置、或从未填过账号，就把工作目录下的旧配置迁移过来
		try {
			string legacy = Path.Combine(Environment.CurrentDirectory, "config.json");
			bool needMigrate = !File.Exists(configPath) || string.IsNullOrWhiteSpace(AppConfig.Load(configPath).Username);

			if (needMigrate && !string.Equals(legacy, configPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy)) {
				AppConfig legacyConfig = AppConfig.Load(legacy);

				if (!string.IsNullOrWhiteSpace(legacyConfig.Username)) {
					legacyConfig.Save(configPath);
				}
			}
		} catch (Exception) {
			// 迁移失败不影响正常流程
		}

		if (!File.Exists(configPath)) {
			AppConfig.SaveTemplate(configPath);
		}

		try {
			config = AppConfig.Load(configPath);
		} catch (Exception) {
			config = new AppConfig();
		}

		InitializeUi();
		UpdateCommandState();
		AppendLog(LogLevel.Info, "就绪。请先在「设置」中确认账号信息，然后点击「启动监控」。");
	}

	Icon CreateAppIcon() {
		// 运行时绘制：绿色圆底 + 白色“S”，同时用于窗口标题栏和托盘
		using var bmp = new Bitmap(32, 32);

		using (var g = Graphics.FromImage(bmp)) {
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(Color.Transparent);

			using var circle = new SolidBrush(Color.FromArgb(0, 148, 60));
			g.FillEllipse(circle, 1, 1, 30, 30);

			using var font = new Font("Segoe UI", 17F, FontStyle.Bold);
			SizeF size = g.MeasureString("S", font);
			g.DrawString("S", font, Brushes.White, 16 - size.Width / 2, 16 - size.Height / 2);
		}

		return Icon.FromHandle(bmp.GetHicon());
	}

	void InitializeUi() {
		Text = "Steam 好友状态监控";
		Icon = CreateAppIcon();
		Size = new Size(980, 660);
		MinimumSize = new Size(720, 480);
		StartPosition = FormStartPosition.CenterScreen;
		Font = new Font("Microsoft YaHei UI", 9F);

		// 托盘图标与菜单
		trayIcon = new NotifyIcon {
			Icon = Icon,
			Text = "Steam 好友状态监控",
			Visible = true
		};

		var menu = new ContextMenuStrip();
		menu.Items.Add(new ToolStripMenuItem("打开主界面", null, (_, _) => RestoreFromTray()));
		menu.Items.Add(new ToolStripSeparator());
		menuStart = new ToolStripMenuItem("启动监控", null, (_, _) => StartSession());
		menuStop = new ToolStripMenuItem("停止监控", null, (_, _) => StopSession());
		menu.Items.AddRange([menuStart, menuStop, new ToolStripSeparator()]);
		menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => {
			realExit = true;
			Close();
		}));
		trayIcon.ContextMenuStrip = menu;
		trayIcon.DoubleClick += (_, _) => RestoreFromTray();

		// 顶部工具栏
		var top = new FlowLayoutPanel {
			Dock = DockStyle.Top,
			Height = 48,
			Padding = new Padding(10, 10, 0, 0)
		};

		btnStart = new Button { Text = "启动监控", Width = 92 };
		btnStop = new Button { Text = "停止", Width = 70, Enabled = false };
		btnStats = new Button { Text = "统计…", Width = 80 };
		btnSettings = new Button { Text = "设置…", Width = 80 };
		btnOpenLogs = new Button { Text = "打开日志目录", Width = 110 };
		btnOpenDb = new Button { Text = "打开数据目录", Width = 110 };
		lblState = new Label {
			Text = "● 未连接",
			AutoSize = true,
			Margin = new Padding(18, 10, 0, 0),
			ForeColor = Color.Gray,
			Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
		};

		top.Controls.AddRange([btnStart, btnStop, btnStats, btnSettings, btnOpenLogs, btnOpenDb, lblState]);

		// 好友动态列表
		lvEvents = new ListView {
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			MultiSelect = false,
			HideSelection = true
		};
		typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lvEvents, true);
		lvEvents.Columns.Add("时间", 90);
		lvEvents.Columns.Add("好友名称", 200);
		lvEvents.Columns.Add("SteamID", 150);
		lvEvents.Columns.Add("状态", 230);
		lvEvents.Columns.Add("游戏详情（富状态）", 230);

		// 底部系统日志
		txtLog = new TextBox {
			Dock = DockStyle.Bottom,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Vertical,
			Height = 140,
			BackColor = Color.FromArgb(28, 28, 30),
			ForeColor = Color.FromArgb(190, 190, 190),
			Font = new Font("Consolas", 9F)
		};

		// 状态栏
		var status = new StatusStrip();
		lblFriends = new ToolStripStatusLabel { Text = "好友: 0" };
		var lblDb = new ToolStripStatusLabel { Text = $"数据库: {config.DatabasePath}", Spring = true, TextAlign = ContentAlignment.MiddleRight };
		status.Items.AddRange([lblFriends, lblDb]);

		// Dock 顺序：后加入的先贴边 —— 状态栏最底，系统日志在它上面，列表填充其余空间
		Controls.Add(lvEvents);
		Controls.Add(txtLog);
		Controls.Add(status);
		Controls.Add(top);

		btnStart.Click += (_, _) => StartSession();
		btnStop.Click += (_, _) => StopSession();
		btnStats.Click += (_, _) => OpenStats();
		btnSettings.Click += (_, _) => OpenSettings();
		btnOpenLogs.Click += (_, _) => OpenDirectory(Path.Combine(configDir, string.IsNullOrWhiteSpace(config.LogDirectory) ? "logs" : config.LogDirectory));
		btnOpenDb.Click += (_, _) => OpenDirectory(configDir);
		Resize += OnResizeToTray;
		FormClosing += OnFormClosing;
	}

	void StartSession() {
		if (session != null) {
			return;
		}

		if (string.IsNullOrWhiteSpace(config.Username)) {
			MessageBox.Show(this, "请先在「设置」中填写 Steam 用户名。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
			OpenSettings();

			if (string.IsNullOrWhiteSpace(config.Username)) {
				return;
			}
		}

		if (string.IsNullOrWhiteSpace(config.Password) && TokenStore.Load(config.Username).RefreshToken == null) {
			MessageBox.Show(this, "首次登录需要密码验证（之后登录令牌会保存在本地，密码可留空）。\n请先在「设置」中填写 Steam 密码。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
			OpenSettings();

			if (string.IsNullOrWhiteSpace(config.Password) && TokenStore.Load(config.Username).RefreshToken == null) {
				return;
			}
		}

		try {
			fileLog = new DailyFileLogger(Path.Combine(configDir, string.IsNullOrWhiteSpace(config.LogDirectory) ? "logs" : config.LogDirectory));
			database = new SqliteEventStore(Path.Combine(configDir, string.IsNullOrWhiteSpace(config.DatabasePath) ? "friends.db" : config.DatabasePath));
			database.Initialize();
			session = new FriendSession(config, fileLog, database, new GuiAuthenticator(config.SharedSecret, this));
		} catch (Exception e) {
			MessageBox.Show(this, $"初始化失败: {e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
			return;
		}

		if (config.WsEnabled) {
			bool requireToken = !config.WsLoopbackOnly;
			wsServer = new WsServer(
				string.IsNullOrWhiteSpace(config.WsHost) ? "127.0.0.1" : config.WsHost.Trim(),
				config.WsPort,
				requireToken,
				config.WsToken,
				() => session.GetCurrentStates(),
				() => session.SelfSteamId);
			wsServer.SystemLogged += entry => SafeInvoke(() => AppendLog(entry.Level, entry.Message));

			try {
				wsServer.Start();
			} catch (Exception e) {
				AppendLog(LogLevel.Error, $"WebSocket 服务启动失败: {e.Message}（Windows 上绑定非 127.0.0.1 地址可能需要管理员权限或 urlacl）");
				wsServer = null;
			}
		}

		session.StatusChanged += status => SafeInvoke(() => {
			SetState(status);
			wsServer?.PushStatus(status);
		});
		session.SystemLogged += entry => SafeInvoke(() => AppendLog(entry.Level, entry.Message));
		session.FriendEventRecorded += record => SafeInvoke(() => {
			AddRecord(record);
			MaybeBalloon(record);
			wsServer?.PushEvent(record);
		});
		session.FriendCountChanged += count => SafeInvoke(() => lblFriends.Text = $"好友: {count}");
		session.SessionStopped += () => SafeInvoke(() => {
			// 会话自己放弃了（如缺少密码无法重新验证），恢复到可启动状态
			cts?.Cancel();
			session = null;
			cts = null;

			try {
				database?.Dispose();
			} catch (Exception) {
				// 忽略
			}

			database = null;
			fileLog = null;
			UpdateCommandState();
		});

		cts = new CancellationTokenSource();
		var currentSession = session;
		var token = cts.Token;

		_ = Task.Run(async () => {
			try {
				await currentSession.RunAsync(token).ConfigureAwait(false);
			} catch (Exception e) {
				SafeInvoke(() => AppendLog(LogLevel.Error, $"监控线程异常退出: {e.Message}"));
			}
		});

		UpdateCommandState();
		AppendLog(LogLevel.Info, "监控已启动");
	}

	void StopSession() {
		if (session == null) {
			return;
		}

		cts?.Cancel();
		session = null;
		cts = null;

		wsServer?.Stop();
		wsServer = null;

		try {
			database?.Dispose();
		} catch (Exception) {
			// 忽略
		}

		database = null;
		fileLog = null;

		SetState("未连接");
		UpdateCommandState();
		AppendLog(LogLevel.Info, "监控已停止");
	}

	void UpdateCommandState() {
		bool running = session != null;
		btnStart.Enabled = menuStart.Enabled = !running;
		btnStop.Enabled = menuStop.Enabled = running;
		btnSettings.Enabled = !running;
	}

	void OpenSettings() {
		using var dialog = new SettingsDialog(config);

		if (dialog.ShowDialog(this) == DialogResult.OK) {
			try {
				config.Save(configPath);
			} catch (Exception e) {
				MessageBox.Show(this, $"保存配置文件失败: {e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}

			AppendLog(LogLevel.Info, "设置已保存" + (session != null ? "；监控运行中，停止后重新启动才会生效" : string.Empty));
		}
	}

	void OpenStats() {
		string dbPath = Path.Combine(configDir, string.IsNullOrWhiteSpace(config.DatabasePath) ? "friends.db" : config.DatabasePath);
		new StatsWindow(dbPath).Show(this);
	}

	void AddRecord(FriendEventRecord record) {
		var item = new ListViewItem([
			record.Time.ToString("HH:mm:ss"),
			record.Name,
			record.SteamId.ToString(),
			record.Status,
			record.RichPresence ?? string.Empty
		]) {
			ForeColor = StatusColor(record.Status),
			ToolTipText = record.RichPresence ?? record.GameName ?? string.Empty
		};

		lvEvents.Items.Add(item);

		if (lvEvents.Items.Count > 500) {
			lvEvents.Items.RemoveAt(0);
		}

		lvEvents.EnsureVisible(lvEvents.Items.Count - 1);
	}

	void MaybeBalloon(FriendEventRecord record) {
		// 启动快照和移除好友不提醒；好友上线 / 进入游戏时气泡提示
		if (!config.NotifyOnFriendOnline || record.EventType != "status") {
			return;
		}

		if (record.Status != "在线" && !record.Status.StartsWith("游戏中")) {
			return;
		}

		trayIcon.ShowBalloonTip(4000, "好友动态", $"{record.Name}  {record.Status}", ToolTipIcon.Info);
	}

	static Color StatusColor(string status) {
		if (status == "移除好友") {
			return Color.Firebrick;
		}

		if (status.StartsWith("游戏中")) {
			return Color.FromArgb(0, 102, 204);
		}

		if (status == "在线" || status.StartsWith("离线（游戏中")) {
			return Color.FromArgb(0, 148, 60);
		}

		if (status is "离线" or "隐身") {
			return Color.Gray;
		}

		return Color.FromArgb(184, 134, 11);
	}

	void SetState(string status) {
		lblState.Text = "● " + status;
		lblState.ForeColor = status switch {
			"已连接（隐身模式）" => Color.FromArgb(0, 148, 60),
			"未连接" => Color.Gray,
			string s when s.Contains("断开") || s.Contains("失败") => Color.Firebrick,
			_ => Color.DarkOrange
		};
		trayIcon.Text = $"Steam 好友状态监控\n{status}".Length > 63 ? $"Steam 好友状态监控\n{status[..60]}" : $"Steam 好友状态监控\n{status}";
	}

	void AppendLog(LogLevel level, string message) {
		string prefix = level switch {
			LogLevel.Warning => "[警告] ",
			LogLevel.Error => "[错误] ",
			_ => string.Empty
		};

		txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}{message}\r\n");

		// 控制系统日志长度，避免无限增长
		if (txtLog.Lines.Length > 500) {
			txtLog.Text = string.Join("\r\n", txtLog.Lines[^400..]) + "\r\n";
		}
	}

	void SafeInvoke(Action action) {
		try {
			if (IsDisposed || !IsHandleCreated) {
				return;
			}

			BeginInvoke(action);
		} catch (ObjectDisposedException) {
			// 窗口正在关闭，丢弃
		}
	}

	void OnResizeToTray(object? sender, EventArgs e) {
		if (WindowState == FormWindowState.Minimized) {
			Hide();
			WindowState = FormWindowState.Normal;
			trayIcon.ShowBalloonTip(2000, "仍在运行", "已最小化到托盘，双击托盘图标恢复界面", ToolTipIcon.Info);
		}
	}

	void RestoreFromTray() {
		Show();
		WindowState = FormWindowState.Normal;
		Activate();
	}

	void OnFormClosing(object? sender, FormClosingEventArgs e) {
		// 点关闭 = 最小化到托盘，真正的退出走托盘菜单的「退出」
		if (!realExit && e.CloseReason == CloseReason.UserClosing) {
			e.Cancel = true;
			Hide();
			trayIcon.ShowBalloonTip(3000, "仍在运行", "程序已最小化到托盘，双击托盘图标恢复；退出请用托盘右键菜单", ToolTipIcon.Info);
			return;
		}

		cts?.Cancel();
		session = null;

		wsServer?.Stop();
		wsServer = null;

		try {
			database?.Dispose();
		} catch (Exception) {
			// 忽略
		}

		trayIcon.Visible = false;
		trayIcon.Dispose();
	}

	static void OpenDirectory(string path) {
		try {
			Directory.CreateDirectory(path);
			Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
		} catch (Exception e) {
			MessageBox.Show($"打开目录失败: {e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}
}
