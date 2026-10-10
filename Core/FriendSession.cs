// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamFriendMonitor.Core;

public enum LogLevel {
	Info, Warning, Error
}

// 一条好友动态记录（txt 日志、SQLite、界面三处共用）
public sealed record FriendEventRecord(DateTime Time, ulong SteamId, string Name, string Status, int PersonaState, string? GameName, string? RichPresence, string EventType);

// Steam 会话核心：隐身登录、断线自动重连、监听好友状态实时推送。
// 通过事件向界面汇报，不直接依赖任何 UI；数据同时写 txt 日志和 SQLite。
public sealed class FriendSession {
	readonly AppConfig config;
	readonly DailyFileLogger fileLog;
	readonly SqliteEventStore database;
	readonly IAuthenticator authenticator;

	readonly SteamClient steamClient;
	readonly CallbackManager callbackManager;
	readonly SteamUser steamUser;
	readonly SteamFriends steamFriends;

	// 好友快照与去重（回调线程访问，统一走 stateLock）
	readonly Dictionary<ulong, FriendRecord> records = new();
	readonly HashSet<ulong> friendIds = new();
	readonly object stateLock = new();

	volatile bool connected;
	volatile bool loggedOn;
	volatile bool exitRequested;
	volatile bool personaSet;

	// 每次登录成功后重置为 5 秒；连续失败时指数退避，最长 5 分钟
	TimeSpan reconnectDelay = TimeSpan.FromSeconds(5);
	string? refreshToken;
	string? guardData;
	System.Threading.Timer? tokenRenewalTimer;
	int authRunning;

	sealed record FriendRecord(string Name, EPersonaState State, string? GameName, string? RichPresence, bool StateLogged);

	public event Action<string>? StatusChanged;
	public event Action<(LogLevel Level, string Message)>? SystemLogged;
	public event Action<FriendEventRecord>? FriendEventRecorded;
	public event Action<int>? FriendCountChanged;
	public event Action? SessionStopped; // 会话放弃重连（如缺少密码无法重新验证）时通知界面恢复按钮

	public bool ProxySwitchedToWebSocket { get; }

	public ulong? SelfSteamId => steamClient.SteamID?.ConvertToUInt64();

	// 登录凭据失效且无法自动重新验证时置位，放弃重连
	bool gaveUp;

	public FriendSession(AppConfig config, DailyFileLogger fileLog, SqliteEventStore database, IAuthenticator authenticator) {
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(fileLog);
		ArgumentNullException.ThrowIfNull(database);
		ArgumentNullException.ThrowIfNull(authenticator);

		this.config = config;
		this.fileLog = fileLog;
		this.database = database;
		this.authenticator = authenticator;

		ProtocolTypes protocol = ParseProtocol(config.Protocol);

		if (config.ProxyEnabled && protocol != ProtocolTypes.WebSocket) {
			// TCP CM 是原始 socket，无法走代理隧道；启用代理时必须改走 WebSocket（经代理隧道）
			protocol = ProtocolTypes.WebSocket;
			ProxySwitchedToWebSocket = true;
		}

		steamClient = new SteamClient(SteamConfiguration.Create(builder => {
			builder.WithProtocolTypes(protocol);

			// 代理经 HttpClientFactory 注入：认证请求与 WebSocket CM 连接都从它创建
			if (config.ProxyEnabled) {
				builder.WithHttpClientFactory(_ => ProxyHttpClientFactory.Create(config));
			}
		}), "SteamFriendMonitor");

		callbackManager = new CallbackManager(steamClient);
		steamUser = steamClient.GetHandler<SteamUser>()!;
		steamFriends = steamClient.GetHandler<SteamFriends>()!;

		callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
		callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
		callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
		callbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
		callbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
		callbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
	}

