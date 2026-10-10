// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;

namespace SteamFriendMonitor.Core;

public sealed record GameStat(string Game, int FriendCount, double Minutes);
public sealed record FriendStat(string Name, ulong SteamId, double OnlineMinutes, double GameMinutes);
public sealed record GameSession(ulong SteamId, string Name, string Game, DateTime Start, DateTime End, double Minutes, string? LastRichPresence);
public sealed record RichPresenceEvent(ulong SteamId, string Name, DateTime Time, string RichPresence);

// 从 SQLite 事件时间线统计：
// - 在线时长 = 相邻两次状态事件的间隔（最后一条未闭合的事件按统计截止时间截断）
// - 游戏会话 = game_name 连续非空的时段（换游戏即切段），会话内的 rich_presence 变化即“游戏中发生了什么”
public static class StatsCalculator {
	static readonly HashSet<string> OfflineStatuses = new(["离线", "隐身", "移除好友"]);

	public static (List<GameStat> Games, List<FriendStat> Friends, List<GameSession> Sessions, List<RichPresenceEvent> Details) Compute(SqliteEventStore store, DateTime start, DateTime end) {
		long startEpoch = new DateTimeOffset(start).ToUnixTimeSeconds();
		long endEpoch = new DateTimeOffset(end).ToUnixTimeSeconds();

		var onlineMinutes = new Dictionary<ulong, double>();
		var inGameMinutes = new Dictionary<ulong, double>();
		var names = new Dictionary<ulong, string>();
		var sessions = new List<GameSession>();
		var details = new List<RichPresenceEvent>();

		foreach (var group in store.ReadTimeline(endEpoch).GroupBy(static r => r.SteamId)) {
			ulong steamId = group.Key;
			string name = string.Empty;
			string? curGame = null;
			long curStart = 0;
			string? curRp = null;

			void CloseSession(long endAt) {
				if (curGame == null) {
					return;
				}

				long s = Math.Max(curStart, startEpoch);
				long e = Math.Min(endAt, endEpoch);

				if (e > s) {
					sessions.Add(new GameSession(
						steamId,
						name,
						curGame,
						DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime,
						DateTimeOffset.FromUnixTimeSeconds(e).LocalDateTime,
						(e - s) / 60.0,
						curRp));
				}

				curGame = null;
			}

			foreach (SqliteEventStore.TimelineRow row in group) {
				if (row.Name.Length > 0) {
					name = row.Name; // 时间线按时间升序，最后一次覆盖为最新昵称
					names[steamId] = name;
				}

				// 在线 / 游戏时长
				long segStart = Math.Max(row.Epoch, startEpoch);
				long segEnd = Math.Min(row.NextEpoch, endEpoch);

				if (segEnd > segStart) {
					double minutes = (segEnd - segStart) / 60.0;

					if (!string.IsNullOrEmpty(row.Game)) {
						inGameMinutes[steamId] = inGameMinutes.GetValueOrDefault(steamId) + minutes;
					}

					if (!OfflineStatuses.Contains(row.Status)) {
						onlineMinutes[steamId] = onlineMinutes.GetValueOrDefault(steamId) + minutes;
					}
				}

				// 游戏会话切段
				if (!string.IsNullOrEmpty(row.Game)) {
					string key = CleanGameKey(row.Game);

					if (curGame == null) {
						curGame = key;
						curStart = row.Epoch;
						curRp = row.RichPresence;
					} else if (curGame != key) {
						CloseSession(row.Epoch);
						curGame = key;
						curStart = row.Epoch;
						curRp = row.RichPresence;
					} else if (row.RichPresence != null) {
						curRp = row.RichPresence;
					}
				} else {
					CloseSession(row.Epoch);
				}

				// 富状态明细
				if (row.EventType == "rich_presence" && row.RichPresence != null && row.Epoch >= startEpoch && row.Epoch < endEpoch) {
					details.Add(new RichPresenceEvent(steamId, name, DateTimeOffset.FromUnixTimeSeconds(row.Epoch).LocalDateTime, row.RichPresence));
				}
			}

			CloseSession(endEpoch);
		}

		var games = sessions
			.GroupBy(static s => s.Game)
			.Select(static g => new GameStat(g.Key, g.Select(static x => x.SteamId).Distinct().Count(), g.Sum(static x => x.Minutes)))
			.OrderByDescending(static g => g.Minutes)
			.ToList();

		var friends = names.Keys
			.Select(id => new FriendStat(names[id], id, onlineMinutes.GetValueOrDefault(id), inGameMinutes.GetValueOrDefault(id)))
			.Where(static f => f.OnlineMinutes > 0 || f.GameMinutes > 0)
			.OrderByDescending(static f => f.OnlineMinutes)
			.ToList();

		sessions.Sort(static (a, b) => b.Start.CompareTo(a.Start));
		details.Sort(static (a, b) => a.Time.CompareTo(b.Time));

		return (games, friends, sessions, details);
	}

	// 统计用的游戏名归一化：去掉尾巴上可能混入的富状态括号（如 "CS2 (DUST2 6:2)" → "CS2"）
	static string CleanGameKey(string game) => Regex.Replace(game, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
}
