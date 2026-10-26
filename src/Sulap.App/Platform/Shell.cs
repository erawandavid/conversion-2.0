using System.IO;
using System.Runtime.InteropServices;
using Sulap.App.Interop;

namespace Sulap.App.Platform;

internal static class Shell
{
    /// <summary>Opens the item's folder in File Explorer with the item selected (what <c>explorer /select,</c> does).</summary>
    public static void ShowInExplorer(string path)
    {
        var item = NativeMethods.ILCreateFromPathW(path);
        if (item == IntPtr.Zero)
        {
            throw new FileNotFoundException("The item doesn't exist.", path);
        }

        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally
        {
            NativeMethods.ILFree(item);
        }
    }
}
