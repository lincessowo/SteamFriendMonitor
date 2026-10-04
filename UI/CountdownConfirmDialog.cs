namespace SteamFriendMonitor.UI;

// 非本机绑定 WebSocket 接口前的强制倒计时确认（10 秒内无法确认，防手滑）
internal sealed class CountdownConfirmDialog : Form {
	public CountdownConfirmDialog(string bindHost, string token) {
		Text = "安全确认";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(540, 300);
		Font = new Font("Microsoft YaHei UI", 9F);

		var lbl = new Label {
			Dock = DockStyle.Fill,
			Text = $"""
				⚠ 安全警告

				你正在把 WebSocket 接口绑定到非 127.0.0.1 地址：{bindHost}

				这意味着同一网络（甚至公网，取决于网络环境）内的任何程序
				都可以访问你的好友数据。客户端连接时必须携带访问令牌：

				    ?token={token}

				请确认你了解并接受此风险。
				""",
			Padding = new Padding(20, 16, 20, 0),
			ForeColor = Color.FromArgb(150, 60, 0)
		};

		var buttons = new FlowLayoutPanel {
			FlowDirection = FlowDirection.RightToLeft,
			Dock = DockStyle.Bottom,
			Height = 52,
			Padding = new Padding(6)
		};

		var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
		var btnOk = new Button { Text = "确认启用（10）", Width = 128, Enabled = false };

		int remaining = 10;
		var timer = new System.Windows.Forms.Timer { Interval = 1000 };

		timer.Tick += (_, _) => {
			remaining--;

			if (remaining <= 0) {
				timer.Stop();
				btnOk.Enabled = true;
				btnOk.Text = "确认启用";
			} else {
				btnOk.Text = $"确认启用（{remaining}）";
			}
		};
		timer.Start();

		btnOk.Click += (_, _) => DialogResult = DialogResult.OK;

		buttons.Controls.Add(btnCancel);
		buttons.Controls.Add(btnOk);
		AcceptButton = null;
		CancelButton = btnCancel;

		Controls.Add(lbl);
		Controls.Add(buttons);
	}

	public static bool Show(IWin32Window? owner, string bindHost, string token) {
		using var dialog = new CountdownConfirmDialog(bindHost, token);
		return dialog.ShowDialog(owner) == DialogResult.OK;
	}
}
