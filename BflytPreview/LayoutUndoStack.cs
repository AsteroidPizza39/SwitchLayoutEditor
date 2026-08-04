using System;
using System.Collections.Generic;

namespace BflytPreview
{
	/// <summary>
	/// Snapshot undo/redo for BFLYT bytes (SaveFile ↔ new BflytFile).
	/// </summary>
	internal sealed class LayoutUndoStack
	{
		readonly List<byte[]> undo = new List<byte[]>();
		readonly List<byte[]> redo = new List<byte[]>();
		readonly int maxDepth;

		public LayoutUndoStack(int maxDepth = 50)
		{
			this.maxDepth = Math.Max(1, maxDepth);
		}

		public bool CanUndo => undo.Count > 0;
		public bool CanRedo => redo.Count > 0;

		public void Clear()
		{
			undo.Clear();
			redo.Clear();
		}

		/// <summary>
		/// Record <paramref name="snapshot"/> as the state before an upcoming edit.
		/// </summary>
		public void Push(byte[] snapshot)
		{
			if (snapshot == null || snapshot.Length == 0)
				return;
			if (undo.Count > 0 && BytesEqual(undo[undo.Count - 1], snapshot))
				return;

			undo.Add(snapshot);
			while (undo.Count > maxDepth)
				undo.RemoveAt(0);
			redo.Clear();
		}

		/// <summary>
		/// Undo: push current onto redo, return previous snapshot (or null).
		/// </summary>
		public byte[] Undo(byte[] current)
		{
			if (undo.Count == 0)
				return null;
			if (current != null && current.Length > 0)
				redo.Add(current);
			byte[] prev = undo[undo.Count - 1];
			undo.RemoveAt(undo.Count - 1);
			return prev;
		}

		/// <summary>
		/// Redo: push current onto undo, return next snapshot (or null).
		/// </summary>
		public byte[] Redo(byte[] current)
		{
			if (redo.Count == 0)
				return null;
			if (current != null && current.Length > 0)
				undo.Add(current);
			byte[] next = redo[redo.Count - 1];
			redo.RemoveAt(redo.Count - 1);
			return next;
		}

		static bool BytesEqual(byte[] a, byte[] b)
		{
			if (ReferenceEquals(a, b)) return true;
			if (a == null || b == null || a.Length != b.Length) return false;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i]) return false;
			}
			return true;
		}
	}
}
