using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BflytPreview
{
	/// <summary>
	/// Vista+ IFileDialog folder picker (Explorer-style), instead of the classic Browse For Folder UI.
	/// </summary>
	internal static class ModernFolderBrowser
	{
		public static bool TryPickFolder(IWin32Window owner, out string path, string title = "Select Folder")
		{
			path = null;
			var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
			try
			{
				dialog.SetOptions(
					FOS.FOS_PICKFOLDERS |
					FOS.FOS_FORCEFILESYSTEM |
					FOS.FOS_PATHMUSTEXIST |
					FOS.FOS_FILEMUSTEXIST);

				if (!string.IsNullOrEmpty(title))
					dialog.SetTitle(title);

				IntPtr hwnd = owner?.Handle ?? IntPtr.Zero;
				int hr = dialog.Show(hwnd);
				if (hr != 0) // cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED) etc.)
					return false;

				dialog.GetResult(out IShellItem item);
				item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr pszPath);
				try
				{
					path = Marshal.PtrToStringAuto(pszPath);
				}
				finally
				{
					if (pszPath != IntPtr.Zero)
						Marshal.FreeCoTaskMem(pszPath);
				}

				return !string.IsNullOrEmpty(path);
			}
			finally
			{
				Marshal.ReleaseComObject(dialog);
			}
		}

		[ComImport]
		[Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
		class FileOpenDialogRCW
		{
		}

		[ComImport]
		[Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IFileDialog
		{
			[PreserveSig] int Show(IntPtr parent);
			void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
			void SetFileTypeIndex(uint iFileType);
			void GetFileTypeIndex(out uint piFileType);
			void Advise(IntPtr pfde, out uint pdwCookie);
			void Unadvise(uint dwCookie);
			void SetOptions(FOS fos);
			void GetOptions(out FOS pfos);
			void SetDefaultFolder(IShellItem psi);
			void SetFolder(IShellItem psi);
			void GetFolder(out IShellItem ppsi);
			void GetCurrentSelection(out IShellItem ppsi);
			void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
			void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
			void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
			void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
			void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
			void GetResult(out IShellItem ppsi);
			void AddPlace(IShellItem psi, int fdap);
			void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
			void Close(int hr);
			void SetClientGuid(ref Guid guid);
			void ClearClientData();
			void SetFilter(IntPtr pFilter);
		}

		[ComImport]
		[Guid("D57C7288-D4AD-4768-BE02-9D9695518B47")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IFileOpenDialog : IFileDialog
		{
			// IFileDialog
			[PreserveSig] new int Show(IntPtr parent);
			new void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
			new void SetFileTypeIndex(uint iFileType);
			new void GetFileTypeIndex(out uint piFileType);
			new void Advise(IntPtr pfde, out uint pdwCookie);
			new void Unadvise(uint dwCookie);
			new void SetOptions(FOS fos);
			new void GetOptions(out FOS pfos);
			new void SetDefaultFolder(IShellItem psi);
			new void SetFolder(IShellItem psi);
			new void GetFolder(out IShellItem ppsi);
			new void GetCurrentSelection(out IShellItem ppsi);
			new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
			new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
			new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
			new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
			new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
			new void GetResult(out IShellItem ppsi);
			new void AddPlace(IShellItem psi, int fdap);
			new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
			new void Close(int hr);
			new void SetClientGuid(ref Guid guid);
			new void ClearClientData();
			new void SetFilter(IntPtr pFilter);
			// IFileOpenDialog
			void GetResults(out IntPtr ppenum);
			void GetSelectedItems(out IntPtr ppsai);
		}

		[ComImport]
		[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface IShellItem
		{
			void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
			void GetParent(out IShellItem ppsi);
			void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
			void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
			void Compare(IShellItem psi, uint hint, out int piOrder);
		}

		[Flags]
		enum FOS : uint
		{
			FOS_OVERWRITEPROMPT = 0x2,
			FOS_STRICTFILETYPES = 0x4,
			FOS_NOCHANGEDIR = 0x8,
			FOS_PICKFOLDERS = 0x20,
			FOS_FORCEFILESYSTEM = 0x40,
			FOS_ALLNONSTORAGEITEMS = 0x80,
			FOS_NOVALIDATE = 0x100,
			FOS_ALLOWMULTISELECT = 0x200,
			FOS_PATHMUSTEXIST = 0x800,
			FOS_FILEMUSTEXIST = 0x1000,
			FOS_CREATEPROMPT = 0x2000,
			FOS_SHAREAWARE = 0x4000,
			FOS_NOREADONLYRETURN = 0x8000,
			FOS_NOTESTCREATE = 0x10000,
			FOS_HIDEMRUPLACES = 0x20000,
			FOS_HIDEPINNEDPLACES = 0x40000,
			FOS_NODEREFERENCELINKS = 0x100000,
			FOS_OKBUTTONNEEDSINTERACTION = 0x200000,
			FOS_DONTADDTORECENT = 0x2000000,
			FOS_FORCESHOWHIDDEN = 0x10000000,
			FOS_DEFAULTNOMINIMODE = 0x20000000,
			FOS_FORCEPREVIEWPANEON = 0x40000000,
			FOS_SUPPORTSTREAMABLEITEMS = 0x80000000
		}

		enum SIGDN : uint
		{
			SIGDN_FILESYSPATH = 0x80058000,
		}
	}
}
