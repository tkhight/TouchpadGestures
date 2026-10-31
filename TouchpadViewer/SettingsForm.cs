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
        readonly RadioButton rbSwipe = new RadioButton();
        readonly RadioButton rbTap = new RadioButton();
        readonly RadioButton rbPinch = new RadioButton();
        readonly GroupBox grpSwipe = new GroupBox();
        readonly FlowLayoutPanel fingerRow = new FlowLayoutPanel();
        readonly List<ComboBox> fingerCombos = new List<ComboBox>();
        readonly GroupBox grpTap = new GroupBox();
        readonly ComboBox cboTapSlot = new ComboBox();
        readonly NumericUpDown numTapCount = new NumericUpDown();
        readonly GroupBox grpPinch = new GroupBox();
        readonly ComboBox cboPinch = new ComboBox();
        readonly RadioButton rbHotkey = new RadioButton();
        readonly RadioButton rbWheel = new RadioButton();
        readonly TextBox txtHotkey = new TextBox();
        readonly Button btnCapture = new Button();
        readonly NumericUpDown numWheel = new NumericUpDown();
        readonly CheckBox chkHorizontal = new CheckBox();
        readonly GroupBox grpAdvanced = new GroupBox();
        readonly Label lblError = new Label();
        readonly List<Threshold> thresholds = new List<Threshold>();

        public SettingsForm(string path, List<BindingEntry> source)
        {
            this.path = path;
            entries = source.Select(e => e.Clone()).ToList();

            Text = "ジェスチャーの設定";
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildThresholds();
            BuildLeft();
            BuildEditor();
            BuildBottom();
            RefillFilter(AllApps);
            RefillList(null);
            UpdateEditorEnabled();
        }

        // ---- 画面の組み立て ----

        void BuildLeft()
        {
            Panel left = new Panel { Dock = DockStyle.Left, Width = 400, Padding = new Padding(10, 10, 5, 0) };

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
            list.Columns.Add("アプリ", 80);
            list.Columns.Add("ジェスチャー", 190);
            list.Columns.Add("動作", 100);
            list.SelectedIndexChanged += delegate { OnListSelected(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            Button add = new Button { Text = "追加", Width = 80 };
            add.Click += delegate { AddEntry(false); };
            Button copy = new Button { Text = "複製", Width = 80 };
            copy.Click += delegate { AddEntry(true); };
            Button del = new Button { Text = "削除", Width = 80 };
            del.Click += delegate { DeleteEntry(); };
            buttons.Controls.AddRange(new Control[] { add, copy, del });

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
            Panel basic = new Panel { Width = 520, Height = 130 };
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

            basic.Controls.Add(new Label { Text = "種類", AutoSize = true, Location = new Point(0, 102) });
            rbSwipe.Text = "スワイプ"; rbSwipe.AutoSize = true; rbSwipe.Location = new Point(90, 100);
            rbTap.Text = "タップ"; rbTap.AutoSize = true; rbTap.Location = new Point(190, 100);
            rbPinch.Text = "ピンチ"; rbPinch.AutoSize = true; rbPinch.Location = new Point(280, 100);
            rbSwipe.Checked = true;
            basic.Controls.AddRange(new Control[] { rbSwipe, rbTap, rbPinch });
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

            // 動作
            GroupBox grpAction = new GroupBox { Text = "動作", Width = 520, Height = 110 };
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
            grpAction.Controls.AddRange(new Control[] { rbHotkey, txtHotkey, btnCapture, rbWheel, numWheel, chkHorizontal, wheelNote });
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
            numFingers.ValueChanged += delegate { if (!loading && cur != null) { RebuildFingerControls(); Apply(); } };
            rbSwipe.CheckedChanged += delegate { OnKindChanged(); };
            rbTap.CheckedChanged += delegate { OnKindChanged(); };
            rbPinch.CheckedChanged += delegate { OnKindChanged(); };
            cboTapSlot.SelectedIndexChanged += changed;
            numTapCount.ValueChanged += changed;
            cboPinch.SelectedIndexChanged += changed;
            rbHotkey.CheckedChanged += changed;
            rbWheel.CheckedChanged += changed;
            txtHotkey.TextChanged += changed;
            numWheel.ValueChanged += changed;
            chkHorizontal.CheckedChanged += changed;
        }

        void BuildBottom()
        {
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(10, 8, 10, 8) };
            Button save = new Button { Text = "保存して反映", Width = 130, Dock = DockStyle.Right };
            save.Click += delegate { Save(); };
            Button cancel = new Button { Text = "閉じる", Width = 90, Dock = DockStyle.Right };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Label note = new Label
            {
                Dock = DockStyle.Fill,
                Text = "保存すると、手書きのコメントは消えます。元のファイルは .bak に残ります。",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            bottom.Controls.Add(note);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(save);
            Controls.Add(bottom);
            CancelButton = cancel;
        }

        void BuildThresholds()
        {
            GestureKind[] sw = { GestureKind.Swipe }, tp = { GestureKind.Tap }, pn = { GestureKind.Pinch };
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
            thresholds.Add(new Threshold { Label = "maxTapMs (タップの長さ ms)", Default = "250", IsInteger = true, Kinds = tp,
                Get = e => e.MaxTapMs, Set = (e, v) => e.MaxTapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "maxGapMs (回数の間隔 ms)", Default = "300", IsInteger = true, Kinds = tp,
                Get = e => e.MaxGapMs, Set = (e, v) => e.MaxGapMs = v == null ? (long?)null : (long)v.Value });
            thresholds.Add(new Threshold { Label = "maxTapMove (タップ中の動き)", Default = "0.03", Kinds = tp,
                Get = e => e.MaxTapMove, Set = (e, v) => e.MaxTapMove = v });

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
            foreach (BindingEntry e in entries.Where(MatchesFilter))
            {
                ListViewItem item = new ListViewItem(new[] { "", "", "" }) { Tag = e };
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

        static void SetRow(ListViewItem item, BindingEntry e)
        {
            item.SubItems[0].Text = AppOf(e) ?? GlobalOnly;
            item.SubItems[1].Text = BindingEditing.DisplayName(e);
            item.SubItems[2].Text = BindingEditing.DescribeAction(e);
        }

        void RefreshRow(BindingEntry e)
        {
            foreach (ListViewItem item in list.Items)
                if (item.Tag == e) { SetRow(item, e); return; }
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

        void DeleteEntry()
        {
            if (cur == null) return;
            int idx = entries.IndexOf(cur);
            entries.Remove(cur);
            BindingEntry next = entries.Count == 0 ? null : entries[Math.Min(idx, entries.Count - 1)];
            RefillFilter(SelectedFilter());
            RefillList(next != null && MatchesFilter(next) ? next : null);
        }

        // ---- 編集欄 <-> BindingEntry ----

        GestureKind CurrentKind()
        {
            return rbTap.Checked ? GestureKind.Tap : rbPinch.Checked ? GestureKind.Pinch : GestureKind.Swipe;
        }

        void ShowKindPanels()
        {
            GestureKind k = CurrentKind();
            grpSwipe.Visible = k == GestureKind.Swipe;
            grpTap.Visible = k == GestureKind.Tap;
            grpPinch.Visible = k == GestureKind.Pinch;
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
            numFingers.Value = Math.Min(Math.Max(e.Fingers ?? 3, (int)numFingers.Minimum), (int)numFingers.Maximum);

            GestureKind kind = BindingEditing.KindOf(e);
            rbSwipe.Checked = kind == GestureKind.Swipe;
            rbTap.Checked = kind == GestureKind.Tap;
            rbPinch.Checked = kind == GestureKind.Pinch;

            // 指ごとの動きは、先に今の入力欄の指の数に合わせてから、entry の値を入れる
            fingerCombos.Clear();
            RebuildFingerControls();
            SwipeDirection?[] motions = BindingEditing.GetMotions(e);
            for (int i = 0; i < fingerCombos.Count; i++)
                fingerCombos[i].SelectedIndex = Math.Max(Array.IndexOf(MotionValues, i < motions.Length ? motions[i] : null), 0);

            cboTapSlot.SelectedIndex = Math.Min(Math.Max(e.TapSlot ?? 0, 0), cboTapSlot.Items.Count - 1);
            numTapCount.Value = Math.Min(Math.Max(e.TapCount ?? 1, (int)numTapCount.Minimum), (int)numTapCount.Maximum);
            cboPinch.SelectedIndex = BindingEditing.GetPinchDirection(e) == PinchDirection.In ? 0 : 1;

            foreach (Threshold t in thresholds)
            {
                object v = t.Get(e);
                t.Box.Text = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }

            bool wheel = e.Action != null && e.Action.Wheel != null;
            rbWheel.Checked = wheel;
            rbHotkey.Checked = !wheel;
            txtHotkey.Text = e.Action != null ? e.Action.Hotkey ?? "" : "";
            numWheel.Value = Math.Min(Math.Max(e.Action != null && e.Action.Wheel != null ? e.Action.Wheel.Value : 1,
                (int)numWheel.Minimum), (int)numWheel.Maximum);
            chkHorizontal.Checked = e.Action != null && e.Action.Horizontal == true;

            ShowKindPanels();
            loading = false;
            ShowValidation();
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

            e.Action = rbWheel.Checked
                ? new ActionEntry { Wheel = (int)numWheel.Value, Horizontal = chkHorizontal.Checked ? true : (bool?)null }
                : new ActionEntry { Hotkey = txtHotkey.Text.Trim() };

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

            try
            {
                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                if (existing != null && existing.Contains("//") && !existing.StartsWith("// このファイルは設定画面からも"))
                {
                    DialogResult r = MessageBox.Show(this,
                        "今の設定ファイルには手書きのコメントがあります。保存するとコメントは消えます。\r\n" +
                        "元のファイルは " + Path.GetFileName(path) + ".bak に残します。続けますか?",
                        "保存の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (r != DialogResult.OK) return;
                }
                if (existing != null) File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, GestureConfig.ToJson(entries), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, path + "\r\n\r\n" + ex.Message, "保存に失敗しました",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
