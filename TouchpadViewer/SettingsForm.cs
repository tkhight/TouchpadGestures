using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Gestures;

namespace TouchpadViewer
{
    // ジェスチャーの割り当てを編集する画面。設定ファイル (gestures.json) の内容を BindingEntry の一覧として編集し、
    // 保存するときに検証して書き戻す。判定や検証そのものは Gestures 側 (GestureConfig / BindingEditing) が持つ
    sealed class SettingsForm : Form
    {
        const string AllApps = "すべて";
        const string GlobalOnly = "(全アプリ共通)";
        static readonly string[] MotionLabels = { "固定", "← 左", "→ 右", "↑ 上", "↓ 下" };
        static readonly SwipeDirection?[] MotionValues =
            { null, SwipeDirection.Left, SwipeDirection.Right, SwipeDirection.Up, SwipeDirection.Down };

        // しきい値の欄。空欄 = 既定値。種類ごとに、使える欄だけを出す
        sealed class Threshold
        {
            public string Label;
            public string Default;
            public bool IsInteger;
            public GestureKind[] Kinds;
            public Func<BindingEntry, object> Get;
            public Action<BindingEntry, double?> Set;
            public Label LabelControl;
            public TextBox Box;
        }

        readonly string path;
        readonly List<BindingEntry> entries;
        BindingEntry cur;
        bool loading;
        bool capturing;

        readonly ComboBox cboFilter = new ComboBox();
        readonly ListView list = new ListView();
        readonly Panel editor = new Panel();

        readonly TextBox txtName = new TextBox();
        readonly ComboBox cboApp = new ComboBox();
        readonly NumericUpDown numFingers = new NumericUpDown();
        readonly CheckBox chkEnabled = new CheckBox();
        readonly CheckBox chkProtected = new CheckBox();
        readonly CheckBox chkUseMods = new CheckBox();
        readonly CheckBox chkCtrl = new CheckBox();
        readonly CheckBox chkAlt = new CheckBox();
        readonly CheckBox chkShift = new CheckBox();
        readonly CheckBox chkWin = new CheckBox();
        readonly RadioButton rbSwipe = new RadioButton();
        readonly RadioButton rbTap = new RadioButton();
        readonly RadioButton rbPinch = new RadioButton();
        readonly RadioButton rbLand = new RadioButton();
        readonly RadioButton rbMulti = new RadioButton();
        readonly GroupBox grpMulti = new GroupBox();
        readonly NumericUpDown numMultiCount = new NumericUpDown();
        readonly GroupBox grpSwipe = new GroupBox();
        readonly FlowLayoutPanel fingerRow = new FlowLayoutPanel();
        readonly List<ComboBox> fingerCombos = new List<ComboBox>();
        readonly GroupBox grpTap = new GroupBox();
        readonly ComboBox cboTapSlot = new ComboBox();
        readonly NumericUpDown numTapCount = new NumericUpDown();
        readonly GroupBox grpPinch = new GroupBox();
        readonly GroupBox grpLand = new GroupBox();
        readonly FlowLayoutPanel landRow = new FlowLayoutPanel();
        readonly List<ComboBox> landCombos = new List<ComboBox>();
        readonly ComboBox cboPinch = new ComboBox();
        readonly RadioButton rbHotkey = new RadioButton();
        readonly RadioButton rbWheel = new RadioButton();
        readonly RadioButton rbClick = new RadioButton();
        readonly RadioButton rbMove = new RadioButton();
        readonly RadioButton rbResize = new RadioButton();
        readonly RadioButton rbRun = new RadioButton();
        readonly TextBox txtRun = new TextBox();
        readonly TextBox txtArgs = new TextBox();
        readonly Button btnBrowseRun = new Button();
        readonly NumericUpDown numMoveSpeed = new NumericUpDown();
        readonly ComboBox cboClick = new ComboBox();
        readonly NumericUpDown numClickCount = new NumericUpDown();
        readonly CheckBox chkHold = new CheckBox();
        readonly CheckBox chkFollow = new CheckBox();
        readonly NumericUpDown numSpeed = new NumericUpDown();
        readonly TextBox txtHotkey = new TextBox();
        readonly Button btnCapture = new Button();
        readonly NumericUpDown numWheel = new NumericUpDown();
        readonly CheckBox chkHorizontal = new CheckBox();
        readonly GroupBox grpAdvanced = new GroupBox();
        readonly Label lblError = new Label();
        readonly List<Threshold> thresholds = new List<Threshold>();

        readonly string stateDir;

        // ジェスチャーを実行しないアプリ。ファイルの "ignoreApps" と対応し、「除外アプリ...」で編集する
        List<string> ignoreApps = new List<string>();
        bool ignoreChanged;

        // 開いたときの割り当て (編集前) と、編集用の複製との対応。「閉じる」で並びだけを保存するのに使う
        readonly List<BindingEntry> original;
        readonly Dictionary<BindingEntry, BindingEntry> originalOf = new Dictionary<BindingEntry, BindingEntry>();

        /// <param name="stateDir">最後に選んだ割り当てを覚えておく場所 (exe のフォルダ)。null なら覚えない。</param>
        public SettingsForm(string path, List<BindingEntry> source, string stateDir = null)
        {
            this.path = path;
            this.stateDir = stateDir;
            try { ignoreApps = GestureConfig.LoadIgnoreApps(path); }
            catch (Exception) { }
            original = source.ToList();
            entries = original.Select(e => e.Clone()).ToList();
            for (int i = 0; i < original.Count; i++)
            {
                originalOf[entries[i]] = original[i];
                baseline[entries[i]] = GestureConfig.ToJson(new[] { entries[i] });
            }

            Text = "ジェスチャーの設定";
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(960, 700);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildThresholds();
            BuildLeft();
            BuildEditor();
            BuildBottom();
            RefillFilter(AllApps);
            RefillList(RememberedEntry());
            UpdateEditorEnabled();
        }

        // ---- 最後に選んだ割り当てを覚える ----

        static string KeyOf(BindingEntry e)
        {
            return (AppOf(e) ?? "") + "|" + BindingEditing.ModifiersLabel(e) + BindingEditing.DisplayName(e);
        }

        BindingEntry RememberedEntry()
        {
            if (stateDir == null) return null;
            int? i = SelectionMemory.Find(stateDir, path, entries.Select(KeyOf).ToList());
            return i == null ? null : entries[i.Value];
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (stateDir == null || cur == null) return;
            int i = entries.IndexOf(cur);
            if (i >= 0) SelectionMemory.Save(stateDir, path, i, KeyOf(cur));
        }

        // ---- 画面の組み立て ----

