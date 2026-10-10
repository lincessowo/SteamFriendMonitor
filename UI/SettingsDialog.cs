// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using SteamFriendMonitor.Core;

namespace SteamFriendMonitor.UI;

internal sealed class SettingsDialog : Form {
	readonly AppConfig config;

	TextBox txtUsername = null!;
	TextBox txtPassword = null!;
	TextBox txtSharedSecret = null!;
	TextBox txtLogDir = null!;
	TextBox txtProxyHost = null!;
	TextBox txtProxyPort = null!;
	TextBox txtProxyUser = null!;
	TextBox txtProxyPassword = null!;
	TextBox txtWsPort = null!;
	TextBox txtWsHost = null!;
	TextBox txtWsToken = null!;
	ComboBox cboProtocol = null!;
	ComboBox cboProxyType = null!;
	CheckBox chkSnapshot = null!;
	CheckBox chkNotify = null!;
	CheckBox chkAutoStart = null!;
	CheckBox chkWs = null!;

	public SettingsDialog(AppConfig config) {
		this.config = config;
		BuildUi();
		LoadValues();
	}

	void BuildUi() {
		Text = "设置";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(600, 640);
		Font = new Font("Microsoft YaHei UI", 9F);

		int row = 0;
		var table = new TableLayoutPanel {
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			Padding = new Padding(14, 14, 14, 4)
		};
		table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
		table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

		void AddRow(string label, Control control) {
			table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
			table.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) }, 0, row);
			control.Dock = DockStyle.Fill;
			control.Margin = new Padding(3, 4, 14, 4);
			table.Controls.Add(control, 1, row);
			row++;
		}

		txtUsername = new TextBox();
		AddRow("Steam 用户名", txtUsername);

		txtPassword = new TextBox { UseSystemPasswordChar = true };
		AddRow("Steam 密码", txtPassword);

		txtSharedSecret = new TextBox { UseSystemPasswordChar = true };
		AddRow("令牌密钥", txtSharedSecret);

		cboProtocol = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
		cboProtocol.Items.AddRange(["All（推荐）", "Tcp", "WebSocket"]);
		AddRow("连接协议", cboProtocol);

		cboProxyType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
		cboProxyType.Items.AddRange(["不使用代理", "HTTP 代理", "SOCKS5 代理"]);
		AddRow("代理类型", cboProxyType);

		txtProxyHost = new TextBox();
		AddRow("代理地址", txtProxyHost);

		txtProxyPort = new TextBox();
		AddRow("代理端口", txtProxyPort);

		txtProxyUser = new TextBox();
		AddRow("代理用户名", txtProxyUser);

		txtProxyPassword = new TextBox { UseSystemPasswordChar = true };
		AddRow("代理密码", txtProxyPassword);

		txtLogDir = new TextBox();
		AddRow("日志目录", txtLogDir);

		// WebSocket 接口：默认关闭，仅本机可连；绑定其他地址需要倒计时确认
		chkWs = new CheckBox { Text = "启用（默认关闭）", AutoSize = true, Margin = new Padding(3, 9, 12, 3) };
		txtWsPort = new TextBox { Width = 58 };
		txtWsHost = new TextBox { Width = 108 };
		var wsPanel = new FlowLayoutPanel {
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Dock = DockStyle.Fill,
			Margin = new Padding(0)
		};
		wsPanel.Controls.AddRange([
			chkWs,
			new Label { Text = "端口", AutoSize = true, Margin = new Padding(8, 10, 4, 0) },
			txtWsPort,
			new Label { Text = "绑定", AutoSize = true, Margin = new Padding(10, 10, 4, 0) },
			txtWsHost
		]);
		AddRow("WS 接口", wsPanel);

		txtWsToken = new TextBox { ReadOnly = true };
		AddRow("WS 令牌", txtWsToken);

		// 选项行：三个复选框并排
		chkSnapshot = new CheckBox { Text = "记录启动快照", AutoSize = true, Margin = new Padding(3, 10, 16, 3) };
		chkNotify = new CheckBox { Text = "好友上线气泡提醒", AutoSize = true, Margin = new Padding(3, 10, 16, 3) };
		chkAutoStart = new CheckBox { Text = "开机自启动", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
		var checks = new FlowLayoutPanel {
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Dock = DockStyle.Fill,
			Margin = new Padding(0)
		};
		checks.Controls.AddRange([chkSnapshot, chkNotify, chkAutoStart]);
		table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
		table.Controls.Add(new Label(), 0, row);
		table.Controls.Add(checks, 1, row);
		row++;

		// 底部说明（固定高度面板，避免被截断）
		var hintPanel = new Panel { Dock = DockStyle.Bottom, Height = 108, Padding = new Padding(18, 6, 18, 4) };
		var hint = new Label {
			Dock = DockStyle.Fill,
			Text = "说明：\r\n· 令牌密钥 = 手机令牌 shared_secret（.maFile 中的字段），填写后登录自动过 SteamGuard\r\n· 启用代理时：认证请求与 CM 连接（自动切换为 WebSocket 隧道）都经代理出去\r\n· 首次登录需要密码；之后登录令牌保存在本地，密码可留空；配置保存在 exe 旁的 config.json",
			ForeColor = Color.DimGray
		};
		hintPanel.Controls.Add(hint);

		var buttons = new FlowLayoutPanel {
			FlowDirection = FlowDirection.RightToLeft,
			Dock = DockStyle.Bottom,
			Height = 48,
			Padding = new Padding(6)
		};
		var btnSave = new Button { Text = "保存", Width = 88 };
		var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
		btnSave.Click += (_, _) => {
			if (cboProxyType.SelectedIndex > 0 && (string.IsNullOrWhiteSpace(txtProxyHost.Text) || !int.TryParse(txtProxyPort.Text.Trim(), out int port) || port is < 1 or > 65535)) {
				MessageBox.Show(this, "启用代理时请填写有效的代理地址和端口（1-65535）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			if (chkWs.Checked) {
				if (!int.TryParse(txtWsPort.Text.Trim(), out int wsPort) || wsPort is < 1 or > 65535) {
					MessageBox.Show(this, "启用 WS 接口时请填写有效的端口（1-65535）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}

				if (!IsLoopbackHost(txtWsHost.Text.Trim())) {
					if (string.IsNullOrWhiteSpace(txtWsToken.Text)) {
						txtWsToken.Text = Guid.NewGuid().ToString("N");
					}

					// 绑定非 127.0.0.1 地址：强制 10 秒倒计时确认
					if (!CountdownConfirmDialog.Show(this, txtWsHost.Text.Trim(), txtWsToken.Text.Trim())) {
						return;
					}
				}
			}

			if (ApplyToConfig()) {
				DialogResult = DialogResult.OK;
			}
		};
		buttons.Controls.Add(btnCancel);
		buttons.Controls.Add(btnSave);
		AcceptButton = btnSave;
		CancelButton = btnCancel;

		// Dock 顺序：后加入的先贴边 —— 按钮在最底，说明在它上面，表格填充其余
		Controls.Add(table);
		Controls.Add(hintPanel);
		Controls.Add(buttons);
	}

	void LoadValues() {
		txtUsername.Text = config.Username;
		txtPassword.Text = config.Password;
		txtSharedSecret.Text = config.SharedSecret;
		txtLogDir.Text = config.LogDirectory;
		txtProxyHost.Text = config.ProxyHost;
		txtProxyPort.Text = config.ProxyPort > 0 ? config.ProxyPort.ToString() : string.Empty;
		txtProxyUser.Text = config.ProxyUsername;
		txtProxyPassword.Text = config.ProxyPassword;
		txtWsToken.Text = config.WsToken;
		chkWs.Checked = config.WsEnabled;
		txtWsPort.Text = config.WsPort > 0 ? config.WsPort.ToString() : "8765";
		txtWsHost.Text = config.WsHost;
		chkSnapshot.Checked = config.LogInitialSnapshot;
		chkNotify.Checked = config.NotifyOnFriendOnline;
		chkAutoStart.Checked = AutoStart.IsEnabled();

		cboProtocol.SelectedIndex = config.Protocol.Trim().ToUpperInvariant() switch {
			"TCP" => 1,
			"WEBSOCKET" => 2,
			_ => 0
		};

		cboProxyType.SelectedIndex = config.ProxyType.ToUpperInvariant() switch {
			"HTTP" => 1,
			"SOCKS5" => 2,
			_ => 0
		};
	}

	static bool IsLoopbackHost(string host) => host is "" or "127.0.0.1" or "localhost" or "::1";

	bool ApplyToConfig() {
		config.Username = txtUsername.Text.Trim();
		config.Password = txtPassword.Text;
		config.SharedSecret = txtSharedSecret.Text.Trim();
		config.Protocol = cboProtocol.SelectedIndex switch {
			1 => "Tcp",
			2 => "WebSocket",
			_ => "All"
		};
		config.ProxyType = cboProxyType.SelectedIndex switch {
			1 => "Http",
			2 => "Socks5",
			_ => "None"
		};
		config.ProxyHost = txtProxyHost.Text.Trim();
		config.ProxyPort = int.TryParse(txtProxyPort.Text.Trim(), out int port) ? port : 0;
		config.ProxyUsername = txtProxyUser.Text.Trim();
		config.ProxyPassword = txtProxyPassword.Text;
		config.LogDirectory = string.IsNullOrWhiteSpace(txtLogDir.Text) ? "logs" : txtLogDir.Text.Trim();
		config.LogInitialSnapshot = chkSnapshot.Checked;
		config.NotifyOnFriendOnline = chkNotify.Checked;
		config.WsEnabled = chkWs.Checked;
		config.WsPort = int.TryParse(txtWsPort.Text.Trim(), out int wsPort) ? wsPort : 8765;
		config.WsHost = string.IsNullOrWhiteSpace(txtWsHost.Text) ? "127.0.0.1" : txtWsHost.Text.Trim();
		config.WsToken = txtWsToken.Text.Trim();

		string? autoStartError = AutoStart.SetEnabled(chkAutoStart.Checked);

		if (autoStartError != null) {
			MessageBox.Show(this, autoStartError, "开机自启动设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return false;
		}

		return true;
	}
}
