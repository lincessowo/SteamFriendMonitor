namespace SteamFriendMonitor.UI;

// 登录验证用的轻量输入弹窗（SteamGuard 验证码等）
internal static class InputDialog {
	public static string Show(string title, string label, string? hint = null) {
		string result = string.Empty;

		using var form = new Form {
			Text = title,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			MinimizeBox = false,
			MaximizeBox = false,
			ShowInTaskbar = false,
			StartPosition = FormStartPosition.CenterParent,
			ClientSize = new Size(400, hint == null ? 140 : 172),
			Font = new Font("Microsoft YaHei UI", 9F)
		};

		int buttonY = hint == null ? 96 : 128;

		var lbl = new Label { Text = label, Location = new Point(16, 14), AutoSize = true };
		var txt = new TextBox { Location = new Point(16, 42), Width = 368 };
		var lblHint = hint == null ? null : new Label { Text = hint, Location = new Point(16, 74), AutoSize = true, ForeColor = Color.Firebrick };
		var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(218, buttonY), Width = 80 };
		var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(304, buttonY), Width = 80 };

		form.Controls.Add(lbl);
		form.Controls.Add(txt);
		form.Controls.Add(btnOk);
		form.Controls.Add(btnCancel);

		if (lblHint != null) {
			form.Controls.Add(lblHint);
		}

		form.AcceptButton = btnOk;
		form.CancelButton = btnCancel;

		if (form.ShowDialog() == DialogResult.OK) {
			result = txt.Text.Trim();
		}

		return result;
	}
}
