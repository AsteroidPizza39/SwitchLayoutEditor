using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using SwitchThemes.Common.Bflan;
using static SwitchThemes.Common.Bflan.Pai1Section;

namespace BflytPreview
{
	/// <summary>
	/// Inline BFLAN picker, timeline scrubber, flat track list, and keyframe grid.
	/// </summary>
	public sealed class LayoutAnimPanel : UserControl
	{
		readonly ComboBox cmbAnim = new ComboBox();
		readonly Label lblPat = new Label();
		readonly TrackBar trackFrame = new TrackBar();
		readonly Label lblFrame = new Label();
		readonly Button btnPlay = new Button();
		readonly CheckBox chkLoop = new CheckBox();
		readonly Button btnSave = new Button();
		readonly Button btnSaveAs = new Button();
		readonly Button btnOpenEditor = new Button();
		readonly ListBox lstTracks = new ListBox();
		readonly DataGridView gridKeys = new DataGridView();
		readonly Timer playTimer = new Timer();

		List<LayoutAnimRef> animRefs = new List<LayoutAnimRef>();
		LayoutAnimRef activeRef;
		BflanFile activeBflan;
		List<LayoutAnimTrack> tracks = new List<LayoutAnimTrack>();
		bool suppressUi;
		bool dirty;
		float currentFrame;

		public LayoutAnimState AnimState { get; private set; }
		public float CurrentFrame => currentFrame;
		public BflanFile ActiveBflan => activeBflan;
		public bool IsDirty => dirty;

		public event EventHandler AnimStateChanged;
		public event EventHandler<string> FocusPaneRequested;
		public event EventHandler OpenInBflanEditorRequested;

		public LayoutAnimPanel()
		{
			SuspendLayout();
			Dock = DockStyle.Fill;
			BackColor = SystemColors.Control;

			var top = new TableLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 78,
				ColumnCount = 1,
				RowCount = 3,
				Padding = new Padding(2),
			};
			top.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
			top.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
			top.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

			cmbAnim.Dock = DockStyle.Fill;
			cmbAnim.DropDownStyle = ComboBoxStyle.DropDownList;
			cmbAnim.SelectedIndexChanged += (s, e) => OnAnimSelected();
			top.Controls.Add(cmbAnim, 0, 0);

			lblPat.Dock = DockStyle.Fill;
			lblPat.TextAlign = ContentAlignment.MiddleLeft;
			lblPat.AutoEllipsis = true;
			top.Controls.Add(lblPat, 0, 1);

			var timeline = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				WrapContents = false,
				FlowDirection = FlowDirection.LeftToRight,
			};
			btnPlay.Text = "Play";
			btnPlay.Width = 48;
			btnPlay.Click += (s, e) => TogglePlay();
			chkLoop.Text = "Loop";
			chkLoop.AutoSize = true;
			chkLoop.Checked = true;
			chkLoop.Margin = new Padding(4, 4, 4, 0);
			lblFrame.AutoSize = true;
			lblFrame.Margin = new Padding(4, 6, 0, 0);
			lblFrame.Text = "0";
			timeline.Controls.Add(btnPlay);
			timeline.Controls.Add(chkLoop);
			timeline.Controls.Add(lblFrame);
			top.Controls.Add(timeline, 0, 2);

			trackFrame.Dock = DockStyle.Top;
			trackFrame.Height = 36;
			trackFrame.TickStyle = TickStyle.None;
			trackFrame.Minimum = 0;
			trackFrame.Maximum = 0;
			trackFrame.ValueChanged += (s, e) =>
			{
				if (suppressUi) return;
				SetFrame(trackFrame.Value, fromUi: true);
			};

