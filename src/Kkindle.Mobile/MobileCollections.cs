using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile;

public enum CollectionChange
{
    Updated,
    Removed
}

/// <summary>
/// Collection (收藏夹) management shared by the library's folder view and a
/// folder's own page. Wording follows the desktop app; the built-in “未收藏”
/// folder is filled automatically and never offered for editing.
/// </summary>
public static class MobileCollections
{
    public static bool IsUncollected(BookCollection collection) =>
        string.Equals(collection.Name, BookLibraryDefaults.UncollectedCollectionName, StringComparison.Ordinal);

    public static string DisplayName(BookCollection collection) =>
        IsUncollected(collection) ? "未收藏" : collection.Name;

    public static void PromptCreate(MobileShell shell, ApplicationModules modules, Func<BookCollection, Task> created)
    {
        shell.ShowSheet("新建收藏夹", new PromptSheet(null, "创建", async name =>
        {
            try
            {
                var collection = await modules.Library.Books.CreateCollectionAsync(name);
                shell.HideSheet();
                await created(collection);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                shell.ShowToast("创建收藏夹失败：" + UiText.Localize(exception.Message));
            }
        }, placeholder: "收藏夹名称"));
    }

    /// <summary>The folder menu; <paramref name="open"/> adds an “open” row for the library's long press.</summary>
    public static async Task ShowActionsAsync(
        MobileShell shell,
        ApplicationModules modules,
        BookCollection collection,
        Func<CollectionChange, Task> changed,
        Action? open = null)
    {
        if (IsUncollected(collection)) return;
        IReadOnlyList<BookCollection> all;
        try
        {
            all = await modules.Library.Books.GetCollectionsAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            shell.ShowToast("读取收藏夹失败：" + UiText.Localize(exception.Message));
            return;
        }
        var targets = all.Where(item => item.Id != collection.Id && !IsUncollected(item)).ToList();

        var items = new List<ChoiceItem>();
        if (open is not null) items.Add(new ChoiceItem("打开收藏夹", Tag: "open"));
        items.Add(new ChoiceItem("重命名", Tag: "rename"));
        items.Add(new ChoiceItem("清空书籍", Tag: "clear"));
        if (targets.Count > 0) items.Add(new ChoiceItem("合并到…", Tag: "merge"));
        items.Add(new ChoiceItem("解散收藏夹", Tag: "dissolve"));

        shell.ShowSheet(collection.Name, new ChoiceSheet(items, choice =>
        {
            switch (choice.Tag as string)
            {
                case "open":
                    shell.HideSheet();
                    open?.Invoke();
                    break;
                case "rename":
                    PromptRename(shell, modules, collection, changed);
                    break;
                case "clear":
                    _ = ClearAsync(shell, modules, collection, changed);
                    break;
                case "merge":
                    PickMergeTarget(shell, modules, collection, targets, changed);
                    break;
                case "dissolve":
                    _ = DissolveAsync(shell, modules, collection, changed);
                    break;
            }
        }));
    }

    private static void PromptRename(
        MobileShell shell, ApplicationModules modules, BookCollection collection, Func<CollectionChange, Task> changed)
    {
        shell.ShowSheet("重命名收藏夹", new PromptSheet(null, "保存", async name =>
        {
            try
            {
                await modules.Library.Books.RenameCollectionAsync(collection.Id, name);
                collection.Name = name;
                shell.HideSheet();
                shell.ShowToast($"已重命名为「{name}」");
                await changed(CollectionChange.Updated);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                shell.ShowToast("重命名失败：" + UiText.Localize(exception.Message));
            }
        }, initialText: collection.Name, placeholder: "收藏夹名称"));
    }

    private static async Task ClearAsync(
        MobileShell shell, ApplicationModules modules, BookCollection collection, Func<CollectionChange, Task> changed)
    {
        if (!await shell.ConfirmAsync("清空收藏夹", $"确定清空「{collection.Name}」吗？收藏夹本身会保留，书籍仍在书库中。", "清空"))
            return;
        await RunAsync(shell, "清空", async () =>
        {
            await modules.Library.Books.ClearCollectionAsync(collection.Id);
            shell.ShowToast($"已清空「{collection.Name}」");
            await changed(CollectionChange.Updated);
        });
    }

    private static void PickMergeTarget(
        MobileShell shell,
        ApplicationModules modules,
        BookCollection source,
        IReadOnlyList<BookCollection> targets,
        Func<CollectionChange, Task> changed)
    {
        var items = targets.Select(target => new ChoiceItem(target.Name, Tag: target)).ToList();
        shell.ShowSheet("合并到", new ChoiceSheet(items, choice =>
        {
            if (choice.Tag is BookCollection target) _ = MergeAsync(shell, modules, source, target, changed);
        }));
    }

    private static async Task MergeAsync(
        MobileShell shell,
        ApplicationModules modules,
        BookCollection source,
        BookCollection target,
        Func<CollectionChange, Task> changed)
    {
        if (!await shell.ConfirmAsync(
                "合并收藏夹", $"确定将「{source.Name}」合并到「{target.Name}」吗？「{source.Name}」将被解散。", "合并"))
            return;
        await RunAsync(shell, "合并", async () =>
        {
            await modules.Library.Books.MergeCollectionsAsync(source.Id, target.Id);
            shell.ShowToast($"已合并到「{target.Name}」");
            await changed(CollectionChange.Removed);
        });
    }

    private static async Task DissolveAsync(
        MobileShell shell, ApplicationModules modules, BookCollection collection, Func<CollectionChange, Task> changed)
    {
        if (!await shell.ConfirmAsync("解散收藏夹", $"确定解散「{collection.Name}」吗？书籍会保留在书库中。", "解散"))
            return;
        await RunAsync(shell, "解散", async () =>
        {
            await modules.Library.Books.DissolveCollectionAsync(collection.Id);
            shell.ShowToast($"已解散「{collection.Name}」");
            await changed(CollectionChange.Removed);
        });
    }

    private static async Task RunAsync(MobileShell shell, string verb, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            shell.ShowToast($"{verb}失败：" + UiText.Localize(exception.Message));
        }
    }
}
