// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SteamFriendMonitor.Core;

// WebSocket 接口：
// - 连接成功即推送全部好友当前状态快照（type=snapshot，含自己 SteamID）
// - 之后实时推送状态变化（event）、连接状态（status），每 10 秒一次心跳（heartbeat）
// - 客户端可发送含 "get_snapshot" 的文本消息主动拉取快照
// - 默认只监听 127.0.0.1；绑定非回环地址时要求 ?token= 访问令牌，且由界面/命令行做倒计时确认
public sealed class WsServer {
	readonly string bindHost;
	readonly int port;
	readonly bool requireToken;
	readonly string? token;
	readonly Func<IReadOnlyList<FriendEventRecord>> snapshotProvider;
	readonly Func<ulong?> selfSteamIdProvider;
	readonly ConcurrentDictionary<WebSocket, byte> clients = new();
	readonly SemaphoreSlim sendLock = new(1, 1);
	readonly JsonSerializerOptions jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	HttpListener? listener;
	System.Threading.Timer? heartbeatTimer;
	CancellationTokenSource? cts;

	public event Action<(LogLevel Level, string Message)>? SystemLogged;

	public string Url { get; }

	public bool Running { get; private set; }

	public WsServer(string bindHost, int port, bool requireToken, string? token, Func<IReadOnlyList<FriendEventRecord>> snapshotProvider, Func<ulong?> selfSteamIdProvider) {
		ArgumentException.ThrowIfNullOrWhiteSpace(bindHost);
		ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
		ArgumentNullException.ThrowIfNull(snapshotProvider);
		ArgumentNullException.ThrowIfNull(selfSteamIdProvider);

		this.bindHost = bindHost;
		this.port = port;
		this.requireToken = requireToken;
		this.token = token;
		this.snapshotProvider = snapshotProvider;
		this.selfSteamIdProvider = selfSteamIdProvider;

		string displayHost = bindHost is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : bindHost;
		Url = $"ws://{displayHost}:{port}/" + (requireToken && !string.IsNullOrEmpty(token) ? $"?token={token}" : string.Empty);
	}

	public void Start() {
		if (Running) {
			return;
		}

		string prefixHost = bindHost is "0.0.0.0" or "::" or "*" ? "+" : bindHost;
		listener = new HttpListener();
		listener.Prefixes.Add($"http://{prefixHost}:{port}/");
		listener.Start();

		Running = true;
		cts = new CancellationTokenSource();
		_ = AcceptLoopAsync();

		heartbeatTimer = new System.Threading.Timer(OnHeartbeat, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
		SystemLogged?.Invoke((LogLevel.Info, $"WebSocket 服务已启动: {Url}"));
	}

	public void Stop() {
		if (!Running) {
			return;
		}

		Running = false;
		heartbeatTimer?.Dispose();
		heartbeatTimer = null;
		cts?.Cancel();

		try {
			listener?.Stop();
			listener?.Close();
		} catch (Exception) {
			// 忽略
		}

		foreach (WebSocket webSocket in clients.Keys) {
			try {
				webSocket.Abort();
				webSocket.Dispose();
			} catch (Exception) {
				// 忽略
			}
		}

		clients.Clear();
		cts?.Dispose();
		cts = null;
	}

	async Task AcceptLoopAsync() {
		try {
			while (Running) {
				HttpListenerContext context;

				try {
					context = await listener!.GetContextAsync().ConfigureAwait(false);
				} catch (Exception) {
					break; // Stop() 会令等待失败
				}

				if (requireToken && !string.Equals(context.Request.QueryString["token"], token, StringComparison.Ordinal)) {
					context.Response.StatusCode = 401;
					context.Response.Close();
					continue;
				}

				if (!context.Request.IsWebSocketRequest) {
					context.Response.StatusCode = 400;
					context.Response.Close();
					continue;
				}

				var webSocket = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
				clients.TryAdd(webSocket.WebSocket, 0);

				// 连接即推送全量快照
				await SendTo(webSocket.WebSocket, BuildSnapshot()).ConfigureAwait(false);
				_ = ReceiveLoopAsync(webSocket.WebSocket);
			}
		} catch (Exception) {
			// 监听异常退出（Stop 或端口错误），由 Start 调用方处理
		}
	}

	async Task ReceiveLoopAsync(WebSocket webSocket) {
		var buffer = new byte[4096];

		try {
			while (Running && webSocket.State == WebSocketState.Open) {
				WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);

				if (result.MessageType == WebSocketMessageType.Close) {
					break;
				}

				string text = Encoding.UTF8.GetString(buffer, 0, result.Count);

				if (text.Contains("get_snapshot")) {
					await SendTo(webSocket, BuildSnapshot()).ConfigureAwait(false);
				} else if (text.Contains("ping")) {
					await SendTo(webSocket, BuildHeartbeat()).ConfigureAwait(false);
				}
			}
		} catch (Exception) {
			// 连接异常断开
		} finally {
			clients.TryRemove(webSocket, out _);

			try {
				webSocket.Dispose();
			} catch (Exception) {
				// 忽略
			}
		}
	}

	public void PushEvent(FriendEventRecord record) => _ = BroadcastAsync(JsonSerializer.Serialize(new {
		type = "event",
		data = new {
			steamId = record.SteamId,
			name = record.Name,
			status = record.Status,
			personaState = record.PersonaState,
			game = record.GameName,
			richPresence = record.RichPresence,
			eventType = record.EventType,
			time = record.Time.ToString("yyyy-MM-dd HH:mm:ss")
		}
	}, jsonOptions));

	public void PushStatus(string status) => _ = BroadcastAsync(JsonSerializer.Serialize(new {
		type = "status",
		data = new { text = status, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
	}, jsonOptions));

	void OnHeartbeat(object? state) => _ = BroadcastAsync(BuildHeartbeat());

	string BuildHeartbeat() => JsonSerializer.Serialize(new {
		type = "heartbeat",
		data = new {
			time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			intervalSeconds = 10,
			clients = clients.Count
		}
	}, jsonOptions);

	string BuildSnapshot() => JsonSerializer.Serialize(new {
		type = "snapshot",
		data = new {
			selfSteamId = selfSteamIdProvider(),
			time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
			friends = snapshotProvider().Select(static f => new {
				steamId = f.SteamId,
				name = f.Name,
				status = f.Status,
				personaState = f.PersonaState,
				game = f.GameName,
				richPresence = f.RichPresence
			})
		}
	}, jsonOptions);

	async Task BroadcastAsync(string message) {
		if (!Running || clients.IsEmpty) {
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(message);

		await sendLock.WaitAsync().ConfigureAwait(false);

		try {
			foreach (WebSocket webSocket in clients.Keys) {
				try {
					if (webSocket.State == WebSocketState.Open) {
						await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
					}
				} catch (Exception) {
					clients.TryRemove(webSocket, out _);
				}
			}
		} finally {
			sendLock.Release();
		}
	}

	async Task SendTo(WebSocket webSocket, string message) {
		byte[] bytes = Encoding.UTF8.GetBytes(message);

		await sendLock.WaitAsync().ConfigureAwait(false);

		try {
			if (webSocket.State == WebSocketState.Open) {
				await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
			}
		} finally {
			sendLock.Release();
		}
	}
}