        void BuildLeft()
        {
            Panel left = new Panel { Dock = DockStyle.Left, Width = 420, Padding = new Padding(10, 10, 5, 0) };

            Panel top = new Panel { Dock = DockStyle.Top, Height = 30 };
            top.Controls.Add(new Label { Text = "アプリで絞り込み", AutoSize = true, Location = new Point(0, 6) });
            cboFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFilter.Location = new Point(120, 2);
            cboFilter.Width = 255;
            cboFilter.SelectedIndexChanged += delegate { if (!loading) RefillList(cur); };
            top.Controls.Add(cboFilter);

            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.GridLines = true;
            list.Dock = DockStyle.Fill;
            list.ShowItemToolTips = true;
            list.Columns.Add("アプリ", 70);
            list.Columns.Add("ジェスチャー", 150);
            list.Columns.Add("動作", 110);
            list.Columns.Add("保存", 60);
            list.SelectedIndexChanged += delegate { OnListSelected(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            Button add = new Button { Text = "追加", Width = 80 };
            add.Click += delegate { AddEntry(false); };
            Button copy = new Button { Text = "複製", Width = 80 };
            copy.Click += delegate { AddEntry(true); };
            Button del = new Button { Text = "削除", Width = 80 };
            del.Click += delegate { DeleteEntry(); };
            Button up = new Button { Text = "↑", Width = 40 };
            up.Click += delegate { MoveEntry(-1); };
            Button down = new Button { Text = "↓", Width = 40 };
            down.Click += delegate { MoveEntry(1); };
            buttons.Controls.AddRange(new Control[] { add, copy, del, up, down });

            left.Controls.Add(list);
            left.Controls.Add(buttons);
            left.Controls.Add(top);
            Controls.Add(left);
        }

        void BuildEditor()
        {
            editor.Dock = DockStyle.Fill;
            editor.AutoScroll = true;
            editor.Padding = new Padding(5, 10, 10, 0);

            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };

            // 基本: 名前・アプリ・指の数・種類
            Panel basic = new Panel { Width = 520, Height = 164 };
            basic.Controls.Add(new Label { Text = "名前", AutoSize = true, Location = new Point(0, 8) });
            txtName.Location = new Point(90, 4);
            txtName.Width = 300;
            basic.Controls.Add(txtName);
            basic.Controls.Add(new Label { Text = "(空欄なら自動)", AutoSize = true, Location = new Point(396, 8) });

            basic.Controls.Add(new Label { Text = "アプリ", AutoSize = true, Location = new Point(0, 40) });
            cboApp.Location = new Point(90, 36);
            cboApp.Width = 200;
            cboApp.DropDownStyle = ComboBoxStyle.DropDown;
            basic.Controls.Add(cboApp);
            basic.Controls.Add(new Label { Text = "空欄 = 全アプリ共通", AutoSize = true, Location = new Point(296, 40) });

            basic.Controls.Add(new Label { Text = "指の数", AutoSize = true, Location = new Point(0, 72) });
            numFingers.Location = new Point(90, 68);
            numFingers.Width = 60;
            numFingers.Minimum = 1;
            numFingers.Maximum = 10;
            numFingers.Value = 3;
            basic.Controls.Add(numFingers);
            chkEnabled.Text = "有効 (オフで一時的に無効)";
            chkEnabled.AutoSize = true;
            chkEnabled.Checked = true;
            chkEnabled.Location = new Point(170, 70);
            basic.Controls.Add(chkEnabled);
            chkProtected.Text = "保護 (削除できない)";
            chkProtected.AutoSize = true;
            chkProtected.Location = new Point(340, 70);
            basic.Controls.Add(chkProtected);

            basic.Controls.Add(new Label { Text = "種類", AutoSize = true, Location = new Point(0, 102) });
            rbSwipe.Text = "スワイプ"; rbSwipe.AutoSize = true; rbSwipe.Location = new Point(90, 100);
            rbTap.Text = "タップ"; rbTap.AutoSize = true; rbTap.Location = new Point(165, 100);
            rbPinch.Text = "ピンチ"; rbPinch.AutoSize = true; rbPinch.Location = new Point(240, 100);
            rbLand.Text = "着地順"; rbLand.AutoSize = true; rbLand.Location = new Point(315, 100);
            rbMulti.Text = "同時タップ"; rbMulti.AutoSize = true; rbMulti.Location = new Point(395, 100);
            rbSwipe.Checked = true;
            basic.Controls.AddRange(new Control[] { rbSwipe, rbTap, rbPinch, rbLand, rbMulti });

            chkUseMods.Text = "修飾キーを押しながら";
            chkUseMods.AutoSize = true;
            chkUseMods.Location = new Point(0, 134);
            chkCtrl.Text = "Ctrl"; chkCtrl.AutoSize = true; chkCtrl.Location = new Point(170, 134);
            chkAlt.Text = "Alt"; chkAlt.AutoSize = true; chkAlt.Location = new Point(228, 134);
            chkShift.Text = "Shift"; chkShift.AutoSize = true; chkShift.Location = new Point(280, 134);
            chkWin.Text = "Win"; chkWin.AutoSize = true; chkWin.Location = new Point(342, 134);
            Label modsNote = new Label { Text = "(ちょうど一致)", AutoSize = true, Location = new Point(396, 136) };
            basic.Controls.AddRange(new Control[] { chkUseMods, chkCtrl, chkAlt, chkShift, chkWin, modsNote });
            flow.Controls.Add(basic);

            // スワイプ: 指ごとの動き
            grpSwipe.Text = "各指の動き (左の指から順)";
            grpSwipe.Width = 520;
            grpSwipe.Height = 105;
            fingerRow.Location = new Point(10, 22);
            fingerRow.Size = new Size(500, 76);
            fingerRow.WrapContents = true;
            grpSwipe.Controls.Add(fingerRow);
            flow.Controls.Add(grpSwipe);

            // タップ
            grpTap.Text = "タップ";
            grpTap.Width = 520;
            grpTap.Height = 70;
            grpTap.Controls.Add(new Label { Text = "タップする指", AutoSize = true, Location = new Point(10, 30) });
            cboTapSlot.DropDownStyle = ComboBoxStyle.DropDownList;
            cboTapSlot.Location = new Point(100, 26);
            cboTapSlot.Width = 110;
            grpTap.Controls.Add(cboTapSlot);
            grpTap.Controls.Add(new Label { Text = "回数", AutoSize = true, Location = new Point(240, 30) });
            numTapCount.Location = new Point(280, 26);
            numTapCount.Width = 55;
            numTapCount.Minimum = 1;
            numTapCount.Maximum = 5;
            grpTap.Controls.Add(numTapCount);
            grpTap.Controls.Add(new Label { Text = "(ほかの指は触れたまま)", AutoSize = true, Location = new Point(350, 30) });
            flow.Controls.Add(grpTap);

            // ピンチ
            grpPinch.Text = "ピンチ";
            grpPinch.Width = 520;
            grpPinch.Height = 70;
            grpPinch.Controls.Add(new Label { Text = "向き", AutoSize = true, Location = new Point(10, 30) });
            cboPinch.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPinch.Items.AddRange(new object[] { "閉じる (In)", "開く (Out)" });
            cboPinch.SelectedIndex = 1;
            cboPinch.Location = new Point(100, 26);
            cboPinch.Width = 130;
            grpPinch.Controls.Add(cboPinch);
            flow.Controls.Add(grpPinch);

            // 同時タップ
            grpMulti.Text = "同時タップ";
            grpMulti.Width = 520;
            grpMulti.Height = 70;
            grpMulti.Controls.Add(new Label { Text = "回数", AutoSize = true, Location = new Point(10, 30) });
            numMultiCount.Location = new Point(50, 26);
            numMultiCount.Width = 55;
            numMultiCount.Minimum = 1;
            numMultiCount.Maximum = 5;
            grpMulti.Controls.Add(numMultiCount);
            grpMulti.Controls.Add(new Label { Text = "(指の数ぶんの指が、ほぼ同時に着地して、ほぼ同時に離れる)", AutoSize = true, Location = new Point(120, 30) });
            flow.Controls.Add(grpMulti);

            // 着地順: 着地した順に、その指が左から何番目か
            grpLand.Text = "着地する順番 (全員が触れたままで、最後の指が着地したときに発火)";
            grpLand.Width = 520;
            grpLand.Height = 105;
            landRow.Location = new Point(10, 22);
            landRow.Size = new Size(500, 76);
            landRow.WrapContents = true;
            grpLand.Controls.Add(landRow);
            flow.Controls.Add(grpLand);

            // 動作
            GroupBox grpAction = new GroupBox { Text = "動作", Width = 520, Height = 358 };
            rbHotkey.Text = "キー"; rbHotkey.AutoSize = true; rbHotkey.Location = new Point(10, 26); rbHotkey.Checked = true;
            txtHotkey.Location = new Point(100, 24);
            txtHotkey.Width = 200;
            btnCapture.Text = "キーを押して入力";
            btnCapture.Location = new Point(308, 22);
            btnCapture.Width = 150;
            btnCapture.Click += delegate { StartCapture(); };
            rbWheel.Text = "ホイール"; rbWheel.AutoSize = true; rbWheel.Location = new Point(10, 62);
            numWheel.Location = new Point(100, 60);
            numWheel.Width = 60;
            numWheel.Minimum = -20;
            numWheel.Maximum = 20;
            numWheel.Value = 1;
            chkHorizontal.Text = "横方向"; chkHorizontal.AutoSize = true; chkHorizontal.Location = new Point(172, 62);
            Label wheelNote = new Label { Text = "1 = 1 ノッチ上/右、負の値 = 下/左", AutoSize = true, Location = new Point(260, 62) };
            rbClick.Text = "クリック"; rbClick.AutoSize = true; rbClick.Location = new Point(10, 98);
            cboClick.DropDownStyle = ComboBoxStyle.DropDownList;
            cboClick.Items.AddRange(new object[] { "左", "右", "中" });
            cboClick.SelectedIndex = 0;
            cboClick.Location = new Point(100, 96);
            cboClick.Width = 56;
            Label countLabel = new Label { Text = "回数", AutoSize = true, Location = new Point(168, 100) };
            numClickCount.Location = new Point(206, 96);
            numClickCount.Width = 50;
            numClickCount.Minimum = 1;
            numClickCount.Maximum = 5;
            Label clickNote = new Label { Text = "「キー」欄のキーを押したまま", AutoSize = true, Location = new Point(268, 100) };
            chkHold.Text = "ドラッグする (指を離すまで押し続ける。回数2 = ダブルタップしてドラッグ)";
            chkHold.AutoSize = true;
            chkHold.Location = new Point(100, 130);
            chkFollow.Text = "指の動きでカーソルを動かす";
            chkFollow.AutoSize = true;
            chkFollow.Checked = true;
            chkFollow.Location = new Point(122, 158);
            numClickCount.Maximum = 5;
            Label speedLabel = new Label { Text = "速さ", AutoSize = true, Location = new Point(340, 160) };
            numSpeed.Location = new Point(374, 156);
            numSpeed.Width = 60;
            numSpeed.DecimalPlaces = 1;
            numSpeed.Increment = 0.1M;
            numSpeed.Minimum = 0.1M;
            numSpeed.Maximum = 10M;
            numSpeed.Value = 1M;
            rbMove.Text = "ウィンドウ移動"; rbMove.AutoSize = true; rbMove.Location = new Point(10, 196);
            Label moveNote = new Label { Text = "カーソルの下のウィンドウをつかみ、指を離すまで動かす", AutoSize = true, Location = new Point(122, 198) };
            Label resizeNote = new Label { Text = "右下の角を引くように、指の動きで大きさを変える", AutoSize = true, Location = new Point(176, 228) };
            Label moveSpeedLabel = new Label { Text = "└ 移動・大きさ変更 共通の速さ", AutoSize = true, Location = new Point(28, 258) };
            rbRun.Text = "アプリ起動"; rbRun.AutoSize = true; rbRun.Location = new Point(10, 292);
            txtRun.Location = new Point(100, 290);
            txtRun.Width = 320;
            btnBrowseRun.Text = "参照...";
            btnBrowseRun.Location = new Point(428, 288);
            btnBrowseRun.Width = 70;
            btnBrowseRun.Click += delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog { Title = "起動するアプリを選ぶ", Filter = "実行ファイル (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|すべてのファイル (*.*)|*.*" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) { txtRun.Text = dlg.FileName; rbRun.Checked = true; }
            };
            txtArgs.Location = new Point(140, 326);
            txtArgs.Width = 358;
            rbResize.Text = "ウィンドウの大きさ変更"; rbResize.AutoSize = true; rbResize.Location = new Point(10, 226);
            numMoveSpeed.Location = new Point(220, 254);
            numMoveSpeed.Width = 60;
            numMoveSpeed.DecimalPlaces = 1;
            numMoveSpeed.Increment = 0.1M;
            numMoveSpeed.Minimum = 0.1M;
            numMoveSpeed.Maximum = 10M;
            numMoveSpeed.Value = 1M;
            grpAction.Controls.AddRange(new Control[] { rbRun, txtRun, btnBrowseRun, new Label { Text = "引数", AutoSize = true, Location = new Point(100, 328) }, txtArgs, rbResize, resizeNote, moveSpeedLabel, rbMove, moveNote, numMoveSpeed, rbHotkey, txtHotkey, btnCapture, rbWheel, numWheel, chkHorizontal, wheelNote,
                rbClick, cboClick, countLabel, numClickCount, clickNote, chkHold, chkFollow, speedLabel, numSpeed });
            flow.Controls.Add(grpAction);

            // 詳細
            grpAdvanced.Text = "詳細 (空欄 = 既定値。通常は触らなくて構いません)";
            grpAdvanced.Width = 520;
            flow.Controls.Add(grpAdvanced);

            lblError.AutoSize = false;
            lblError.Width = 520;
            lblError.Height = 60;
            lblError.ForeColor = Color.Firebrick;
            flow.Controls.Add(lblError);

            editor.Controls.Add(flow);
            Controls.Add(editor);
            editor.BringToFront();

            // 変更はその場で entry に反映する
            EventHandler changed = delegate { if (!loading && cur != null) Apply(); };
            txtName.TextChanged += changed;
            cboApp.TextChanged += changed;
            cboApp.Leave += delegate { if (!loading) { RefillFilter(SelectedFilter()); RefillList(cur); } };
            cboApp.DropDown += delegate { RefillAppChoices(); };
            chkEnabled.CheckedChanged += changed;
            chkProtected.CheckedChanged += changed;
            chkUseMods.CheckedChanged += delegate { UpdateModsEnabled(); if (!loading && cur != null) Apply(); };
            chkCtrl.CheckedChanged += changed;
            chkAlt.CheckedChanged += changed;
            chkShift.CheckedChanged += changed;
            chkWin.CheckedChanged += changed;
            numFingers.ValueChanged += delegate { if (!loading && cur != null) { RebuildFingerControls(); Apply(); } };
            rbSwipe.CheckedChanged += delegate { OnKindChanged(); };
            rbTap.CheckedChanged += delegate { OnKindChanged(); };
            rbPinch.CheckedChanged += delegate { OnKindChanged(); };
            rbLand.CheckedChanged += delegate { OnKindChanged(); };
            rbMulti.CheckedChanged += delegate { OnKindChanged(); };
            numMultiCount.ValueChanged += changed;
            cboTapSlot.SelectedIndexChanged += changed;
            numTapCount.ValueChanged += changed;
            cboPinch.SelectedIndexChanged += changed;
            rbHotkey.CheckedChanged += changed;
            rbWheel.CheckedChanged += changed;
            rbClick.CheckedChanged += changed;
            rbMove.CheckedChanged += changed;
            rbResize.CheckedChanged += changed;
            rbRun.CheckedChanged += changed;
            txtRun.TextChanged += changed;
            txtArgs.TextChanged += changed;
            numMoveSpeed.ValueChanged += changed;
            cboClick.SelectedIndexChanged += changed;
            numClickCount.ValueChanged += changed;
            chkHold.CheckedChanged += changed;
            chkFollow.CheckedChanged += changed;
            numSpeed.ValueChanged += changed;
            txtHotkey.TextChanged += changed;
            numWheel.ValueChanged += changed;
            chkHorizontal.CheckedChanged += changed;
        }

