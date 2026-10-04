using SteamFriendMonitor.Core;

namespace SteamFriendMonitor.UI;

// 统计窗口：按 今天 / 本周 / 本月 / 本年、可选按好友筛选，
// 查看游戏排行、好友在线时长、游戏记录（每段游戏会话 + 期间的富状态变化明细）
internal sealed class StatsWindow : Form {
	readonly SqliteEventStore store;

	ComboBox cboPeriod = null!;
	ComboBox cboFriend = null!;
	Label lblSummary = null!;
	ListView lvGames = null!;
	ListView lvFriends = null!;
	ListView lvSessions = null!;
	ListView lvDetails = null!;

	List<GameStat> cachedGames = [];
	List<FriendStat> cachedFriends = [];
	List<GameSession> cachedSessions = [];
	List<RichPresenceEvent> cachedDetails = [];
	ulong? friendFilter;
	bool rebuildingFriendCombo;

	public StatsWindow(string dbPath) {
		store = new SqliteEventStore(dbPath);
		store.Initialize();

		BuildUi();
		Load += (_, _) => RefreshStats();
		FormClosed += (_, _) => store.Dispose();
	}

	void BuildUi() {
		Text = "好友统计";
		Size = new Size(920, 660);
		MinimumSize = new Size(720, 500);
		StartPosition = FormStartPosition.CenterParent;
		Font = new Font("Microsoft YaHei UI", 9F);
		Icon = Owner?.Icon;

		var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 9, 12, 0) };
		var lblPeriod = new Label { Text = "统计周期:", Location = new Point(12, 12), AutoSize = true };
		cboPeriod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(80, 8), Width = 90 };
		cboPeriod.Items.AddRange(["今天", "本周", "本月", "本年"]);
		cboPeriod.SelectedIndex = 0;
		cboPeriod.SelectedIndexChanged += (_, _) => RefreshStats();

		var lblFriend = new Label { Text = "好友筛选:", Location = new Point(184, 12), AutoSize = true };
		cboFriend = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(252, 8), Width = 180 };
		cboFriend.Items.Add(new FriendFilterItem("全部好友", null));
		cboFriend.SelectedIndex = 0;
		cboFriend.SelectedIndexChanged += (_, _) => {
			if (!rebuildingFriendCombo) {
				friendFilter = ((FriendFilterItem)cboFriend.SelectedItem!).Id;
				RenderAll();
			}
		};

		lblSummary = new Label { Text = string.Empty, Location = new Point(446, 12), AutoSize = true, ForeColor = Color.DimGray };
		top.Controls.AddRange([lblPeriod, cboPeriod, lblFriend, cboFriend, lblSummary]);

		var tabs = new TabControl { Dock = DockStyle.Fill };

		var tabGames = new TabPage("游戏排行（好友都在玩什么）");
		lvGames = BuildList([("排名", 60), ("游戏", 340), ("在玩好友数", 110), ("累计时长", 150)]);
		tabGames.Controls.Add(lvGames);

		var tabFriends = new TabPage("好友在线时长");
		lvFriends = BuildList([("排名", 60), ("好友", 220), ("SteamID", 150), ("在线总时长", 130), ("其中游戏中", 120)]);
		tabFriends.Controls.Add(lvFriends);

		var tabSessions = new TabPage("游戏记录（游戏中发生了什么）");
		lvSessions = BuildList([("好友", 130), ("游戏", 190), ("开始", 115), ("结束", 115), ("时长", 90), ("最后的富状态", 220)]);
		lvSessions.SelectedIndexChanged += (_, _) => RenderSessionDetails();

		lvDetails = BuildList([("时间", 90), ("富状态变化（比分 / 地图等）", 520)]);

		var split = new SplitContainer {
			Dock = DockStyle.Fill,
			Orientation = Orientation.Vertical,
			SplitterWidth = 6
		};
		split.Panel1.BackColor = SystemColors.Control;
		split.Panel1.Controls.Add(lvSessions);
		split.Panel2.Controls.Add(lvDetails);
		tabSessions.Controls.Add(split);

		tabs.TabPages.AddRange([tabGames, tabFriends, tabSessions]);

		Controls.Add(tabs);
		Controls.Add(top);
	}

	static ListView BuildList((string Header, int Width)[] columns) {
		var lv = new ListView {
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			MultiSelect = false,
			HideSelection = true
		};

		foreach ((string header, int width) in columns) {
			lv.Columns.Add(header, width);
		}

		return lv;
	}

	void RefreshStats() {
		DateTime now = DateTime.Now;
		DateTime start = cboPeriod.SelectedIndex switch {
			1 => now.Date.AddDays(-(((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7)), // 本周从周一起
			2 => new DateTime(now.Year, now.Month, 1),
			3 => new DateTime(now.Year, 1, 1),
			_ => now.Date
		};

		lvGames.Items.Clear();
		lvFriends.Items.Clear();
		lvSessions.Items.Clear();
		lvDetails.Items.Clear();

		try {
			(cachedGames, cachedFriends, cachedSessions, cachedDetails) = StatsCalculator.Compute(store, start, now);
			RebuildFriendCombo();
			RenderAll();
			lblSummary.Text = $"周期：{start:yyyy-MM-dd HH:mm} 起 · {cachedGames.Count} 款游戏 / {cachedFriends.Count} 位好友 / {cachedSessions.Count} 段游戏记录";
		} catch (Exception e) {
			lblSummary.Text = $"统计失败: {e.Message}";
		}
	}

	void RebuildFriendCombo() {
		rebuildingFriendCombo = true;

		cboFriend.Items.Clear();
		cboFriend.Items.Add(new FriendFilterItem("全部好友", null));

		foreach (FriendStat friend in cachedFriends) {
			cboFriend.Items.Add(new FriendFilterItem(friend.Name, friend.SteamId));
		}

		int index = cachedFriends.FindIndex(f => f.SteamId == friendFilter);
		cboFriend.SelectedIndex = index >= 0 ? index + 1 : 0;

		if (index < 0) {
			friendFilter = null;
		}

		rebuildingFriendCombo = false;
	}

	void RenderAll() {
		RenderGames();
		RenderFriends();
		RenderSessions();
	}

	void RenderGames() {
		lvGames.Items.Clear();

		List<GameStat> games = friendFilter == null
			? cachedGames
			: cachedSessions.Where(s => s.SteamId == friendFilter)
				.GroupBy(static s => s.Game)
				.Select(static g => new GameStat(g.Key, g.Select(static x => x.SteamId).Distinct().Count(), g.Sum(static x => x.Minutes)))
				.OrderByDescending(static g => g.Minutes)
				.ToList();

		int rank = 1;

		foreach (GameStat game in games.Take(100)) {
			lvGames.Items.Add(new ListViewItem([
				(rank++).ToString(),
				game.Game,
				game.FriendCount.ToString(),
				FormatMinutes(game.Minutes)
			]));
		}

		if (games.Count == 0) {
			lvGames.Items.Add(new ListViewItem(["—", "该范围内还没有游戏记录", "", ""]) { ForeColor = Color.Gray });
		}
	}

	void RenderFriends() {
		lvFriends.Items.Clear();

		var friends = friendFilter == null ? cachedFriends : cachedFriends.Where(f => f.SteamId == friendFilter).ToList();
		int rank = 1;

		foreach (FriendStat friend in friends.Take(100)) {
			lvFriends.Items.Add(new ListViewItem([
				(rank++).ToString(),
				friend.Name,
				friend.SteamId.ToString(),
				FormatMinutes(friend.OnlineMinutes),
				FormatMinutes(friend.GameMinutes)
			]));
		}

		if (friends.Count == 0) {
			lvFriends.Items.Add(new ListViewItem(["—", "该范围内还没有在线记录", "", "", ""]) { ForeColor = Color.Gray });
		}
	}

	void RenderSessions() {
		lvSessions.Items.Clear();

		var sessions = friendFilter == null ? cachedSessions : cachedSessions.Where(s => s.SteamId == friendFilter).ToList();

		foreach (GameSession session in sessions.Take(200)) {
			var item = new ListViewItem([
				session.Name,
				session.Game,
				session.Start.ToString("MM-dd HH:mm"),
				session.End.ToString("MM-dd HH:mm"),
				FormatMinutes(session.Minutes),
				session.LastRichPresence ?? string.Empty
			]) {
				Tag = session
			};
			lvSessions.Items.Add(item);
		}

		if (sessions.Count == 0) {
			lvSessions.Items.Add(new ListViewItem(["—", "该范围内还没有游戏记录", "", "", "", ""]) { ForeColor = Color.Gray });
		}

		RenderSessionDetails();
	}

	void RenderSessionDetails() {
		lvDetails.Items.Clear();

		if (lvSessions.SelectedItems.Count == 0 || lvSessions.SelectedItems[0].Tag is not GameSession session) {
			return;
		}

		var details = cachedDetails
			.Where(d => d.SteamId == session.SteamId && d.Time >= session.Start && d.Time <= session.End)
			.ToList();

		if (details.Count == 0) {
			lvDetails.Items.Add(new ListViewItem(["—", "该时段没有富状态变化记录"]) { ForeColor = Color.Gray });
			return;
		}

		foreach (RichPresenceEvent detail in details) {
			lvDetails.Items.Add(new ListViewItem([
				detail.Time.ToString("HH:mm:ss"),
				detail.RichPresence
			]));
		}
	}

	static string FormatMinutes(double minutes) {
		int total = (int)Math.Round(minutes);

		if (total < 1) {
			return "—";
		}

		if (total < 60) {
			return $"{total} 分钟";
		}

		int hours = total / 60;
		int mins = total % 60;

		if (hours < 24) {
			return mins > 0 ? $"{hours} 小时 {mins} 分" : $"{hours} 小时";
		}

		int days = hours / 24;
		hours %= 24;

		return hours > 0 ? $"{days} 天 {hours} 小时" : $"{days} 天";
	}
}

// 好友筛选下拉项
internal sealed class FriendFilterItem(string label, ulong? id) {
	public ulong? Id { get; } = id;

	public override string ToString() => label;
}
