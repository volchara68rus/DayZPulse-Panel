using System.Runtime.InteropServices;
using System.Text;

namespace DayZPulse.SystemIntegration;

/// <summary>
/// Диалог выбора папки. Панель работает на той же машине, что и браузер,
/// поэтому каталог выбирается системным окном Windows, а путь возвращается в интерфейс.
/// Используется нативный <c>SHBrowseForFolder</c>: он не требует цикла сообщений
/// в вызывающем потоке, поэтому надёжно открывается и из запроса к панели.
/// </summary>
public static class FolderPicker
{
    // Флаги SHBrowseForFolder.
    private const uint BIF_RETURNONLYFSDIRS = 0x0001; // только каталоги файловой системы
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;   // современный диалог, можно создавать папки
    private const uint BIF_EDITBOX = 0x0010;          // поле ввода пути
    private const uint BIF_SHAREABLE = 0x8000;        // сетевые ресурсы

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shell32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>
    /// Открывает диалог выбора папки. Возвращает null, если пользователь отменил выбор
    /// или диалог недоступен (например, панель запущена без рабочего стола).
    /// </summary>
    public static FolderPickResult Pick(string? initialPath, string title)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FolderPickResult.Unavailable("Выбор папки доступен только в Windows.");
        }

        if (!Environment.UserInteractive)
        {
            return FolderPickResult.Unavailable(
                "Панель запущена как служба: системный диалог недоступен. Введите путь вручную.");
        }

        var result = FolderPickResult.Cancelled();

        // SHBrowseForFolder требует инициализированного COM в потоке.
        var thread = new Thread(() =>
        {
            var comInitialized = false;
            try
            {
                // S_FALSE тоже означает, что COM готов в этом потоке.
                var hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                comInitialized = hr >= 0;

                result = ShowDialog(initialPath, title);
            }
            catch (Exception ex)
            {
                result = FolderPickResult.Unavailable("Не удалось открыть диалог: " + ex.Message);
            }
            finally
            {
                if (comInitialized)
                {
                    try { CoUninitialize(); } catch { /* поток завершается */ }
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // Диалог ждёт пользователя; ограничиваем ожидание, чтобы запрос не висел вечно.
        if (!thread.Join(TimeSpan.FromMinutes(3)))
        {
            return FolderPickResult.Unavailable(
                "Диалог выбора папки не был закрыт. Проверьте, не открыт ли он за другими окнами.");
        }

        return result;
    }

    private static FolderPickResult ShowDialog(string? initialPath, string title)
    {
        var displayName = new StringBuilder(260);

        var info = new BROWSEINFO
        {
            hwndOwner = IntPtr.Zero,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = Marshal.AllocHGlobal(260 * sizeof(char)),
            lpszTitle = string.IsNullOrWhiteSpace(title) ? "Выберите папку" : title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX | BIF_SHAREABLE,
            lpfn = IntPtr.Zero,
            lParam = IntPtr.Zero,
            iImage = 0
        };

        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            itemIdList = SHBrowseForFolder(ref info);

            if (itemIdList == IntPtr.Zero)
            {
                // Пользователь нажал «Отмена».
                return FolderPickResult.Cancelled();
            }

            if (!SHGetPathFromIDList(itemIdList, displayName))
            {
                return FolderPickResult.Unavailable("Не удалось получить путь выбранной папки.");
            }

            var path = displayName.ToString();
            return string.IsNullOrWhiteSpace(path)
                ? FolderPickResult.Cancelled()
                : FolderPickResult.Ok(path);
        }
        finally
        {
            if (itemIdList != IntPtr.Zero)
            {
                CoTaskMemFree(itemIdList);
            }

            if (info.pszDisplayName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(info.pszDisplayName);
            }
        }
    }

    /// <summary>
    /// Начальный каталог для диалога: указанный путь, его существующий родитель
    /// или корень диска.
    /// </summary>
    public static string? ResolveStartPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());

            if (Directory.Exists(full))
            {
                return full;
            }

            var parent = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(parent))
            {
                if (Directory.Exists(parent))
                {
                    return parent;
                }

                parent = Path.GetDirectoryName(parent);
            }

            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root) ? root : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Результат выбора папки.</summary>
public sealed record FolderPickResult(bool Success, string? Path, bool WasCancelled, string? Error)
{
    public static FolderPickResult Ok(string path) => new(true, path, false, null);

    public static FolderPickResult Cancelled() => new(false, null, true, null);

    public static FolderPickResult Unavailable(string error) => new(false, null, false, error);
}