        void BuildBottom()
        {
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(10, 8, 10, 8) };
            Button save = new Button { Text = "保存して反映", Width = 130, Dock = DockStyle.Right };
            save.Click += delegate { Save(); };
            Button cancel = new Button { Text = "閉じる", Width = 90, Dock = DockStyle.Right };
            cancel.Click += delegate
            {
                // 閉じるときも、並びの変更だけは保存する (中身の編集は捨てる)
                DialogResult = SaveOrderOnly() ? DialogResult.OK : DialogResult.Cancel;
                Close();
            };
            Button ignore = new Button { Text = "除外アプリ...", Width = 110, Dock = DockStyle.Right };
            ignore.Click += delegate { EditIgnoreApps(); };
            Label note = new Label
            {
                Dock = DockStyle.Fill,
                Text = "黄色の行 = 編集済み、緑の行 = 追加 (保存するまで反映されません)\r\n赤い文字 = 保存できない行 (マウスを重ねると内容が出ます)\r\n保存すると、手書きのコメントは消えます (元のファイルは .bak に残ります)",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            bottom.Controls.Add(note);
            bottom.Controls.Add(ignore);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(save);
            Controls.Add(bottom);
            CancelButton = cancel;
        }

        void BuildThresholds()
        {
            GestureKind[] sw = { GestureKind.Swipe }, tp = { GestureKind.Tap, GestureKind.MultiTap }, pn = { GestureKind.Pinch };
            GestureKind[] swPn = { GestureKind.Swipe, GestureKind.Pinch }, swTp = { GestureKind.Swipe, GestureKind.Tap };
            thresholds.Add(new Threshold { Label = "minDistance (動かす距離)", Default = "0.06", Kinds = sw,
                Get = e => e.MinDistance, Set = (e, v) => e.MinDistance = v });
            thresholds.Add(new Threshold { Label = "pinchDistance (開閉の量)", Default = "0.05", Kinds = pn,
                Get = e => e.PinchDistance, Set = (e, v) => e.PinchDistance = v });
            thresholds.Add(new Threshold { Label = "centroidTolerance (全体のずれ)", Default = "0.10", Kinds = pn,
                Get = e => e.CentroidTolerance, Set = (e, v) => e.CentroidTolerance = v });
            thresholds.Add(new Threshold { Label = "fixedTolerance (固定の指のずれ)", Default = "0.03", Kinds = swTp,
                Get = e => e.FixedTolerance, Set = (e, v) => e.FixedTolerance = v });
            thresholds.Add(new Threshold { Label = "settleMs (待ち時間 ms)", Default = "0", IsInteger = true, Kinds = swPn,
                Get = e => e.SettleMs, Set = (e, v) => e.SettleMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "repeatDistance (連続発火の間隔)", Default = "なし (1回だけ)", Kinds = swPn,
                Get = e => e.RepeatDistance, Set = (e, v) => e.RepeatDistance = v });
            GestureKind[] ld = { GestureKind.LandOrder };
            thresholds.Add(new Threshold { Label = "landMinGapMs (着地の最短間隔 ms)", Default = "80", IsInteger = true, Kinds = ld,
                Get = e => e.LandMinGapMs, Set = (e, v) => e.LandMinGapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "landMaxGapMs (着地の最長間隔 ms)", Default = "500", IsInteger = true, Kinds = ld,
                Get = e => e.LandMaxGapMs, Set = (e, v) => e.LandMaxGapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "landMaxMove (着地後の動き)", Default = "0.03", Kinds = ld,
                Get = e => e.LandMaxMove, Set = (e, v) => e.LandMaxMove = v });
            thresholds.Add(new Threshold { Label = "maxTapMs (タップの長さ ms)", Default = "250", IsInteger = true, Kinds = tp,
                Get = e => e.MaxTapMs, Set = (e, v) => e.MaxTapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "maxGapMs (回数の間隔 ms)", Default = "300", IsInteger = true, Kinds = tp,
                Get = e => e.MaxGapMs, Set = (e, v) => e.MaxGapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "maxTapMove (タップ中の動き)", Default = "0.03", Kinds = tp,
                Get = e => e.MaxTapMove, Set = (e, v) => e.MaxTapMove = v });
            thresholds.Add(new Threshold { Label = "maxSpreadMs (同時とみなす幅 ms)", Default = "100", IsInteger = true, Kinds = new[] { GestureKind.MultiTap },
                Get = e => e.MaxSpreadMs, Set = (e, v) => e.MaxSpreadMs = v == null ? (long?)null : (long)v.Value });
            GestureKind[] all = { GestureKind.Swipe, GestureKind.Tap, GestureKind.Pinch, GestureKind.LandOrder, GestureKind.MultiTap };
            thresholds.Add(new Threshold { Label = "cooldownMs (連続発火の抑制 ms)", Default = "0", IsInteger = true, Kinds = all,
                Get = e => e.CooldownMs, Set = (e, v) => e.CooldownMs = v == null ? (long?)null : (long)v.Value });

            foreach (Threshold t in thresholds)
            {
                t.LabelControl = new Label { Text = t.Label + "  既定 " + t.Default, AutoSize = true };
                t.Box = new TextBox { Width = 90, PlaceholderText = t.Default };
                t.Box.TextChanged += delegate { if (!loading && cur != null) Apply(); };
                grpAdvanced.Controls.Add(t.LabelControl);
                grpAdvanced.Controls.Add(t.Box);
            }
        }

