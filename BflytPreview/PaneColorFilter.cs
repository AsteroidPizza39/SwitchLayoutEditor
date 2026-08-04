using System.Collections.Generic;
using SwitchThemes.Common.Bflyt;
using static SwitchThemes.Common.Bflyt.BflytFile;

namespace BflytPreview
{
	internal enum PaneFilterMode
	{
		Whitelist = 0,
		Blacklist = 1
	}

	/// <summary>
	/// Scopes palette edits to pane subtrees. Empty Roots = affect entire layout.
	/// Whitelist: only panes under any root (inclusive). Blacklist: all except those.
	/// </summary>
	internal sealed class PaneColorFilter
	{
		public PaneFilterMode Mode { get; set; } = PaneFilterMode.Blacklist;
		public HashSet<BasePane> Roots { get; } = new HashSet<BasePane>();

		public bool IsActive => Roots.Count > 0;

		public void Clear() => Roots.Clear();

		public void SetRoots(IEnumerable<BasePane> panes)
		{
			Roots.Clear();
			if (panes == null) return;
			foreach (var p in panes)
			{
				if (p != null)
					Roots.Add(p);
			}
		}

		public bool IsPaneInScope(BasePane pane)
		{
			if (pane == null || !IsActive)
				return true;

			bool under = IsUnderAnyRoot(pane);
			return Mode == PaneFilterMode.Whitelist ? under : !under;
		}

		public bool IsFilterRoot(BasePane pane) =>
			pane != null && Roots.Contains(pane);

		bool IsUnderAnyRoot(BasePane pane)
		{
			for (BasePane cur = pane; cur != null; cur = cur.Parent)
			{
				if (Roots.Contains(cur))
					return true;
			}
			return false;
		}

		public string Describe()
		{
			if (!IsActive)
				return "Filter: off (all panes)";
			string mode = Mode == PaneFilterMode.Whitelist ? "Whitelist" : "Blacklist";
			return $"{mode}: {Roots.Count} root(s)";
		}
	}
}