			var buttons = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 28,
				WrapContents = false,
			};
			btnSave.Text = "Save";
			btnSave.Width = 52;
			btnSave.Click += (s, e) => SaveActive(saveAs: false);
			btnSaveAs.Text = "Save As…";
			btnSaveAs.Width = 72;
			btnSaveAs.Click += (s, e) => SaveActive(saveAs: true);
			btnOpenEditor.Text = "Open in Bflan Editor…";
			btnOpenEditor.AutoSize = true;
			btnOpenEditor.Click += (s, e) => OpenInBflanEditorRequested?.Invoke(this, EventArgs.Empty);
			buttons.Controls.Add(btnSave);
			buttons.Controls.Add(btnSaveAs);
			buttons.Controls.Add(btnOpenEditor);

			var split = new SplitContainer
			{
				Dock = DockStyle.Fill,
				Orientation = Orientation.Horizontal,
				SplitterDistance = 90,
			};
			lstTracks.Dock = DockStyle.Fill;
			lstTracks.IntegralHeight = false;
			lstTracks.DisplayMember = "DisplayName";
			lstTracks.SelectedIndexChanged += (s, e) => LoadKeyframesForSelection();
			lstTracks.DoubleClick += (s, e) =>
			{
				if (lstTracks.SelectedItem is LayoutAnimTrack t
					&& t.TargetKind == PaiEntry.AnimationTarget.Pane
					&& !string.IsNullOrEmpty(t.TargetName))
					FocusPaneRequested?.Invoke(this, t.TargetName);
			};
			split.Panel1.Controls.Add(lstTracks);

			gridKeys.Dock = DockStyle.Fill;
			gridKeys.AllowUserToAddRows = true;
			gridKeys.AllowUserToDeleteRows = true;
			gridKeys.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
			gridKeys.RowHeadersVisible = false;
			gridKeys.Columns.Add("Frame", "Frame");
			gridKeys.Columns.Add("Value", "Value");
			gridKeys.Columns.Add("Blend", "Blend");
			gridKeys.CellValueChanged += GridKeys_CellValueChanged;
			gridKeys.UserDeletedRow += (s, e) => CommitKeyframesFromGrid();
			gridKeys.UserAddedRow += (s, e) => { /* commit on cell edit */ };
			split.Panel2.Controls.Add(gridKeys);

			Controls.Add(split);
			Controls.Add(buttons);
			Controls.Add(trackFrame);
			Controls.Add(top);

			playTimer.Interval = 33;
			playTimer.Tick += (s, e) => AdvancePlay();

			ResumeLayout(false);
			UpdateEnabled();
		}

		public void LoadAnimations(IList<LayoutAnimRef> refs)
		{
			StopPlay();
			animRefs = refs != null ? new List<LayoutAnimRef>(refs) : new List<LayoutAnimRef>();
			suppressUi = true;
			cmbAnim.Items.Clear();
			cmbAnim.Items.Add("(none)");
			foreach (var r in animRefs)
				cmbAnim.Items.Add(r);
			cmbAnim.SelectedIndex = 0;
			suppressUi = false;
			ClearActive();
			UpdateEnabled();
		}

		public void SelectAnimByNameContains(string fragment)
		{
			if (string.IsNullOrEmpty(fragment)) return;
			for (int i = 0; i < cmbAnim.Items.Count; i++)
			{
				if (cmbAnim.Items[i] is LayoutAnimRef r
					&& r.DisplayName != null
					&& r.DisplayName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					cmbAnim.SelectedIndex = i;
					return;
				}
			}
		}

		public IFileWriter ActiveWriter => activeRef?.Writer;
		public LayoutAnimRef ActiveRef => activeRef;

		public byte[] GetSaveBytes() => activeBflan?.WriteFile();

		void OnAnimSelected()
		{
			if (suppressUi) return;
			StopPlay();
			if (cmbAnim.SelectedItem is LayoutAnimRef r)
			{
				activeRef = r;
				activeBflan = LayoutAnimDiscovery.TryLoad(r);
				dirty = false;
				if (activeBflan == null)
				{
					MessageBox.Show("Could not parse BFLAN: " + r.DisplayName);
					ClearActive();
					return;
				}
				BindBflan();
			}
			else
			{
				ClearActive();
			}
		}

		void ClearActive()
		{
			activeRef = null;
			activeBflan = null;
			tracks.Clear();
			lstTracks.DataSource = null;
			gridKeys.Rows.Clear();
			lblPat.Text = "";
			suppressUi = true;
			trackFrame.Maximum = 0;
			trackFrame.Value = 0;
			suppressUi = false;
			currentFrame = 0;
			AnimState = null;
			dirty = false;
			UpdateEnabled();
			AnimStateChanged?.Invoke(this, EventArgs.Empty);
		}

		void BindBflan()
		{
			var pat = activeBflan.patData;
			var pai = activeBflan.paiData;
			string patName = pat?.Name ?? "";
			ushort start = pat?.Unk_StartOfFile ?? 0;
			ushort end = pat?.Unk_EndOfFile ?? 0;
			ushort frameSize = pai?.FrameSize ?? 0;
			lblPat.Text = string.IsNullOrEmpty(patName)
				? $"Frames 0…{frameSize}"
				: $"{patName}  ·  {start}→{end}  ·  size {frameSize}";

			int max = Math.Max(0, (int)frameSize);
			suppressUi = true;
			trackFrame.Minimum = 0;
			trackFrame.Maximum = max;
			trackFrame.Value = Math.Min(trackFrame.Value, max);
			suppressUi = false;

			tracks = LayoutAnimEvaluator.ListTracks(activeBflan);
			lstTracks.DataSource = null;
			lstTracks.DataSource = tracks;
			lstTracks.DisplayMember = "DisplayName";

			SetFrame(trackFrame.Value, fromUi: true);
			UpdateEnabled();
		}

		void SetFrame(float frame, bool fromUi)
		{
			currentFrame = frame;
			if (!fromUi)
			{
				suppressUi = true;
				int v = (int)Math.Round(frame);
				if (v < trackFrame.Minimum) v = trackFrame.Minimum;
				if (v > trackFrame.Maximum) v = trackFrame.Maximum;
				trackFrame.Value = v;
				suppressUi = false;
			}
			lblFrame.Text = currentFrame.ToString("0.###", CultureInfo.InvariantCulture);
			AnimState = activeBflan != null
				? LayoutAnimEvaluator.Evaluate(activeBflan, currentFrame)
				: null;
			AnimStateChanged?.Invoke(this, EventArgs.Empty);
		}

		void LoadKeyframesForSelection()
		{
			gridKeys.Rows.Clear();
			if (!(lstTracks.SelectedItem is LayoutAnimTrack t) || t.Entry?.KeyFrames == null)
				return;
			suppressUi = true;
			foreach (var kf in t.Entry.KeyFrames)
			{
				gridKeys.Rows.Add(
					kf.Frame.ToString(CultureInfo.InvariantCulture),
					kf.Value.ToString(CultureInfo.InvariantCulture),
					kf.Blend.ToString(CultureInfo.InvariantCulture));
			}
			suppressUi = false;
		}

		void GridKeys_CellValueChanged(object sender, DataGridViewCellEventArgs e)
		{
			if (suppressUi || e.RowIndex < 0) return;
			CommitKeyframesFromGrid();
		}

		void CommitKeyframesFromGrid()
		{
			if (!(lstTracks.SelectedItem is LayoutAnimTrack t) || t.Entry == null)
				return;

			var list = new List<KeyFrame>();
			foreach (DataGridViewRow row in gridKeys.Rows)
			{
				if (row.IsNewRow) continue;
				string fs = row.Cells[0].Value?.ToString();
				string vs = row.Cells[1].Value?.ToString();
				string bs = row.Cells[2].Value?.ToString();
				if (string.IsNullOrWhiteSpace(fs) && string.IsNullOrWhiteSpace(vs))
					continue;
				if (!float.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out float fr)
					&& !float.TryParse(fs, out fr))
					continue;
				float.TryParse(vs, NumberStyles.Float, CultureInfo.InvariantCulture, out float val);
				if (val == 0f) float.TryParse(vs, out val);
				float.TryParse(bs, NumberStyles.Float, CultureInfo.InvariantCulture, out float blend);
				if (blend == 0f && !string.IsNullOrWhiteSpace(bs)) float.TryParse(bs, out blend);
				list.Add(new KeyFrame { Frame = fr, Value = val, Blend = blend });
			}
			list.Sort((a, b) => a.Frame.CompareTo(b.Frame));
			t.Entry.KeyFrames = list;
			dirty = true;
			SetFrame(currentFrame, fromUi: true);
			UpdateEnabled();
		}

		void TogglePlay()
		{
			if (playTimer.Enabled)
				StopPlay();
			else if (activeBflan != null && trackFrame.Maximum > 0)
			{
				playTimer.Start();
				btnPlay.Text = "Pause";
			}
		}

		void StopPlay()
		{
			playTimer.Stop();
			btnPlay.Text = "Play";
		}

		void AdvancePlay()
		{
			float next = currentFrame + 1f;
			if (next > trackFrame.Maximum)
			{
				if (chkLoop.Checked)
					next = 0;
				else
				{
					StopPlay();
					return;
				}
			}
			SetFrame(next, fromUi: false);
		}

		void SaveActive(bool saveAs)
		{
			if (activeBflan == null) return;
			byte[] data;
			try
			{
				data = activeBflan.WriteFile();
			}
			catch (Exception ex)
			{
				MessageBox.Show("Failed to serialize BFLAN: " + ex.Message);
				return;
			}

			if (saveAs || activeRef?.Writer == null)
			{
				using (var dlg = new SaveFileDialog
				{
					Filter = "BFLAN files|*.bflan|All files|*.*",
					FileName = (activeRef?.DisplayName ?? "anim") + ".bflan",
				})
				{
					if (dlg.ShowDialog() != DialogResult.OK)
						return;
					File.WriteAllBytes(dlg.FileName, data);
					if (activeRef != null)
					{
						activeRef.FilePath = dlg.FileName;
						activeRef.Writer = new DiskFileProvider(dlg.FileName);
						activeRef.Data = data;
					}
				}
			}
			else
			{
				activeRef.Writer.Save(data);
				activeRef.Data = data;
			}
			dirty = false;
			UpdateEnabled();
		}

		void UpdateEnabled()
		{
			bool has = activeBflan != null;
			trackFrame.Enabled = has;
			btnPlay.Enabled = has && trackFrame.Maximum > 0;
			btnSave.Enabled = has && (dirty || activeRef?.Writer != null);
			btnSaveAs.Enabled = has;
			btnOpenEditor.Enabled = has;
			lstTracks.Enabled = has;
			gridKeys.Enabled = has;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				StopPlay();
				playTimer.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