        // 今の種類で使うしきい値の欄だけを、上から詰めて並べる
        void LayoutThresholds()
        {
            GestureKind kind = CurrentKind();
            int y = 24;
            foreach (Threshold t in thresholds)
            {
                bool show = t.Kinds.Contains(kind);
                t.LabelControl.Visible = show;
                t.Box.Visible = show;
                if (!show) continue;
                t.LabelControl.Location = new Point(10, y + 3);
                t.Box.Location = new Point(330, y);
                y += 28;
            }
            grpAdvanced.Height = y + 8;
        }

        // ---- 一覧 ----

        string SelectedFilter()
        {
            return cboFilter.SelectedItem == null ? AllApps : (string)cboFilter.SelectedItem;
        }

        static string AppOf(BindingEntry e)
        {
            return AppNames.Normalize(e.App);
        }

        bool MatchesFilter(BindingEntry e)
        {
            string f = SelectedFilter();
            if (f == AllApps) return true;
            string app = AppOf(e);
            return f == GlobalOnly ? app == null : app == f;
        }

        void RefillFilter(string keep)
        {
            bool was = loading;
            loading = true;
            cboFilter.Items.Clear();
            cboFilter.Items.Add(AllApps);
            cboFilter.Items.Add(GlobalOnly);
            foreach (string app in entries.Select(AppOf).Where(a => a != null).Distinct().OrderBy(a => a))
                cboFilter.Items.Add(app);
            int i = cboFilter.Items.IndexOf(keep);
            cboFilter.SelectedIndex = i >= 0 ? i : 0;
            loading = was;
        }

