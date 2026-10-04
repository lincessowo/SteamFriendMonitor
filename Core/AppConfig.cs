using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamFriendMonitor.Core;

public sealed class AppConfig {
	[JsonPropertyName("Username")]
	public string Username { get; set; } = string.Empty;

	[JsonPropertyName("Password")]
	public string Password { get; set; } = string.Empty;

	// 手机令牌 shared_secret（.maFile 中的 shared_secret 字段，base64），可选：
	// 填写后登录时自动生成 Steam 令牌验证码，无需手动输入
	[JsonPropertyName("SharedSecret")]
	public string SharedSecret { get; set; } = string.Empty;

	// 好友状态日志目录（相对 config.json 所在目录），按天生成 yyyyMMdd.txt
	[JsonPropertyName("LogDirectory")]
	public string LogDirectory { get; set; } = "logs";

	// SQLite 数据库文件（相对 config.json 所在目录），好友动态会同步写入
	[JsonPropertyName("DatabasePath")]
	public string DatabasePath { get; set; } = "friends.db";

	// 连接 Steam 的方式：All（默认，与 ASF 一致：优先 TCP 直连 CM 服务器，失败回退 WebSocket）/ Tcp / Udp / WebSocket
	// 注意：启用代理时会自动强制使用 WebSocket（TCP CM 是原始 socket，无法走代理）
	[JsonPropertyName("Protocol")]
	public string Protocol { get; set; } = "All";

	// Steam 会话 ID，留空则根据用户名自动生成固定值，避免与官方客户端互踢
	[JsonPropertyName("LoginID")]
	public uint? LoginID { get; set; }

	// 启动时是否把当前好友列表的初始状态也写进日志
	[JsonPropertyName("LogInitialSnapshot")]
	public bool LogInitialSnapshot { get; set; } = true;

	// 好友上线 / 进入游戏时弹出托盘气泡提醒
	[JsonPropertyName("NotifyOnFriendOnline")]
	public bool NotifyOnFriendOnline { get; set; } = true;

	// 代理设置：None / Http / Socks5，作用于认证请求和 CM 连接（WebSocket 隧道）
	[JsonPropertyName("ProxyType")]
	public string ProxyType { get; set; } = "None";

	[JsonPropertyName("ProxyHost")]
	public string ProxyHost { get; set; } = string.Empty;

	[JsonPropertyName("ProxyPort")]
	public int ProxyPort { get; set; }

	// 代理认证（可选，SOCKS5 不支持账号密码认证时忽略）
	[JsonPropertyName("ProxyUsername")]
	public string ProxyUsername { get; set; } = string.Empty;

	[JsonPropertyName("ProxyPassword")]
	public string ProxyPassword { get; set; } = string.Empty;

	// WebSocket 接口：默认关闭。开启后客户端可获取好友快照、实时动态，并接收 10 秒心跳
	[JsonPropertyName("WsEnabled")]
	public bool WsEnabled { get; set; } = false;

	[JsonPropertyName("WsPort")]
	public int WsPort { get; set; } = 8765;

	// 绑定地址：默认 127.0.0.1（仅本机可连）。改成其他地址需要倒计时确认，且客户端必须携带 WsToken
	[JsonPropertyName("WsHost")]
	public string WsHost { get; set; } = "127.0.0.1";

	// 非 127.0.0.1 绑定时客户端必须携带的访问令牌（开启时自动生成，可自行更换）
	[JsonPropertyName("WsToken")]
	public string WsToken { get; set; } = string.Empty;

	[JsonIgnore]
	public bool WsLoopbackOnly => WsHost.Trim() is "" or "127.0.0.1" or "localhost" or "::1";

	[JsonIgnore]
	public bool ProxyEnabled => ProxyType is not ("None" or "") && !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort is > 0 and <= 65535;

	[JsonIgnore]
	public string ProxyScheme => ProxyType.Equals("Socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";

	static readonly JsonSerializerOptions PrettyOptions = new() {
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static AppConfig Load(string path) =>
		JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();

	public void Save(string path) =>
		File.WriteAllText(path, JsonSerializer.Serialize(this, PrettyOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

	public static void SaveTemplate(string path) => new AppConfig().Save(path);
}