	public async Task RunAsync(CancellationToken cancellationToken) {
		(refreshToken, guardData) = TokenStore.Load(config.Username);
		LogInfo(string.IsNullOrEmpty(refreshToken)
			? "本机没有登录凭据，首次登录将进行密码验证"
			: $"已加载本机保存的登录凭据（refresh token 长度 {refreshToken.Length}，开头 {refreshToken[..Math.Min(6, refreshToken.Length)]}…）");

		// 恢复上次运行时的好友状态：如果状态没变化（如一直是离线），不再重复写快照
		int restored = 0;

		foreach (SqliteEventStore.LastState state in database.ReadLatestStates()) {
			records[state.SteamId] = new FriendRecord(state.Name, (EPersonaState)state.State, state.Game, state.RichPresence, true);
			restored++;
		}

		if (restored > 0) {
			LogInfo($"已从数据库恢复 {restored} 位好友的上次状态，状态未变化时不会重复记录");
		}

		if (config.ProxyEnabled) {
			LogInfo($"已启用代理: {config.ProxyScheme}://{config.ProxyHost}:{config.ProxyPort}" + (ProxySwitchedToWebSocket ? "（CM 连接已自动切换为 WebSocket 隧道）" : string.Empty));
		}

		SetStatus("正在连接 Steam 网络…");
		steamClient.Connect();

		tokenRenewalTimer = new System.Threading.Timer(OnTokenRenewalTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

		try {
			while (!cancellationToken.IsCancellationRequested) {
				await callbackManager.RunWaitCallbackAsync(cancellationToken).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) {
			// 用户主动停止
		}

		exitRequested = true;
		tokenRenewalTimer?.Dispose();

		LogInfo("正在断开与 Steam 的连接…");

		try {
			steamUser.LogOff();
			steamClient.Disconnect();
		} catch (Exception) {
			// 可能已处于断开状态，忽略
		}
	}

	// 供 WebSocket 快照使用：当前好友的最新已知状态
	public IReadOnlyList<FriendEventRecord> GetCurrentStates() {
		lock (stateLock) {
			return friendIds
				.Where(records.ContainsKey)
				.Select(id => {
					FriendRecord r = records[id];
					return new FriendEventRecord(DateTime.Now, id, r.Name, StatusText.Describe(r.State, r.GameName), (int)r.State, r.GameName, r.RichPresence, "snapshot");
				})
				.ToList();
		}
	}

	void OnConnected(SteamClient.ConnectedCallback callback) {
		connected = true;
		loggedOn = false;
		personaSet = false;
		reconnectDelay = TimeSpan.FromSeconds(5);

		LogInfo("已连接到 Steam 网络");
		SetStatus("已连接，正在登录…");

		if (!string.IsNullOrEmpty(refreshToken)) {
			LogOnWithToken();
		} else if (string.IsNullOrWhiteSpace(config.Password)) {
			// 令牌已失效且设置里没有密码，无法自动重新验证——明确停止，不做无声的无限重试
			LogError("登录凭据已失效，且设置中没有保存 Steam 密码，无法自动重新验证");
			LogError("请在「设置」中填写 Steam 密码后，重新点击「启动监控」");
			SetStatus("已停止：请在设置中填写密码");
			gaveUp = true;
			steamClient.Disconnect();
		} else {
			_ = AuthenticateAndLogOnAsync();
		}
	}

	void OnDisconnected(SteamClient.DisconnectedCallback callback) {
		connected = false;
		loggedOn = false;
		tokenRenewalTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

		if (exitRequested) {
			SetStatus("未连接");
			return;
		}

		if (gaveUp) {
			SetStatus("未连接");
			SessionStopped?.Invoke();
			return;
		}

		LogWarn($"与 Steam 的连接已断开，{reconnectDelay.TotalSeconds:0} 秒后自动重连");
		SetStatus($"已断开，{reconnectDelay.TotalSeconds:0} 秒后重连");

		ReconnectAfter(reconnectDelay);
		reconnectDelay = TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, TimeSpan.FromSeconds(300).Ticks));
	}

	async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
		if (callback.Result == EResult.OK) {
			loggedOn = true;
			LogInfo($"登录成功: {config.Username}（SteamID: {steamClient.SteamID?.ConvertToUInt64()}）");
			SetStatus("已连接（隐身模式）");

			SetInvisiblePersona();
			reconnectDelay = TimeSpan.FromSeconds(5);

			// Steam 会定期轮换登录令牌，必须在过期前续期，否则令牌一段时间后会被判定失效（AccessDenied）
			tokenRenewalTimer?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

			return;
		}

		loggedOn = false;
		LogError($"登录失败: {callback.Result}");