        void RefillList(BindingEntry select)
        {
            bool was = loading;
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            errorsOf = BindingEditing.FindErrors(entries);
            foreach (BindingEntry e in entries.Where(MatchesFilter))
            {
                ListViewItem item = new ListViewItem(new[] { "", "", "", "" }) { Tag = e };
                SetRow(item, e);
                list.Items.Add(item);
            }
            list.EndUpdate();
            loading = was;

            ListViewItem pick = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == select);
            if (pick == null && list.Items.Count > 0) pick = list.Items[0];
            if (pick != null) { pick.Selected = true; pick.EnsureVisible(); }
            else OnListSelected();
        }

        // 開いたときの中身 (編集前) の JSON。編集して、まだ保存していない行に色を付けるのに使う
        readonly Dictionary<BindingEntry, string> baseline = new Dictionary<BindingEntry, string>();

        static readonly Color EditedBack = Color.FromArgb(255, 245, 190);   // 編集済み (黄)
        static readonly Color AddedBack = Color.FromArgb(205, 238, 205);    // 追加 (緑)

        Color RowBack(BindingEntry e)
        {
            string was;
            if (!baseline.TryGetValue(e, out was)) return AddedBack;
            return GestureConfig.ToJson(new[] { e }) == was ? SystemColors.Window : EditedBack;
        }

        void SetRow(ListViewItem item, BindingEntry e)
        {
            item.SubItems[0].Text = AppOf(e) ?? GlobalOnly;
            bool off = e.Enabled == false;
            item.SubItems[1].Text = (e.Protected == true ? "[保護] " : "") + (off ? "(無効) " : "") + BindingEditing.ModifiersLabel(e) + BindingEditing.DisplayName(e);
            item.UseItemStyleForSubItems = true;
            item.BackColor = RowBack(e);
            item.ForeColor = off ? SystemColors.GrayText : SystemColors.WindowText;
            item.SubItems[2].Text = BindingEditing.DescribeAction(e);

            // 「保存して反映」でエラーになる行は、赤い文字で「エラー」と出す (内容はマウスを重ねると見える)
            List<string> errs;
            if (errorsOf.TryGetValue(e, out errs))
            {
                item.SubItems[3].Text = "エラー";
                item.ForeColor = Color.Firebrick;
                item.ToolTipText = "このままでは保存できません:\r\n" + string.Join("\r\n", errs.Select(m => " ・" + m));
            }
            else
            {
                item.SubItems[3].Text = "";
                item.ToolTipText = "";
            }
        }

        // 保存時にエラーになる行と、その内容。割り当てを 1 つ変えると、重複のように、ほかの行のエラーも変わるので、変えるたびに作り直す
        Dictionary<BindingEntry, List<string>> errorsOf = new Dictionary<BindingEntry, List<string>>();

        void RefreshRow(BindingEntry e)
        {
            errorsOf = BindingEditing.FindErrors(entries);
            foreach (ListViewItem item in list.Items)
                SetRow(item, (BindingEntry)item.Tag);
        }

        void OnListSelected()
        {
            if (loading) return;
            cur = list.SelectedItems.Count == 0 ? null : (BindingEntry)list.SelectedItems[0].Tag;
            LoadIntoControls();
            UpdateEditorEnabled();
        }

        void UpdateEditorEnabled()
        {
            foreach (Control c in editor.Controls) c.Enabled = cur != null;
            if (cur == null) lblError.Text = "";
        }

