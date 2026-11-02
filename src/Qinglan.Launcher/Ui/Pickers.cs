using System.Windows;

namespace Qinglan.Launcher.Ui;

/// <summary>文件夹 / 整合包选择对话框（WPF 内置，零 NuGet）。</summary>
internal static class Pickers
{
    /// <summary>选游戏目录；取消返回 null。</summary>
    public static string? Folder(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择轻蓝实例的游戏目录",
            Multiselect = false,
        };
        if (owner is not null)
        {
            dlg.ShowDialog(owner);
            return dlg.FolderName is { Length: > 0 } f ? f : null;
        }
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>选 .mrpack 种子包；取消返回 null。</summary>
    public static string? Mrpack(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包种子",
            Filter = "整合包 (*.mrpack)|*.mrpack|全部文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (owner is not null)
        {
            dlg.ShowDialog(owner);
            return dlg.FileName is { Length: > 0 } f ? f : null;
        }
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