		switch (callback.Result) {
			case EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.CachedCredentialInvalid or EResult.InvalidParam or EResult.InvalidLoginAuthCode or EResult.AccountLoginDeniedNeedTwoFactor or EResult.TwoFactorCodeMismatch:
				// 本地保存的登录令牌可能已失效（改密码 / 撤销授权 / 到期未续期等），清掉后重新走密码认证。
				// 断开重连，让 OnConnected 统一触发认证，避免与重连流程竞争（旧版本在这里的延迟认证会报
				// “The SteamClient instance must be connected”并踢掉正在进行的重连）
				LogWarn("本机保存的登录凭据已失效，将清除后重新进行密码验证");

				if (callback.Result == EResult.AccessDenied) {
					LogWarn("提示：若反复出现，请检查密码是否正确、是否修改过密码、是否在 Steam 手机应用上撤销了本机授权");
				}

				TokenStore.Clear(config.Username);
				refreshToken = null;
				steamClient.Disconnect();
				break;

			case EResult.RateLimitExceeded:
				LogError("登录尝试过于频繁（RateLimitExceeded），请约 10 分钟后再试");
				reconnectDelay = TimeSpan.FromMinutes(10);
				steamClient.Disconnect();
				break;

			default:
				await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);

				if (connected && !loggedOn) {
					if (string.IsNullOrEmpty(refreshToken)) {
						await AuthenticateAndLogOnAsync().ConfigureAwait(false);
					} else {
						LogOnWithToken();
					}
				}