        void AddEntry(bool duplicate)
        {
            BindingEntry e;
            if (duplicate && cur != null)
            {
                e = cur.Clone();
                e.Name = string.IsNullOrWhiteSpace(cur.Name) ? null : cur.Name + " (コピー)";
                e.Protected = null;   // 複製は、保護を引き継がない (消せない複製が増えないように)
            }
            else
            {
                e = new BindingEntry
                {
                    Fingers = 3, Slot = 0, Direction = "Left",
                    Action = new ActionEntry { Hotkey = "" },
                };
                string f = SelectedFilter();
                if (f != AllApps && f != GlobalOnly) e.App = f;
            }
            int at = cur == null ? entries.Count : entries.IndexOf(cur) + 1;
            entries.Insert(at, e);
            if (!MatchesFilter(e)) RefillFilter(AllApps);
            RefillList(e);
            txtName.Focus();
        }

        // 表示されている行のあいだで、選んだ行を 1 つ上/下へ動かす
        void MoveEntry(int delta)
        {
            if (cur == null || list.SelectedItems.Count == 0) return;
            int i = list.SelectedItems[0].Index + delta;
            if (i < 0 || i >= list.Items.Count) return;
            BindingEditing.MoveTo(entries, cur, (BindingEntry)list.Items[i].Tag);
            RefillList(cur);
            list.Focus();
        }

        void DeleteEntry()
        {
            if (cur == null) return;
            if (cur.Protected == true)
            {
                MessageBox.Show(this,
                    "「" + BindingEditing.ModifiersLabel(cur) + BindingEditing.DisplayName(cur) + "」は保護されているため、削除できません。\r\n" +
                    "削除するには、編集欄の「保護」のチェックを外してください。",
                    "削除できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int idx = entries.IndexOf(cur);
            entries.Remove(cur);
            BindingEntry next = entries.Count == 0 ? null : entries[Math.Min(idx, entries.Count - 1)];
            RefillFilter(SelectedFilter());
            RefillList(next != null && MatchesFilter(next) ? next : null);
        }

        // ---- 編集欄 <-> BindingEntry ----

        GestureKind CurrentKind()
        {
            return rbTap.Checked ? GestureKind.Tap : rbPinch.Checked ? GestureKind.Pinch
                : rbLand.Checked ? GestureKind.LandOrder : rbMulti.Checked ? GestureKind.MultiTap : GestureKind.Swipe;
        }

        void ShowKindPanels()
        {
            GestureKind k = CurrentKind();
            grpSwipe.Visible = k == GestureKind.Swipe;
            grpTap.Visible = k == GestureKind.Tap;
            grpPinch.Visible = k == GestureKind.Pinch;
            grpLand.Visible = k == GestureKind.LandOrder;
            grpMulti.Visible = k == GestureKind.MultiTap;
            LayoutThresholds();
        }

        void OnKindChanged()
        {
            if (loading) return;
            ShowKindPanels();
            if (cur != null) Apply();
        }

        void RebuildFingerControls()
        {
            int n = (int)numFingers.Value;
            SwipeDirection?[] old = fingerCombos.Select(c => MotionValues[Math.Max(c.SelectedIndex, 0)]).ToArray();

            fingerRow.SuspendLayout();
            fingerRow.Controls.Clear();
            fingerCombos.Clear();
            for (int i = 0; i < n; i++)
            {
                Panel cell = new Panel { Width = 92, Height = 52 };
                cell.Controls.Add(new Label { Text = (i + 1) + "本目" + (i == 0 ? " (左)" : i == n - 1 && n > 1 ? " (右)" : ""),
                    AutoSize = true, Location = new Point(0, 0) });
                ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 84, Location = new Point(0, 22) };
                combo.Items.AddRange(MotionLabels);
                int sel = i < old.Length ? Array.IndexOf(MotionValues, old[i]) : 0;
                combo.SelectedIndex = Math.Max(sel, 0);
                combo.SelectedIndexChanged += delegate { if (!loading && cur != null) Apply(); };
                cell.Controls.Add(combo);
                fingerCombos.Add(combo);
                fingerRow.Controls.Add(cell);
            }
            fingerRow.ResumeLayout();

            int[] oldLand = landCombos.Select(c => c.SelectedIndex).ToArray();
            landRow.SuspendLayout();
            landRow.Controls.Clear();
            landCombos.Clear();
            for (int i = 0; i < n; i++)
            {
                Panel cell = new Panel { Width = 100, Height = 52 };
                cell.Controls.Add(new Label { Text = (i + 1) + "番目に着地", AutoSize = true, Location = new Point(0, 0) });
                ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92, Location = new Point(0, 22) };
                for (int k = 0; k < n; k++) combo.Items.Add("左から" + (k + 1) + "番目");
                combo.SelectedIndex = i < oldLand.Length && oldLand[i] >= 0 && oldLand[i] < n ? oldLand[i] : i;
                combo.SelectedIndexChanged += delegate { if (!loading && cur != null) Apply(); };
                cell.Controls.Add(combo);
                landCombos.Add(combo);
                landRow.Controls.Add(cell);
            }
            landRow.ResumeLayout();

            int keep = Math.Max(cboTapSlot.SelectedIndex, 0);
            cboTapSlot.Items.Clear();
            for (int i = 0; i < n; i++) cboTapSlot.Items.Add("左から" + (i + 1) + "番目");
            cboTapSlot.SelectedIndex = Math.Min(keep, n - 1);
        }

