// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Data.Sqlite;

namespace SteamFriendMonitor.Core;

// 好友动态的 SQLite 存储（data source: friends.db），与 txt 日志并行写入
public sealed class SqliteEventStore : IDisposable {
	readonly object gate = new();
	readonly string dbPath;
	SqliteConnection? connection;

	public SqliteEventStore(string dbPath) {
		ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
		this.dbPath = Path.GetFullPath(dbPath);
		Directory.CreateDirectory(Path.GetDirectoryName(this.dbPath)!);
	}

	public void Initialize() {
		lock (gate) {
			if (connection != null) {
				return; // 幂等
			}

			connection = new SqliteConnection(new SqliteConnectionStringBuilder {
				DataSource = dbPath,
				Mode = SqliteOpenMode.ReadWriteCreate
			}.ToString());
			connection.Open();

			// WAL 模式：写入更顺滑，也方便用 DB 工具边看边写
			using (var pragma = connection.CreateCommand()) {
				pragma.CommandText = "PRAGMA journal_mode=WAL;";
				pragma.ExecuteNonQuery();
			}

			using (var cmd = connection.CreateCommand()) {
				cmd.CommandText = """
					CREATE TABLE IF NOT EXISTS friend_events (
						id            INTEGER PRIMARY KEY AUTOINCREMENT,
						time          TEXT    NOT NULL,
						epoch         INTEGER NOT NULL,
						steam_id      TEXT    NOT NULL,
						friend_name   TEXT    NOT NULL,
						status        TEXT    NOT NULL,
						persona_state INTEGER NOT NULL,
						game_name     TEXT,
						rich_presence TEXT,
						event_type    TEXT    NOT NULL
					);
					CREATE INDEX IF NOT EXISTS idx_friend_events_steam_id ON friend_events(steam_id);
					CREATE INDEX IF NOT EXISTS idx_friend_events_time     ON friend_events(time);
					""";
				cmd.ExecuteNonQuery();
			}

			// 兼容旧版本数据库：补上 rich_presence 列（已存在则忽略报错）
			using (var alter = connection.CreateCommand()) {
				alter.CommandText = "ALTER TABLE friend_events ADD COLUMN rich_presence TEXT;";

				try {
					alter.ExecuteNonQuery();
				} catch (SqliteException) {
					// 列已存在
				}
			}
		}
	}

	// 数据库异常只吞掉不抛出：不能影响实时监控和 txt 日志
	public void Append(FriendEventRecord record) {
		try {
			lock (gate) {
				if (connection == null) {
					return;
				}

				using var cmd = connection.CreateCommand();
				cmd.CommandText = """
					INSERT INTO friend_events (time, epoch, steam_id, friend_name, status, persona_state, game_name, rich_presence, event_type)
					VALUES ($time, $epoch, $steam_id, $friend_name, $status, $persona_state, $game_name, $rich_presence, $event_type);
					""";
				cmd.Parameters.AddWithValue("$time", record.Time.ToString("yyyy-MM-dd HH:mm:ss"));
				cmd.Parameters.AddWithValue("$epoch", new DateTimeOffset(record.Time).ToUnixTimeSeconds());
				cmd.Parameters.AddWithValue("$steam_id", record.SteamId.ToString());
				cmd.Parameters.AddWithValue("$friend_name", record.Name);
				cmd.Parameters.AddWithValue("$status", record.Status);
				cmd.Parameters.AddWithValue("$persona_state", record.PersonaState);
				cmd.Parameters.AddWithValue("$game_name", (object?)record.GameName ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$rich_presence", (object?)record.RichPresence ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$event_type", record.EventType);
				cmd.ExecuteNonQuery();
			}
		} catch (Exception) {
			// 静默：数据库故障不应中断监控
		}
	}

	public sealed record TimelineRow(ulong SteamId, string Name, string Status, string? Game, string? RichPresence, string EventType, long Epoch, long NextEpoch);

	// 读取 epoch < endEpochExclusive 的全部事件（按好友分组、时间升序），
	// NextEpoch 为该好友下一条事件的时间（没有则取 endEpochExclusive），供时长统计分段
	public IReadOnlyList<TimelineRow> ReadTimeline(long endEpochExclusive) {
		lock (gate) {
			if (connection == null) {
				return [];
			}

			var rows = new List<TimelineRow>();

			try {
				using var cmd = connection.CreateCommand();
				cmd.CommandText = """
					SELECT steam_id, friend_name, status, game_name, rich_presence, event_type, epoch,
					       LEAD(epoch) OVER (PARTITION BY steam_id ORDER BY epoch, id)
					FROM friend_events
					WHERE epoch < $end
					ORDER BY steam_id, epoch, id;
					""";
				cmd.Parameters.AddWithValue("$end", endEpochExclusive);

				using var reader = cmd.ExecuteReader();

				while (reader.Read()) {
					rows.Add(new TimelineRow(
						ulong.Parse(reader.GetString(0)),
						reader.GetString(1),
						reader.GetString(2),
						reader.IsDBNull(3) ? null : reader.GetString(3),
						reader.IsDBNull(4) ? null : reader.GetString(4),
						reader.GetString(5),
						reader.GetInt64(6),
						reader.IsDBNull(7) ? endEpochExclusive : reader.GetInt64(7)));
				}
			} catch (Exception) {
				// 查询失败按空数据处理
			}

			return rows;
		}
	}

	public sealed record LastState(ulong SteamId, string Name, int State, string? Game, string? RichPresence);

	// 每位好友最后一条已知状态（排除 removed），用于跨启动去重：状态没变化就不重复写快照
	public IReadOnlyList<LastState> ReadLatestStates() {
		lock (gate) {
			if (connection == null) {
				return [];
			}

			var rows = new List<LastState>();

			try {
				using var cmd = connection.CreateCommand();
				cmd.CommandText = """
					SELECT steam_id, friend_name, persona_state, game_name, rich_presence
					FROM friend_events
					WHERE id IN (
						SELECT MAX(id) FROM friend_events WHERE event_type != 'removed' GROUP BY steam_id
					);
					""";

				using var reader = cmd.ExecuteReader();

				while (reader.Read()) {
					rows.Add(new LastState(
						ulong.Parse(reader.GetString(0)),
						reader.GetString(1),
						reader.GetInt32(2),
						reader.IsDBNull(3) ? null : reader.GetString(3),
						reader.IsDBNull(4) ? null : reader.GetString(4)));
				}
			} catch (Exception) {
				// 查询失败按空数据处理
			}

			return rows;
		}
	}

	public void Dispose() {
		lock (gate) {
			try {
				connection?.Dispose();
			} catch (Exception) {
				// 忽略
			}

			connection = null;
		}
	}
}