				break;
		}
	}

	void OnAccountInfo(SteamUser.AccountInfoCallback callback) => SetInvisiblePersona();

	void OnFriendsList(SteamFriends.FriendsListCallback callback) {
		List<ulong> removed = new();
		List<SteamID> toRequest = new();

		// Steam 的好友列表推送分两种：登录后的全量列表（Incremental = false）
		// 和好友关系变化时的增量列表。这里统一按“单条关系变化”处理：
		// 关系为 Friend 且未知 → 新好友；已有好友但关系改变 → 好友被移除
		lock (stateLock) {
			foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList) {
				// 好友列表推送里也包含群组（Clan）/聊天频道，它们带同样的 Friend 关系值，
				// 必须按账号类型排除，否则群组会被当成好友
				if (!friend.SteamID.IsIndividualAccount) {
					continue;
				}

				ulong id = friend.SteamID.ConvertToUInt64();
				bool known = friendIds.Contains(id);

				if (friend.Relationship == EFriendRelationship.Friend) {
					if (!known) {
						friendIds.Add(id);
					}

					// 全量列表 → 请求全部好友资料（与官方客户端加载好友列表时行为一致）；
					// 增量列表 → 只请求新好友。之后的状态变化全部由 CM 连接实时推送
					if (!callback.Incremental || !known) {
						toRequest.Add(friend.SteamID);
					}
				} else if (known) {
					friendIds.Remove(id);
					removed.Add(id);
				}
			}
		}

		foreach (ulong id in removed) {
			records.TryGetValue(id, out FriendRecord? record);
			EmitEvent(id, string.IsNullOrEmpty(record?.Name) ? "未知" : record!.Name, record?.State ?? EPersonaState.Offline, record?.GameName, record?.RichPresence, "removed");
		}

		int count;
		lock (stateLock) {
			count = friendIds.Count;
		}

		FriendCountChanged?.Invoke(count);

		if (!callback.Incremental && callback.FriendList.Count > 0) {
			LogInfo($"好友列表已同步（全量 {callback.FriendList.Count} 条，当前 {count} 位好友），正在请求资料…");
		}

		if (toRequest.Count > 0) {
			// 放到后台线程分批请求，避免阻塞回调分发
			_ = Task.Run(() => RequestFriendInfoBatches(toRequest));
		}
	}

	void OnPersonaState(SteamFriends.PersonaStateCallback callback) {
		ulong id = callback.FriendID.ConvertToUInt64();

		if (!callback.FriendID.IsIndividualAccount) {
			return; // 群组等非个人账号的 persona 更新不记录
		}

		if (steamClient.SteamID == null || id == steamClient.SteamID.ConvertToUInt64()) {
			return; // 自己的状态变化不记录
		}

		lock (stateLock) {
			if (!friendIds.Contains(id)) {
				return; // 只关注好友（群成员等陌生人会话也会推送 persona）
			}
		}

		// Steam 的一条 persona 更新可能只携带部分字段，用 StatusFlags 判断本次带了哪些字段；
		// 个别情况下 flags 为空，此时仅采信非默认值，避免把“仅昵称更新”误判成“离线”
		EClientPersonaStateFlag flags = callback.StatusFlags;
		bool hasName;
		bool hasPresence;
		bool hasGame;
		bool hasBlob;

		if (flags == (EClientPersonaStateFlag)0) {
			hasName = !string.IsNullOrEmpty(callback.Name);
			hasPresence = callback.State != EPersonaState.Offline;
			hasGame = !string.IsNullOrEmpty(callback.GameName);
			hasBlob = callback.GameDataBlob is { Length: > 0 };
		} else {
			hasName = flags.HasFlag(EClientPersonaStateFlag.PlayerName);
			hasPresence = flags.HasFlag(EClientPersonaStateFlag.Status) || flags.HasFlag(EClientPersonaStateFlag.Presence);
			hasGame = flags.HasFlag(EClientPersonaStateFlag.GameExtraInfo) || flags.HasFlag(EClientPersonaStateFlag.GameDataBlob);
			hasBlob = flags.HasFlag(EClientPersonaStateFlag.GameDataBlob);
		}

		// 游戏富状态（如 CS2 的地图 / 比分）从 GameDataBlob 解码，与游戏名分开存储统计
		string? decodedRichPresence = hasBlob ? RichPresenceDecoder.Decode(callback.GameDataBlob) : null;

		lock (stateLock) {
			records.TryGetValue(id, out FriendRecord? record);
			record ??= new FriendRecord(string.Empty, EPersonaState.Offline, null, null, false);
			bool wasLogged = record.StateLogged;

			string name = hasName && !string.IsNullOrEmpty(callback.Name) ? callback.Name : record.Name;
			EPersonaState state = hasPresence ? callback.State : record.State;
			string? game = hasGame ? (string.IsNullOrEmpty(callback.GameName) ? null : callback.GameName) : record.GameName;
			string? richPresence = hasBlob ? decodedRichPresence : record.RichPresence;

			if (string.IsNullOrEmpty(name)) {
				return; // 名称还没拿到，等 Steam 后续的更新
			}

			bool stateChanged = record.Name != name || record.State != state || record.GameName != game;

			if (!hasPresence && !hasGame) {
				// 纯昵称更新：只更新缓存，不产生记录（Steam 分块推送数据，避免“离线→在线”式的噪音）
				if (stateChanged) {
					records[id] = new FriendRecord(name, state, game, richPresence, wasLogged);
				}

				return;
			}

			if (!stateChanged) {
				// 状态没变：刷新富状态缓存。
				// 若该好友的状态还没记录过（如全员离线时只收到“离线”确认），补一条初始快照，
				// 否则好友全离线时日志里一条记录都没有
				records[id] = new FriendRecord(name, state, game, richPresence, true);

				if (!wasLogged) {
					if (config.LogInitialSnapshot) {
						EmitEvent(id, name, state, game, richPresence, "snapshot");
					}

					return;
				}

				// 富状态变化（比分/地图等）只入数据库（不进 txt 和主列表，避免刷屏），供统计窗口查看
				if (richPresence != null && record.RichPresence != richPresence) {
					EmitRichPresence(id, name, state, game, richPresence);
				}

				return;
			}

			records[id] = new FriendRecord(name, state, game, richPresence, true);

			if (!wasLogged && !config.LogInitialSnapshot) {
				return; // 首个状态只做快照标记，不写日志
			}

			EmitEvent(id, name, state, game, richPresence, wasLogged ? "status" : "snapshot");
		}
	}

	async Task AuthenticateAndLogOnAsync() {
		// 可能正处在断线重连流程中，此时无法认证；重连成功后 OnConnected 会再次触发
		if (!steamClient.IsConnected) {
			LogWarn("尚未连接到 Steam，等待重连后再进行账号验证");
			return;
		}

		// 防止重复认证（连接成功回调和登录失败回调可能同时触发）
		if (Interlocked.Exchange(ref authRunning, 1) == 1) {
			return;
		}

		try {
			LogInfo("本机没有可用登录凭据，正在通过 Steam 认证接口验证账号…");
			SetStatus("正在验证账号…");

			IAuthenticator auth = authenticator;

			CredentialsAuthSession authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails {
				Authenticator = auth,
				DeviceFriendlyName = Environment.MachineName,
				GuardData = guardData,
				IsPersistentSession = true,
				Password = config.Password,
				Username = config.Username
			}).ConfigureAwait(false);

			AuthPollResult pollResult = await authSession.PollingWaitForResultAsync().ConfigureAwait(false);

			refreshToken = pollResult.RefreshToken;

			// NewGuardData 是 SteamGuard 设备信任数据，持久化后下次认证 Steam 就认得这台设备
			if (!string.IsNullOrEmpty(pollResult.NewGuardData)) {
				guardData = pollResult.NewGuardData;
			}

			string? saveError = TokenStore.Save(config.Username, refreshToken, guardData);
			LogInfo(saveError == null
				? $"登录凭据已获取并保存（refresh token 长度 {refreshToken.Length}；下次启动无需重复验证）"
				: $"登录凭据已获取，但保存失败: {saveError}");

			LogOnWithToken();
		} catch (AuthenticationException e) {
			LogError($"Steam 认证失败: {e.Result}");

			if (e.Result == EResult.AccessDenied) {
				LogWarn("提示：请检查密码是否正确；若近期修改过密码或在其他设备上撤销了授权，需要重新输入密码登录");
			}

			reconnectDelay = e.Result == EResult.RateLimitExceeded ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1);

			if (steamClient.IsConnected) {
				steamClient.Disconnect();
			}
		} catch (AsyncJobFailedException e) {
			LogError($"Steam 认证请求失败: {e.Message}");
			reconnectDelay = TimeSpan.FromMinutes(1);

			if (steamClient.IsConnected) {
				steamClient.Disconnect();
			}
		} catch (OperationCanceledException) {
			// 正在退出
		} catch (Exception e) {
			LogError($"登录过程出现未预期的错误: {e.Message}");
			reconnectDelay = TimeSpan.FromMinutes(1);

			if (steamClient.IsConnected) {
				steamClient.Disconnect();
			}
		} finally {
			Interlocked.Exchange(ref authRunning, 0);
		}
	}

	// 定时续期 Steam 登录令牌：Steam 会轮换 refresh token，长期不续期的令牌会被判定失效（AccessDenied）
	async void OnTokenRenewalTimer(object? state) {
		if (!loggedOn || exitRequested || string.IsNullOrEmpty(refreshToken) || steamClient.SteamID == null) {
			return;
		}

		try {
			AccessTokenGenerateResult result = await steamClient.Authentication.GenerateAccessTokenForAppAsync(steamClient.SteamID, refreshToken, true).ConfigureAwait(false);

			if (!string.IsNullOrEmpty(result.RefreshToken)) {
				refreshToken = result.RefreshToken;
				string? saveError = TokenStore.Save(config.Username, refreshToken, guardData);
				LogInfo(saveError == null ? "Steam 登录令牌已自动续期并保存" : $"Steam 登录令牌已续期，但保存失败: {saveError}");
			}

			ScheduleTokenRenewal(result.AccessToken);
		} catch (Exception e) {
			LogWarn($"Steam 登录令牌续期失败: {e.Message}，10 分钟后重试（当前会话不受影响）");
			tokenRenewalTimer?.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
		}
	}

	void ScheduleTokenRenewal(string? accessToken) {
		DateTime? expiry = string.IsNullOrEmpty(accessToken) ? null : GetJwtExpiry(accessToken);
		TimeSpan due;

		if (expiry.HasValue) {
			due = expiry.Value - DateTime.UtcNow - TimeSpan.FromMinutes(60); // 提前 1 小时续期

			if (due > TimeSpan.FromHours(24)) {
				due = TimeSpan.FromHours(24);
			}

			LogInfo($"Steam 登录令牌有效期至 {expiry:yyyy-MM-dd HH:mm}（UTC），将提前自动续期");
		} else {
			due = TimeSpan.FromMinutes(10);
		}

		if (due < TimeSpan.FromMinutes(1)) {
			due = TimeSpan.FromMinutes(1);
		}

		tokenRenewalTimer?.Change(due, Timeout.InfiniteTimeSpan);
	}

	// 解析 access token（JWT）里的 exp 过期时间
	static DateTime? GetJwtExpiry(string jwt) {
		try {
			string[] parts = jwt.Split('.');

			if (parts.Length < 2) {
				return null;
			}

			string payload = parts[1].Replace('-', '+').Replace('_', '/');

			switch (payload.Length % 4) {
				case 2:
					payload += "==";
					break;
				case 3:
					payload += "=";
					break;
			}

			using var document = JsonDocument.Parse(Convert.FromBase64String(payload));

			if (document.RootElement.TryGetProperty("exp", out JsonElement exp) && exp.TryGetInt64(out long seconds)) {
				return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
			}
		} catch (Exception) {
			// 解析失败按未知有效期处理
		}

		return null;
	}

	void LogOnWithToken() {
		LogInfo("正在使用保存的登录令牌登录（隐身模式）…");

		steamUser.LogOn(new SteamUser.LogOnDetails {
			Username = config.Username,
			AccessToken = refreshToken,
			LoginID = config.LoginID ?? DeriveLoginId(),
			MachineName = Environment.MachineName,
			ShouldRememberPassword = true
		});
	}

	void SetInvisiblePersona() {
		if (personaSet) {
			return;
		}

		personaSet = true;

		try {
			steamFriends.SetPersonaState(EPersonaState.Invisible);
			LogInfo("已设置隐身状态（对好友显示为离线）");
		} catch (Exception e) {
			LogWarn($"设置隐身状态失败: {e.Message}");
		}
	}

	void RequestFriendInfoBatches(List<SteamID> ids) {
		const int BatchSize = 50;

		for (int i = 0; i < ids.Count; i++) {
			RequestFriendInfoSafe(ids[i].ConvertToUInt64());

			if ((i + 1) % BatchSize == 0 && i + 1 < ids.Count) {
				Thread.Sleep(500);
			}
		}
	}

	void RequestFriendInfoSafe(ulong id) {
		try {
			steamFriends.RequestFriendInfo(new SteamID(id), EClientPersonaStateFlag.Status | EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence | EClientPersonaStateFlag.GameExtraInfo | EClientPersonaStateFlag.GameDataBlob);
		} catch (Exception e) {
			LogWarn($"请求好友信息失败（{id}）: {e.Message}");
		}
	}

	void EmitEvent(ulong id, string name, EPersonaState state, string? game, string? richPresence, string eventType) {
		var record = new FriendEventRecord(
			DateTime.Now,
			id,
			name,
			eventType == "removed" ? "移除好友" : StatusText.Describe(state, game),
			(int) state,
			game,
			richPresence,
			eventType);

		try {
			fileLog.Write($"（{record.Time:HH:mm:ss}） {name} {id} {record.Status}");
		} catch (Exception e) {
			LogWarn($"写入日志文件失败: {e.Message}");
		}

		database.Append(record);
		FriendEventRecorded?.Invoke(record);
	}

	// 富状态变化（比分/地图等）只入数据库（event_type = rich_presence），供统计窗口的「游戏动态」查看；
	// 不写 txt、不进主列表，避免比分刷新高频刷屏
	void EmitRichPresence(ulong id, string name, EPersonaState state, string? game, string richPresence) {
		database.Append(new FriendEventRecord(DateTime.Now, id, name, StatusText.Describe(state, game), (int) state, game, richPresence, "rich_presence"));
	}

	void ReconnectAfter(TimeSpan delay) {
		_ = Task.Run(async () => {
			await Task.Delay(delay).ConfigureAwait(false);

			if (!exitRequested && !connected) {
				LogInfo("正在重新连接 Steam…");
				steamClient.Connect();
			}
		});
	}

	// 根据用户名生成固定的会话 ID，与官方客户端等其他会话错开，避免互相顶号
	uint DeriveLoginId() {
		unchecked {
			uint hash = 2166136261;

			foreach (char c in config.Username) {
				hash ^= c;
				hash *= 16777619;
			}

			return hash;
		}
	}

	static ProtocolTypes ParseProtocol(string value) => value.Trim().ToUpperInvariant() switch {
		"TCP" => ProtocolTypes.Tcp,
		"UDP" => ProtocolTypes.Udp,
		"WEBSOCKET" => ProtocolTypes.WebSocket,
		_ => ProtocolTypes.All
	};

	void LogInfo(string message) => SystemLogged?.Invoke((LogLevel.Info, message));
	void LogWarn(string message) => SystemLogged?.Invoke((LogLevel.Warning, message));
	void LogError(string message) => SystemLogged?.Invoke((LogLevel.Error, message));
	void SetStatus(string status) => StatusChanged?.Invoke(status);
}