        void LoadIntoControls()
        {
            if (cur == null) return;
            loading = true;
            BindingEntry e = cur;
            txtName.Text = e.Name ?? "";
            cboApp.Text = e.App ?? "";
            chkEnabled.Checked = e.Enabled != false;
            chkProtected.Checked = e.Protected == true;
            KeyModifiers? mods = null;
            try { mods = KeyModifierNames.ParseOrNull(e.Modifiers); }
            catch (FormatException) { }
            chkUseMods.Checked = mods != null;
            chkCtrl.Checked = mods != null && mods.Value.HasFlag(KeyModifiers.Ctrl);
            chkAlt.Checked = mods != null && mods.Value.HasFlag(KeyModifiers.Alt);
            chkShift.Checked = mods != null && mods.Value.HasFlag(KeyModifiers.Shift);
            chkWin.Checked = mods != null && mods.Value.HasFlag(KeyModifiers.Win);
            UpdateModsEnabled();
            numFingers.Value = Math.Min(Math.Max(e.Fingers ?? 3, (int)numFingers.Minimum), (int)numFingers.Maximum);

            GestureKind kind = BindingEditing.KindOf(e);
            rbSwipe.Checked = kind == GestureKind.Swipe;
            rbTap.Checked = kind == GestureKind.Tap;
            rbPinch.Checked = kind == GestureKind.Pinch;
            rbLand.Checked = kind == GestureKind.LandOrder;
            rbMulti.Checked = kind == GestureKind.MultiTap;
            numMultiCount.Value = Math.Min(Math.Max(e.TapCount ?? 1, (int)numMultiCount.Minimum), (int)numMultiCount.Maximum);

            // 指ごとの動きは、先に今の入力欄の指の数に合わせてから、entry の値を入れる
            fingerCombos.Clear();
            RebuildFingerControls();
            SwipeDirection?[] motions = BindingEditing.GetMotions(e);
            for (int i = 0; i < fingerCombos.Count; i++)
                fingerCombos[i].SelectedIndex = Math.Max(Array.IndexOf(MotionValues, i < motions.Length ? motions[i] : null), 0);

            landCombos.Clear();
            RebuildFingerControls();
            if (e.LandOrder != null && e.LandOrder.Count == landCombos.Count)
                for (int i = 0; i < landCombos.Count; i++)
                    landCombos[i].SelectedIndex = Math.Min(Math.Max(e.LandOrder[i], 0), landCombos[i].Items.Count - 1);
            cboTapSlot.SelectedIndex = Math.Min(Math.Max(e.TapSlot ?? 0, 0), cboTapSlot.Items.Count - 1);
            numTapCount.Value = Math.Min(Math.Max(e.TapCount ?? 1, (int)numTapCount.Minimum), (int)numTapCount.Maximum);
            cboPinch.SelectedIndex = BindingEditing.GetPinchDirection(e) == PinchDirection.In ? 0 : 1;

            foreach (Threshold t in thresholds)
            {
                object v = t.Get(e);
                t.Box.Text = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }

            bool wheel = e.Action != null && e.Action.Wheel != null;
            bool click = e.Action != null && e.Action.Click != null;
            bool move = e.Action != null && e.Action.MoveWindow == true;
            rbWheel.Checked = wheel;
            rbClick.Checked = click;
            bool resize = e.Action != null && e.Action.ResizeWindow == true;
            rbMove.Checked = move;
            rbResize.Checked = resize;
            bool run = e.Action != null && e.Action.Run != null;
            rbRun.Checked = run;
            txtRun.Text = run ? e.Action.Run : "";
            txtArgs.Text = run ? e.Action.Args ?? "" : "";
            rbHotkey.Checked = !wheel && !click && !move && !resize && !run;
            numMoveSpeed.Value = Math.Min(Math.Max((move || resize) && e.Action.Speed != null ? (decimal)e.Action.Speed.Value : 1M,
                numMoveSpeed.Minimum), numMoveSpeed.Maximum);
            string clickName = click ? e.Action.Click.Trim().ToLowerInvariant() : "left";
            cboClick.SelectedIndex = clickName == "right" ? 1 : clickName == "middle" ? 2 : 0;
            numClickCount.Value = Math.Min(Math.Max(e.Action != null && e.Action.Count != null ? e.Action.Count.Value : 1,
                (int)numClickCount.Minimum), (int)numClickCount.Maximum);
            chkHold.Checked = e.Action != null && e.Action.Hold == true;
            chkFollow.Checked = e.Action == null || e.Action.Follow != false;
            // クリックの速さは、クリックの割り当てのときだけ読む (移動・大きさ変更の速さが、こちらにも出てしまわないように)
            numSpeed.Value = Math.Min(Math.Max(click && e.Action.Speed != null ? (decimal)e.Action.Speed.Value : 1M,
                numSpeed.Minimum), numSpeed.Maximum);
            txtHotkey.Text = e.Action != null ? e.Action.Hotkey ?? "" : "";
            numWheel.Value = Math.Min(Math.Max(e.Action != null && e.Action.Wheel != null ? e.Action.Wheel.Value : 1,
                (int)numWheel.Minimum), (int)numWheel.Maximum);
            chkHorizontal.Checked = e.Action != null && e.Action.Horizontal == true;

            ShowKindPanels();
            loading = false;
            ShowValidation();
        }

        // 修飾キーを指定しないときは、4 つのチェックは使えない
        void UpdateModsEnabled()
        {
            bool on = chkUseMods.Checked;
            chkCtrl.Enabled = chkAlt.Enabled = chkShift.Enabled = chkWin.Enabled = on;
        }

        static string NullIfEmpty(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        // 入力欄の内容で、選択中の entry を作り直す
        void Apply()
        {
            BindingEntry e = cur;
            if (e == null) return;
            List<string> uiErrors = new List<string>();

            e.Name = NullIfEmpty(txtName.Text);
            e.App = NullIfEmpty(cboApp.Text);
            e.Enabled = chkEnabled.Checked ? (bool?)null : false;
            e.Protected = chkProtected.Checked ? true : (bool?)null;
            if (chkUseMods.Checked)
            {
                KeyModifiers m = KeyModifiers.None;
                if (chkCtrl.Checked) m |= KeyModifiers.Ctrl;
                if (chkAlt.Checked) m |= KeyModifiers.Alt;
                if (chkShift.Checked) m |= KeyModifiers.Shift;
                if (chkWin.Checked) m |= KeyModifiers.Win;
                e.Modifiers = KeyModifierNames.Format(m);
            }
            else
            {
                e.Modifiers = null;
            }
            e.Fingers = (int)numFingers.Value;
            BindingEditing.ClearGesture(e);

            GestureKind kind = CurrentKind();
            if (kind == GestureKind.Swipe)
            {
                BindingEditing.SetMotions(e, fingerCombos.Select(c => MotionValues[Math.Max(c.SelectedIndex, 0)]).ToArray());
            }
            else if (kind == GestureKind.Tap)
            {
                e.TapSlot = Math.Max(cboTapSlot.SelectedIndex, 0);
                e.TapCount = (int)numTapCount.Value;
            }
            else if (kind == GestureKind.MultiTap)
            {
                e.TapAll = true;
                e.TapCount = numMultiCount.Value > 1 ? (int)numMultiCount.Value : (int?)null;
            }
            else if (kind == GestureKind.LandOrder)
            {
                e.LandOrder = landCombos.Select(c => Math.Max(c.SelectedIndex, 0)).ToList();
            }
            else
            {
                e.Pinch = cboPinch.SelectedIndex == 0 ? "In" : "Out";
            }

            foreach (Threshold t in thresholds)
            {
                if (!t.Kinds.Contains(kind)) continue;
                string text = t.Box.Text.Trim();
                if (text.Length == 0) continue;
                double v;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                    || (t.IsInteger && v != Math.Floor(v)))
                {
                    uiErrors.Add(t.Label.Split(' ')[0] + " は" + (t.IsInteger ? "整数" : "数値") + "で入力してください");
                    continue;
                }
                t.Set(e, v);
            }

            if (rbWheel.Checked)
                e.Action = new ActionEntry { Wheel = (int)numWheel.Value, Horizontal = chkHorizontal.Checked ? true : (bool?)null };
            else if (rbRun.Checked)
                e.Action = new ActionEntry { Run = txtRun.Text.Trim(), Args = NullIfEmpty(txtArgs.Text) };
            else if (rbResize.Checked)
                e.Action = new ActionEntry { ResizeWindow = true, Speed = numMoveSpeed.Value != 1M ? (double)numMoveSpeed.Value : (double?)null };
            else if (rbMove.Checked)
                e.Action = new ActionEntry { MoveWindow = true, Speed = numMoveSpeed.Value != 1M ? (double)numMoveSpeed.Value : (double?)null };
            else if (rbClick.Checked)
                e.Action = new ActionEntry
                {
                    Click = new[] { "Left", "Right", "Middle" }[Math.Max(cboClick.SelectedIndex, 0)],
                    Count = numClickCount.Value > 1 ? (int)numClickCount.Value : (int?)null,
                    Hotkey = NullIfEmpty(txtHotkey.Text),
                    Hold = chkHold.Checked ? true : (bool?)null,
                    Follow = chkHold.Checked && !chkFollow.Checked ? false : (bool?)null,
                    Speed = chkHold.Checked && numSpeed.Value != 1M ? (double)numSpeed.Value : (double?)null,
                };
            else
                e.Action = new ActionEntry { Hotkey = txtHotkey.Text.Trim() };

            RefreshRow(e);
            ShowValidation(uiErrors);
        }

