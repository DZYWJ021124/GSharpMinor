using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Pa1.App
{
    /// <summary>Vista+ 文件夹选择器（IFileDialog · FOS_PICKFOLDERS · FOS_ALLOWMULTISELECT）：
    /// 带地址栏可直贴路径，可一次多选文件夹；老式 FolderBrowserDialog 树形浏览深目录
    /// （E:\Music\古典）极其难用。</summary>
    static class FolderPick
    {
        public static List<string> PickFolders(string initialDir)
        {
            var dirs = new List<string>();
            var dlg = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 /* FOS_PICKFOLDERS */ | 0x200 /* FOS_ALLOWMULTISELECT */);
                if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir))
                {
                    IShellItem item;
                    if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, typeof(IShellItem).GUID, out item) == 0 && item != null)
                        dlg.SetFolder(item);
                }
                int hr = dlg.Show(IntPtr.Zero);
                if (hr != 0) return dirs;            /* 取消 / 出错 */
                IShellItemArray arr;
                dlg.GetResults(out arr);
                uint n;
                arr.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IShellItem it;
                    arr.GetItemAt(i, out it);
                    string path;
                    it.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    if (!string.IsNullOrEmpty(path)) dirs.Add(path);
                }
                return dirs;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            /* IModalWindow */
            [PreserveSig] int Show(IntPtr hwndParent);
            /* IFileDialog（vtable 顺序不可乱） */
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
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
            void SetClientGuid([MarshalAs(UnmanagedType.LPStruct)] Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            /* IFileOpenDialog：多选走 GetResults（IShellItemArray） */
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(IntPtr keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetAttributes(int dwAttribFlags, int sfgaoMask, out int psfgaoAttribs);
            void GetCount(out uint pdwNumItems);
            void GetItemAt(uint dwIndex, out IShellItem ppsi);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