        static readonly Regex LabelPrefix = new Regex("^bindings\\[\\d+\\]( \"[^\"]*\")?: ");

        void ShowValidation(List<string> extra = null)
        {
            List<string> lines = new List<string>();
            if (extra != null) lines.AddRange(extra);
            if (cur != null)
            {
                try { GestureConfig.Build(new[] { cur }); }
                catch (GestureConfigException ex) { lines.AddRange(ex.Errors.Select(m => LabelPrefix.Replace(m, ""))); }
            }
            lblError.Text = string.Join("\r\n", lines);
        }

        // ---- アプリ名の候補 ----

        void RefillAppChoices()
        {
            string text = cboApp.Text;
            HashSet<string> names = new HashSet<string>(entries.Select(AppOf).Where(a => a != null));
            foreach (Process p in Process.GetProcesses())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName.ToLowerInvariant()); }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            bool was = loading;
            loading = true;
            cboApp.Items.Clear();
            cboApp.Items.AddRange(names.OrderBy(n => n).Cast<object>().ToArray());
            cboApp.Text = text;
            loading = was;
        }

        // ---- キーを押して入力 ----

        void StartCapture()
        {
            rbHotkey.Checked = true;
            capturing = true;
            btnCapture.Text = "入力待ち… (キーを押す)";
            txtHotkey.Focus();
        }

        void EndCapture()
        {
            capturing = false;
            btnCapture.Text = "キーを押して入力";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!capturing || !txtHotkey.Focused) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu) return true; // 修飾キーだけでは確定しない

            string name = KeyNames.NameOf((ushort)key);
            if (name == null)
            {
                lblError.Text = "このキーは割り当てに使えません (" + key + ")";
                return true;
            }
            List<string> parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(name);
            txtHotkey.Text = string.Join("+", parts);
            EndCapture();
            return true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            EndCapture();
            base.OnDeactivate(e);
        }

        // ---- 保存 ----

        void Save()
        {
            IReadOnlyList<GestureBinding> built;
            try
            {
                built = GestureConfig.Build(entries);
            }
            catch (GestureConfigException ex)
            {
                MessageBox.Show(this, string.Join("\r\n", ex.Errors), "保存できません (内容に問題があります)",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!WriteEntries(entries)) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        // 閉じるときに、編集前の中身のまま、並びの変更と除外アプリの変更だけを保存する。保存したら true
        // (追加・削除をしているときは、並びは保存せず、元の並びのまま)
        bool SaveOrderOnly()
        {
            List<BindingEntry> ordered = original;
            if (entries.Count == original.Count)
            {
                List<BindingEntry> mapped = new List<BindingEntry>();
                foreach (BindingEntry e in entries)
                {
                    BindingEntry o;
                    if (!originalOf.TryGetValue(e, out o)) { mapped = null; break; }
                    mapped.Add(o);
                }
                if (mapped != null) ordered = mapped;
            }
            if (ordered.SequenceEqual(original) && !ignoreChanged) return false;
            return WriteEntries(ordered);
        }

        // 除外アプリ (ジェスチャーを実行しないアプリ) を、1 行に 1 つで編集する
        void EditIgnoreApps()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "除外アプリ";
                dlg.Font = Font;
                dlg.ClientSize = new Size(440, 320);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                Label hint = new Label
                {
                    Dock = DockStyle.Top, Height = 74, Padding = new Padding(10, 8, 10, 0),
                    Text = "ここに書いたアプリが前面のときは、ジェスチャーを実行しません (画面表示とログは通常どおり)。\r\n" +
                           "1 行に 1 つ。アプリ名は、ログの [ ] の中の名前です (大文字小文字と .exe は区別しません)。",
                };
                TextBox box = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
                    Text = string.Join("\r\n", ignoreApps),
                };
                Panel buttons = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(10, 8, 10, 8) };
                Button ok = new Button { Text = "OK", Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "キャンセル", Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                dlg.Controls.Add(box);
                dlg.Controls.Add(buttons);
                dlg.Controls.Add(hint);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                List<string> now = GestureConfig.NormalizeApps(
                    box.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                if (now.SequenceEqual(ignoreApps)) return;
                ignoreApps = now;
                ignoreChanged = true;
            }
        }

        // 設定ファイルに書き出す。手書きのコメントが消える場合は確認し、元のファイルは .bak に残す。書けたら true
        bool WriteEntries(List<BindingEntry> list)
        {
            try
            {
                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                if (existing != null && existing.Contains("//") && !existing.StartsWith("// このファイルは設定画面からも"))
                {
                    DialogResult r = MessageBox.Show(this,
                        "今の設定ファイルには手書きのコメントがあります。保存するとコメントは消えます。\r\n" +
                        "元のファイルは " + Path.GetFileName(path) + ".bak に残します。続けますか?",
                        "保存の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (r != DialogResult.OK) return false;
                }
                if (existing != null) File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, GestureConfig.ToJson(list, ignoreApps), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, path + "\r\n\r\n" + ex.Message, "保存に失敗しました",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }
    }
}
